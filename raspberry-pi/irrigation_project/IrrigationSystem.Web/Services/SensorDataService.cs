using Npgsql;
using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public class SensorDataService
{
    private readonly string ConnectionString;
    private readonly ILogger<SensorDataService> Logger;
    private readonly SemaphoreSlim AiSchemaLock = new(1, 1);
    private bool AiSchemaEnsured;

    public SensorDataService(string connectionString, ILogger<SensorDataService> logger)
    {
        ConnectionString = connectionString;
        Logger = logger;
    }

    public async Task<List<SensorReading>> GetLatestReadingsAsync()
    {
        var readings = new List<SensorReading>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"
                SELECT DISTINCT ON (zone_id)
                    id, zone_id, moisture, temperature, humidity, recorded_at
                FROM sensor_readings
                ORDER BY zone_id, recorded_at DESC";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                readings.Add(new SensorReading
                {
                    Id = reader.GetInt32(0),
                    ZoneId = reader.GetInt32(1),
                    Moisture = reader.IsDBNull(2) ? null : reader.GetFloat(2),
                    Temperature = reader.IsDBNull(3) ? null : reader.GetFloat(3),
                    Humidity = reader.IsDBNull(4) ? null : reader.GetFloat(4),
                    RecordedAt = reader.GetDateTime(5)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get latest sensor readings");
        }

        return readings;
    }

    public async Task<List<SensorReading>> GetZoneHistoryAsync(int zoneId, int hours = 24)
    {
        var readings = new List<SensorReading>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = $@"
                SELECT id, zone_id, moisture, temperature, humidity, recorded_at
                FROM sensor_readings
                WHERE zone_id = @zone_id 
                AND recorded_at >= NOW() - INTERVAL '{hours} hours'
                ORDER BY recorded_at ASC";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("zone_id", zoneId);

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                readings.Add(new SensorReading
                {
                    Id = reader.GetInt32(0),
                    ZoneId = reader.GetInt32(1),
                    Moisture = reader.IsDBNull(2) ? null : reader.GetFloat(2),
                    Temperature = reader.IsDBNull(3) ? null : reader.GetFloat(3),
                    Humidity = reader.IsDBNull(4) ? null : reader.GetFloat(4),
                    RecordedAt = reader.GetDateTime(5)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get zone history for zone {ZoneId}", zoneId);
        }

        return readings;
    }

    public async Task<List<Zone>> GetZonesAsync()
    {
        var zones = new List<Zone>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = "SELECT id, name, plant_type, moisture_threshold, is_active FROM zones ORDER BY id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                zones.Add(new Zone
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    PlantType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    MoistureThreshold = reader.GetFloat(3),
                    IsActive = reader.GetBoolean(4)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get zones");
        }

        return zones;
    }

    public async Task<Zone?> GetZoneAsync(int zoneId)
    {
        var zones = await GetZonesAsync();
        return zones.FirstOrDefault(zone => zone.Id == zoneId);
    }

    public async Task<List<Zone>> EnsureDefaultZonesAsync()
    {
        var zones = await GetZonesAsync();
        if (zones.Count > 0)
        {
            return zones;
        }

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"
                INSERT INTO zones (name, plant_type, moisture_threshold, is_active)
                VALUES
                    ('Zone 1', 'Tomatoes', 30.0, true),
                    ('Zone 2', 'Herbs', 35.0, true),
                    ('Zone 3', 'Lettuce', 40.0, true)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            Logger.LogInformation("Created default irrigation zones");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create default zones");
        }

        return await GetZonesAsync();
    }

    public async Task<int> StartIrrigationEventAsync(int zoneId, string triggerReason, float? moistureBefore)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO irrigation_events (zone_id, trigger_reason, moisture_before) 
                        VALUES (@zone_id, @trigger_reason, @moisture_before) 
                        RETURNING id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("zone_id", zoneId);
            cmd.Parameters.AddWithValue("trigger_reason", triggerReason);
            cmd.Parameters.AddWithValue("moisture_before", (object?)moistureBefore ?? DBNull.Value);

            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start irrigation event");
            return -1;
        }
    }

    public async Task EndIrrigationEventAsync(int eventId, int durationSec, float? moistureAfter)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"UPDATE irrigation_events 
                        SET ended_at = NOW(), 
                            duration_sec = @duration_sec, 
                            moisture_after = @moisture_after 
                        WHERE id = @id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", eventId);
            cmd.Parameters.AddWithValue("duration_sec", durationSec);
            cmd.Parameters.AddWithValue("moisture_after", (object?)moistureAfter ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to end irrigation event");
        }
    }
    public async Task<bool> UpdateZoneAsync(int zoneId, string name, string? plantType, float moistureThreshold, bool isActive)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"UPDATE zones 
                        SET name = @name, 
                            plant_type = @plant_type, 
                            moisture_threshold = @moisture_threshold, 
                            is_active = @is_active 
                        WHERE id = @id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", zoneId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("plant_type", (object?)plantType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("moisture_threshold", moistureThreshold);
            cmd.Parameters.AddWithValue("is_active", isActive);

            await cmd.ExecuteNonQueryAsync();
            Logger.LogInformation("Updated zone {ZoneId}", zoneId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update zone {ZoneId}", zoneId);
            return false;
        }
    }
    public async Task<List<SystemLog>> GetRecentLogsAsync(int count = 20)
    {
        var logs = new List<SystemLog>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, level, message, logged_at 
                        FROM system_logs 
                        ORDER BY logged_at DESC 
                        LIMIT @count";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("count", count);

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                logs.Add(new SystemLog
                {
                    Id = reader.GetInt32(0),
                    Level = reader.GetString(1),
                    Message = reader.GetString(2),
                    LoggedAt = reader.GetDateTime(3)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get system logs");
        }

        return logs;
    }
    public async Task<List<IrrigationEvent>> GetRecentIrrigationEventsAsync(int zoneId, int count)
    {
        var events = new List<IrrigationEvent>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, zone_id, started_at, ended_at, duration_sec, trigger_reason, moisture_before, moisture_after
                        FROM irrigation_events
                        WHERE zone_id = @zone_id 
                        ORDER BY started_at DESC
                        LIMIT @count";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("zone_id", zoneId);
            cmd.Parameters.AddWithValue("count", count);

            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                events.Add(new IrrigationEvent
                {
                    Id = reader.GetInt32(0),
                    ZoneId = reader.GetInt32(1),
                    StartedAt = reader.GetDateTime(2),
                    EndedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    DurationSec = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    TriggerReason = reader.IsDBNull(5) ? null : reader.GetString(5),
                    MoistureBefore = reader.IsDBNull(6) ? null : reader.GetFloat(6),
                    MoistureAfter = reader.IsDBNull(7) ? null : reader.GetFloat(7)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get irrigation events");
        }

        return events;
    }

    public async Task LogSystemMessageAsync(string level, string message)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO system_logs (level, message) VALUES (@level, @message)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("level", level);
            cmd.Parameters.AddWithValue("message", message);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to log system message");
        }
    }
    public async Task<SystemSettings?> GetSystemSettingsAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, auto_watering_enabled, system_mode, default_watering_duration, 
                        night_mode_enabled, night_mode_start_hour, night_mode_end_hour, eco_mode_enabled 
                        FROM system_settings ORDER BY id DESC LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new SystemSettings
                {
                    Id = reader.GetInt32(0),
                    AutoWateringEnabled = reader.GetBoolean(1),
                    SystemMode = reader.GetString(2),
                    DefaultWateringDuration = reader.GetInt32(3),
                    NightModeEnabled = reader.GetBoolean(4),
                    NightModeStartHour = reader.GetInt32(5),
                    NightModeEndHour = reader.GetInt32(6),
                    EcoModeEnabled = reader.GetBoolean(7)
                };
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get system settings");
        }

        return null;
    }

    public async Task<AiIrrigationDecisionLog?> GetLatestAiDecisionAsync()
    {
        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, timestamp, sensor_reading_id, zone_id, moisture_percent, ai_was_attempted,
                            ai_should_water, final_should_water_after_safety, recommended_valve_state,
                            final_valve_state, recommended_duration_seconds, confidence, reason,
                            learned_observation, suggested_moisture_threshold, risk_level, was_fallback_used,
                            error_message, safety_notes
                        FROM ai_irrigation_decision_logs
                        ORDER BY timestamp DESC
                        LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            return await reader.ReadAsync() ? ReadAiDecisionLog(reader) : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get latest AI irrigation decision");
            return null;
        }
    }

    public async Task<AiIrrigationOptions> GetAiIrrigationOptionsAsync(AiIrrigationOptions defaults)
    {
        var normalizedDefaults = NormalizeAiIrrigationOptions(defaults);

        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            await EnsureAiSettingsRowAsync(conn, normalizedDefaults);

            var sql = @"SELECT enable_ai_decision_making, model, api_key_environment_variable,
                            minimum_minutes_between_ai_calls, recent_history_hours, recent_watering_events,
                            pattern_memory_count, high_moisture_block_percent, max_duration_seconds,
                            low_confidence_threshold, minimum_minutes_between_waterings,
                            default_fallback_duration_seconds
                        FROM ai_irrigation_settings
                        WHERE id = 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            return await reader.ReadAsync()
                ? ReadAiIrrigationOptions(reader)
                : normalizedDefaults;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get AI irrigation settings");
            return normalizedDefaults;
        }
    }

    public async Task<bool> UpdateAiIrrigationOptionsAsync(AiIrrigationOptions settings)
    {
        var normalized = NormalizeAiIrrigationOptions(settings);

        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO ai_irrigation_settings (
                            id,
                            enable_ai_decision_making,
                            model,
                            api_key_environment_variable,
                            minimum_minutes_between_ai_calls,
                            recent_history_hours,
                            recent_watering_events,
                            pattern_memory_count,
                            high_moisture_block_percent,
                            max_duration_seconds,
                            low_confidence_threshold,
                            minimum_minutes_between_waterings,
                            default_fallback_duration_seconds,
                            updated_at)
                        VALUES (
                            1,
                            @enable_ai,
                            @model,
                            @api_key_env,
                            @minimum_ai_interval,
                            @recent_history_hours,
                            @recent_watering_events,
                            @pattern_memory_count,
                            @high_moisture_block,
                            @max_duration_seconds,
                            @low_confidence_threshold,
                            @minimum_watering_interval,
                            @fallback_duration,
                            NOW())
                        ON CONFLICT (id) DO UPDATE
                        SET enable_ai_decision_making = EXCLUDED.enable_ai_decision_making,
                            model = EXCLUDED.model,
                            api_key_environment_variable = EXCLUDED.api_key_environment_variable,
                            minimum_minutes_between_ai_calls = EXCLUDED.minimum_minutes_between_ai_calls,
                            recent_history_hours = EXCLUDED.recent_history_hours,
                            recent_watering_events = EXCLUDED.recent_watering_events,
                            pattern_memory_count = EXCLUDED.pattern_memory_count,
                            high_moisture_block_percent = EXCLUDED.high_moisture_block_percent,
                            max_duration_seconds = EXCLUDED.max_duration_seconds,
                            low_confidence_threshold = EXCLUDED.low_confidence_threshold,
                            minimum_minutes_between_waterings = EXCLUDED.minimum_minutes_between_waterings,
                            default_fallback_duration_seconds = EXCLUDED.default_fallback_duration_seconds,
                            updated_at = NOW()";

            await using var cmd = new NpgsqlCommand(sql, conn);
            AddAiSettingsParameters(cmd, normalized);

            await cmd.ExecuteNonQueryAsync();

            await LogSystemMessageAsync(
                "INFO",
                $"AI settings updated: Enabled={normalized.EnableAiDecisionMaking}, Model={normalized.Model}, Cooldown={normalized.MinimumMinutesBetweenAiCalls}m");

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update AI irrigation settings");
            return false;
        }
    }

    public async Task<AiIrrigationDecisionLog?> GetLatestAttemptedAiDecisionAsync()
    {
        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, timestamp, sensor_reading_id, zone_id, moisture_percent, ai_was_attempted,
                            ai_should_water, final_should_water_after_safety, recommended_valve_state,
                            final_valve_state, recommended_duration_seconds, confidence, reason,
                            learned_observation, suggested_moisture_threshold, risk_level, was_fallback_used,
                            error_message, safety_notes
                        FROM ai_irrigation_decision_logs
                        WHERE ai_was_attempted = true
                        ORDER BY timestamp DESC
                        LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            return await reader.ReadAsync() ? ReadAiDecisionLog(reader) : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get latest attempted AI irrigation decision");
            return null;
        }
    }

    public async Task<List<AiIrrigationDecisionLog>> GetAiDecisionHistoryAsync(int count = 20)
    {
        var decisions = new List<AiIrrigationDecisionLog>();

        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, timestamp, sensor_reading_id, zone_id, moisture_percent, ai_was_attempted,
                            ai_should_water, final_should_water_after_safety, recommended_valve_state,
                            final_valve_state, recommended_duration_seconds, confidence, reason,
                            learned_observation, suggested_moisture_threshold, risk_level, was_fallback_used,
                            error_message, safety_notes
                        FROM ai_irrigation_decision_logs
                        ORDER BY timestamp DESC
                        LIMIT @count";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("count", Math.Clamp(count, 1, 200));
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                decisions.Add(ReadAiDecisionLog(reader));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get AI irrigation decision history");
        }

        return decisions;
    }

    public async Task<List<PatternMemory>> GetRecentPatternMemoriesAsync(int count = 5)
    {
        var memories = new List<PatternMemory>();

        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, created_at, summary, suggested_threshold, average_moisture, notes
                        FROM pattern_memory
                        ORDER BY created_at DESC
                        LIMIT @count";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("count", Math.Clamp(count, 1, 50));
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                memories.Add(new PatternMemory
                {
                    Id = reader.GetInt32(0),
                    CreatedAt = reader.GetDateTime(1),
                    Summary = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    SuggestedThreshold = reader.IsDBNull(3) ? null : Convert.ToSingle(reader.GetDouble(3)),
                    AverageMoisture = reader.IsDBNull(4) ? null : Convert.ToSingle(reader.GetDouble(4)),
                    Notes = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get pattern memories");
        }

        return memories;
    }

    public async Task InsertAiDecisionLogAsync(IrrigationDecisionResult result)
    {
        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO ai_irrigation_decision_logs (
                            timestamp,
                            sensor_reading_id,
                            zone_id,
                            moisture_percent,
                            ai_was_attempted,
                            ai_should_water,
                            final_should_water_after_safety,
                            recommended_valve_state,
                            final_valve_state,
                            recommended_duration_seconds,
                            confidence,
                            reason,
                            learned_observation,
                            suggested_moisture_threshold,
                            risk_level,
                            was_fallback_used,
                            error_message,
                            safety_notes)
                        VALUES (
                            @timestamp,
                            @sensor_reading_id,
                            @zone_id,
                            @moisture_percent,
                            @ai_was_attempted,
                            @ai_should_water,
                            @final_should_water_after_safety,
                            @recommended_valve_state,
                            @final_valve_state,
                            @recommended_duration_seconds,
                            @confidence,
                            @reason,
                            @learned_observation,
                            @suggested_moisture_threshold,
                            @risk_level,
                            @was_fallback_used,
                            @error_message,
                            @safety_notes)";

            var aiDecision = result.AiDecision;
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("timestamp", result.Timestamp);
            cmd.Parameters.AddWithValue("sensor_reading_id", result.SensorReadingId > 0 ? (object)result.SensorReadingId : DBNull.Value);
            cmd.Parameters.AddWithValue("zone_id", result.ZoneId);
            cmd.Parameters.AddWithValue("moisture_percent", (object?)result.MoisturePercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ai_was_attempted", result.AiWasAttempted);
            cmd.Parameters.AddWithValue("ai_should_water", aiDecision?.ShouldWater ?? false);
            cmd.Parameters.AddWithValue("final_should_water_after_safety", result.FinalShouldWaterAfterSafety);
            cmd.Parameters.AddWithValue("recommended_valve_state", aiDecision?.RecommendedValveState ?? "OFF");
            cmd.Parameters.AddWithValue("final_valve_state", result.RecommendedValveState);
            cmd.Parameters.AddWithValue("recommended_duration_seconds", aiDecision?.RecommendedDurationSeconds ?? result.RecommendedDurationSeconds);
            cmd.Parameters.AddWithValue("confidence", result.Confidence);
            cmd.Parameters.AddWithValue("reason", result.Reason);
            cmd.Parameters.AddWithValue("learned_observation", result.LearnedObservation);
            cmd.Parameters.AddWithValue("suggested_moisture_threshold", (object?)result.SuggestedMoistureThreshold ?? DBNull.Value);
            cmd.Parameters.AddWithValue("risk_level", result.RiskLevel);
            cmd.Parameters.AddWithValue("was_fallback_used", result.WasFallbackUsed);
            cmd.Parameters.AddWithValue("error_message", (object?)result.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("safety_notes", result.SafetyNotes);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to insert AI irrigation decision log for zone {ZoneId}", result.ZoneId);
        }
    }

    public async Task InsertPatternMemoryAsync(string summary, float? suggestedThreshold, float? averageMoisture, string? notes)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        try
        {
            await EnsureAiTablesAsync();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO pattern_memory (summary, suggested_threshold, average_moisture, notes)
                        VALUES (@summary, @suggested_threshold, @average_moisture, @notes)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("summary", summary);
            cmd.Parameters.AddWithValue("suggested_threshold", (object?)suggestedThreshold ?? DBNull.Value);
            cmd.Parameters.AddWithValue("average_moisture", (object?)averageMoisture ?? DBNull.Value);
            cmd.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to insert pattern memory");
        }
    }

    public async Task<int> CleanOldDataAsync(int daysToKeep)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = $@"DELETE FROM sensor_readings 
                        WHERE recorded_at < NOW() - INTERVAL '{daysToKeep} days'";

            await using var cmd = new NpgsqlCommand(sql, conn);
            var deleted = await cmd.ExecuteNonQueryAsync();

            await LogSystemMessageAsync("INFO", $"Database cleanup: {deleted} old sensor readings deleted");
            
            return deleted;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clean old data");
            return 0;
        }
    }
    public async Task<DisplaySettings?> GetDisplaySettingsAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = "SELECT mode, selected_zone_id, refresh_interval FROM display_settings ORDER BY id DESC LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new DisplaySettings
                {
                    Mode = reader.GetString(0),
                    SelectedZoneId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    RefreshInterval = reader.GetInt32(2)
                };
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get display settings");
        }

        return null;
    }

    public async Task<bool> UpdateSystemSettingsAsync(SystemSettings settings)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"UPDATE system_settings 
                        SET auto_watering_enabled = @auto_watering,
                            system_mode = @mode,
                            default_watering_duration = @duration,
                            night_mode_enabled = @night_mode,
                            night_mode_start_hour = @night_start,
                            night_mode_end_hour = @night_end,
                            eco_mode_enabled = @eco_mode,
                            updated_at = NOW()";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("auto_watering", settings.AutoWateringEnabled);
            cmd.Parameters.AddWithValue("mode", settings.SystemMode);
            cmd.Parameters.AddWithValue("duration", settings.DefaultWateringDuration);
            cmd.Parameters.AddWithValue("night_mode", settings.NightModeEnabled);
            cmd.Parameters.AddWithValue("night_start", settings.NightModeStartHour);
            cmd.Parameters.AddWithValue("night_end", settings.NightModeEndHour);
            cmd.Parameters.AddWithValue("eco_mode", settings.EcoModeEnabled);

            await cmd.ExecuteNonQueryAsync();
            
            var statusParts = new List<string>();
            statusParts.Add($"AutoWater={settings.AutoWateringEnabled}");
            if (settings.NightModeEnabled)
                statusParts.Add($"NightMode={settings.NightModeStartHour}:00-{settings.NightModeEndHour}:00");
            if (settings.EcoModeEnabled)
                statusParts.Add("EcoMode=ON");
            statusParts.Add($"Duration={settings.DefaultWateringDuration}s");
            
            await LogSystemMessageAsync("INFO", $"Settings updated: {string.Join(", ", statusParts)}");
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update system settings");
            return false;
        }
    }
    public async Task<SensorReading> InsertSensorReadingAsync(int zoneId, float moisture, float temperature, float humidity)
    {
        try
        {   
            using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO sensor_readings (zone_id, moisture, temperature, humidity, recorded_at)
                VALUES (@zone_id, @moisture, @temperature, @humidity, NOW())
                RETURNING id, recorded_at";

            using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("zone_id", zoneId);
            command.Parameters.AddWithValue("moisture", moisture);
            command.Parameters.AddWithValue("temperature", temperature);
            command.Parameters.AddWithValue("humidity", humidity);

            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new SensorReading
                {
                    Id = reader.GetInt32(0),
                    ZoneId = zoneId,
                    Moisture = moisture,
                    Temperature = temperature,
                    Humidity = humidity,
                    RecordedAt = reader.GetDateTime(1)
                };
            }

            throw new InvalidOperationException("Sensor reading insert did not return a row.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error inserting sensor reading for zone {ZoneId}", zoneId);
            throw;
        }
    }

    private async Task EnsureAiTablesAsync()
    {
        if (AiSchemaEnsured)
        {
            return;
        }

        await AiSchemaLock.WaitAsync();
        try
        {
            if (AiSchemaEnsured)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS ai_irrigation_decision_logs (
                    id SERIAL PRIMARY KEY,
                    timestamp timestamp without time zone DEFAULT now() NOT NULL,
                    sensor_reading_id integer REFERENCES sensor_readings(id) ON DELETE SET NULL,
                    zone_id integer NOT NULL REFERENCES zones(id) ON DELETE CASCADE,
                    moisture_percent double precision,
                    ai_was_attempted boolean DEFAULT false NOT NULL,
                    ai_should_water boolean DEFAULT false NOT NULL,
                    final_should_water_after_safety boolean DEFAULT false NOT NULL,
                    recommended_valve_state character varying(3) DEFAULT 'OFF' NOT NULL,
                    final_valve_state character varying(3) DEFAULT 'OFF' NOT NULL,
                    recommended_duration_seconds integer DEFAULT 0 NOT NULL,
                    confidence double precision DEFAULT 0 NOT NULL,
                    reason text,
                    learned_observation text,
                    suggested_moisture_threshold double precision,
                    risk_level character varying(20) DEFAULT 'LOW' NOT NULL,
                    was_fallback_used boolean DEFAULT true NOT NULL,
                    error_message text,
                    safety_notes text
                );

                CREATE INDEX IF NOT EXISTS idx_ai_irrigation_decision_logs_timestamp
                    ON ai_irrigation_decision_logs (timestamp DESC);

                CREATE INDEX IF NOT EXISTS idx_ai_irrigation_decision_logs_zone_timestamp
                    ON ai_irrigation_decision_logs (zone_id, timestamp DESC);

                CREATE TABLE IF NOT EXISTS pattern_memory (
                    id SERIAL PRIMARY KEY,
                    created_at timestamp without time zone DEFAULT now() NOT NULL,
                    summary text NOT NULL,
                    suggested_threshold double precision,
                    average_moisture double precision,
                    notes text
                );

                CREATE INDEX IF NOT EXISTS idx_pattern_memory_created_at
                    ON pattern_memory (created_at DESC);

                CREATE TABLE IF NOT EXISTS ai_irrigation_settings (
                    id integer PRIMARY KEY DEFAULT 1,
                    enable_ai_decision_making boolean DEFAULT false NOT NULL,
                    model character varying(100) DEFAULT 'gpt-4.1-nano' NOT NULL,
                    api_key_environment_variable character varying(100) DEFAULT 'OPENAI_API_KEY' NOT NULL,
                    minimum_minutes_between_ai_calls integer DEFAULT 15 NOT NULL,
                    recent_history_hours integer DEFAULT 24 NOT NULL,
                    recent_watering_events integer DEFAULT 10 NOT NULL,
                    pattern_memory_count integer DEFAULT 5 NOT NULL,
                    high_moisture_block_percent double precision DEFAULT 60 NOT NULL,
                    max_duration_seconds integer DEFAULT 120 NOT NULL,
                    low_confidence_threshold double precision DEFAULT 0.65 NOT NULL,
                    minimum_minutes_between_waterings integer DEFAULT 120 NOT NULL,
                    default_fallback_duration_seconds integer DEFAULT 10 NOT NULL,
                    updated_at timestamp without time zone DEFAULT now() NOT NULL,
                    CONSTRAINT ai_irrigation_settings_singleton CHECK (id = 1)
                );";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            AiSchemaEnsured = true;
        }
        finally
        {
            AiSchemaLock.Release();
        }
    }

    private static AiIrrigationOptions NormalizeAiIrrigationOptions(AiIrrigationOptions source)
    {
        return new AiIrrigationOptions
        {
            EnableAiDecisionMaking = source.EnableAiDecisionMaking,
            Model = NormalizeShortSetting(source.Model, "gpt-4.1-nano"),
            ApiKeyEnvironmentVariable = NormalizeApiKeyEnvironmentVariableName(source.ApiKeyEnvironmentVariable),
            MinimumMinutesBetweenAiCalls = Math.Clamp(source.MinimumMinutesBetweenAiCalls, 0, 1440),
            RecentHistoryHours = Math.Clamp(source.RecentHistoryHours, 1, 168),
            RecentWateringEvents = Math.Clamp(source.RecentWateringEvents, 1, 100),
            PatternMemoryCount = Math.Clamp(source.PatternMemoryCount, 1, 50),
            HighMoistureBlockPercent = Math.Clamp(source.HighMoistureBlockPercent, 0, 100),
            MaxDurationSeconds = Math.Clamp(source.MaxDurationSeconds, 1, 3600),
            LowConfidenceThreshold = Math.Clamp(source.LowConfidenceThreshold, 0, 1),
            MinimumMinutesBetweenWaterings = Math.Clamp(source.MinimumMinutesBetweenWaterings, 0, 1440),
            DefaultFallbackDurationSeconds = Math.Clamp(source.DefaultFallbackDurationSeconds, 1, 3600)
        };
    }

    private static string NormalizeShortSetting(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 100 ? trimmed : trimmed[..100];
    }

    private static string NormalizeApiKeyEnvironmentVariableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "OPENAI_API_KEY";
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 100 ||
            trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("sk_", StringComparison.OrdinalIgnoreCase))
        {
            return "OPENAI_API_KEY";
        }

        return trimmed;
    }

    private static async Task EnsureAiSettingsRowAsync(NpgsqlConnection conn, AiIrrigationOptions defaults)
    {
        var sql = @"INSERT INTO ai_irrigation_settings (
                        id,
                        enable_ai_decision_making,
                        model,
                        api_key_environment_variable,
                        minimum_minutes_between_ai_calls,
                        recent_history_hours,
                        recent_watering_events,
                        pattern_memory_count,
                        high_moisture_block_percent,
                        max_duration_seconds,
                        low_confidence_threshold,
                        minimum_minutes_between_waterings,
                        default_fallback_duration_seconds,
                        updated_at)
                    VALUES (
                        1,
                        @enable_ai,
                        @model,
                        @api_key_env,
                        @minimum_ai_interval,
                        @recent_history_hours,
                        @recent_watering_events,
                        @pattern_memory_count,
                        @high_moisture_block,
                        @max_duration_seconds,
                        @low_confidence_threshold,
                        @minimum_watering_interval,
                        @fallback_duration,
                        NOW())
                    ON CONFLICT (id) DO NOTHING";

        await using var cmd = new NpgsqlCommand(sql, conn);
        AddAiSettingsParameters(cmd, defaults);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AddAiSettingsParameters(NpgsqlCommand cmd, AiIrrigationOptions settings)
    {
        cmd.Parameters.AddWithValue("enable_ai", settings.EnableAiDecisionMaking);
        cmd.Parameters.AddWithValue("model", settings.Model);
        cmd.Parameters.AddWithValue("api_key_env", settings.ApiKeyEnvironmentVariable);
        cmd.Parameters.AddWithValue("minimum_ai_interval", settings.MinimumMinutesBetweenAiCalls);
        cmd.Parameters.AddWithValue("recent_history_hours", settings.RecentHistoryHours);
        cmd.Parameters.AddWithValue("recent_watering_events", settings.RecentWateringEvents);
        cmd.Parameters.AddWithValue("pattern_memory_count", settings.PatternMemoryCount);
        cmd.Parameters.AddWithValue("high_moisture_block", settings.HighMoistureBlockPercent);
        cmd.Parameters.AddWithValue("max_duration_seconds", settings.MaxDurationSeconds);
        cmd.Parameters.AddWithValue("low_confidence_threshold", settings.LowConfidenceThreshold);
        cmd.Parameters.AddWithValue("minimum_watering_interval", settings.MinimumMinutesBetweenWaterings);
        cmd.Parameters.AddWithValue("fallback_duration", settings.DefaultFallbackDurationSeconds);
    }

    private static AiIrrigationOptions ReadAiIrrigationOptions(NpgsqlDataReader reader)
    {
        return NormalizeAiIrrigationOptions(new AiIrrigationOptions
        {
            EnableAiDecisionMaking = reader.GetBoolean(0),
            Model = reader.GetString(1),
            ApiKeyEnvironmentVariable = reader.GetString(2),
            MinimumMinutesBetweenAiCalls = reader.GetInt32(3),
            RecentHistoryHours = reader.GetInt32(4),
            RecentWateringEvents = reader.GetInt32(5),
            PatternMemoryCount = reader.GetInt32(6),
            HighMoistureBlockPercent = Convert.ToSingle(reader.GetDouble(7)),
            MaxDurationSeconds = reader.GetInt32(8),
            LowConfidenceThreshold = reader.GetDouble(9),
            MinimumMinutesBetweenWaterings = reader.GetInt32(10),
            DefaultFallbackDurationSeconds = reader.GetInt32(11)
        });
    }

    private static AiIrrigationDecisionLog ReadAiDecisionLog(NpgsqlDataReader reader)
    {
        return new AiIrrigationDecisionLog
        {
            Id = reader.GetInt32(0),
            Timestamp = reader.GetDateTime(1),
            SensorReadingId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ZoneId = reader.GetInt32(3),
            MoisturePercent = reader.IsDBNull(4) ? null : Convert.ToSingle(reader.GetDouble(4)),
            AiWasAttempted = reader.GetBoolean(5),
            AiShouldWater = reader.GetBoolean(6),
            FinalShouldWaterAfterSafety = reader.GetBoolean(7),
            RecommendedValveState = reader.IsDBNull(8) ? "OFF" : reader.GetString(8),
            FinalValveState = reader.IsDBNull(9) ? "OFF" : reader.GetString(9),
            RecommendedDurationSeconds = reader.GetInt32(10),
            Confidence = reader.GetDouble(11),
            Reason = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            LearnedObservation = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            SuggestedMoistureThreshold = reader.IsDBNull(14) ? null : Convert.ToSingle(reader.GetDouble(14)),
            RiskLevel = reader.IsDBNull(15) ? "LOW" : reader.GetString(15),
            WasFallbackUsed = reader.GetBoolean(16),
            ErrorMessage = reader.IsDBNull(17) ? null : reader.GetString(17),
            SafetyNotes = reader.IsDBNull(18) ? string.Empty : reader.GetString(18)
        };
    }

}
