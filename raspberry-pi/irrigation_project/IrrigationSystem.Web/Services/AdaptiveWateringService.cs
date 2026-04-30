using Npgsql;
using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public class AdaptiveWateringService
{
    private readonly string ConnectionString;
    private readonly ILogger<AdaptiveWateringService> Logger;
    private readonly SensorDataService DataService;

    public AdaptiveWateringService(string connectionString, ILogger<AdaptiveWateringService> logger, SensorDataService dataService)
    {
        ConnectionString = connectionString;
        Logger = logger;
        DataService = dataService;
    }

    public async Task RunAdaptiveAnalysisAsync()
    {
        Logger.LogInformation("Starting adaptive watering analysis");

        var zones = await DataService.GetZonesAsync();

        foreach (var zone in zones)
        {
            if (!zone.IsActive) continue;

            await AnalyzeAndAdjustZoneAsync(zone);
        }

        Logger.LogInformation("Adaptive analysis completed");
    }

    private async Task AnalyzeAndAdjustZoneAsync(Zone zone)
    {
        var recentEvents = await GetRecentIrrigationEventsAsync(zone.Id, 10);

        if (recentEvents.Count < 3)
        {
            Logger.LogInformation("Zone {ZoneId}: Not enough data for analysis (need at least 3 events)", zone.Id);
            return;
        }

        var avgMoistureIncrease = recentEvents
            .Where(e => e.MoistureBefore.HasValue && e.MoistureAfter.HasValue)
            .Select(e => e.MoistureAfter!.Value - e.MoistureBefore!.Value)
            .DefaultIfEmpty(0)
            .Average();

        var avgTimeBetweenWaterings = CalculateAverageTimeBetweenWaterings(recentEvents);

        float newThreshold = zone.MoistureThreshold;
        string reason = "";

        if (avgMoistureIncrease < 5)
        {
            newThreshold = Math.Max(10, zone.MoistureThreshold - 2);
            reason = "Low moisture increase - lowering threshold to water earlier";
        }
        else if (avgMoistureIncrease > 20)
        {
            newThreshold = Math.Min(60, zone.MoistureThreshold + 2);
            reason = "High moisture increase - raising threshold to reduce watering frequency";
        }
        else if (avgTimeBetweenWaterings < 4)
        {
            newThreshold = Math.Min(60, zone.MoistureThreshold + 1);
            reason = "Watering too frequently - raising threshold";
        }
        else if (avgTimeBetweenWaterings > 48)
        {
            newThreshold = Math.Max(10, zone.MoistureThreshold - 1);
            reason = "Long time between waterings - lowering threshold";
        }

        if (Math.Abs(newThreshold - zone.MoistureThreshold) > 0.1)
        {
            await UpdateThresholdAsync(zone.Id, newThreshold, reason);
            Logger.LogInformation("Zone {ZoneId}: Adjusted threshold from {Old}% to {New}% - {Reason}", 
                zone.Id, zone.MoistureThreshold, newThreshold, reason);
        }
        else
        {
            Logger.LogInformation("Zone {ZoneId}: Threshold optimal at {Threshold}%", zone.Id, zone.MoistureThreshold);
        }
    }

    private async Task<List<IrrigationEvent>> GetRecentIrrigationEventsAsync(int zoneId, int count)
    {
        var events = new List<IrrigationEvent>();

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT id, zone_id, started_at, ended_at, duration_sec, trigger_reason, moisture_before, moisture_after
                        FROM irrigation_events
                        WHERE zone_id = @zone_id 
                        AND moisture_before IS NOT NULL 
                        AND moisture_after IS NOT NULL
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

    private double CalculateAverageTimeBetweenWaterings(List<IrrigationEvent> events)
    {
        if (events.Count < 2) return 24;

        var intervals = new List<double>();

        for (int i = 0; i < events.Count - 1; i++)
        {
            var interval = (events[i].StartedAt - events[i + 1].StartedAt).TotalHours;
            intervals.Add(interval);
        }

        return intervals.Average();
    }

    private async Task UpdateThresholdAsync(int zoneId, float newThreshold, string reason)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"UPDATE zones SET moisture_threshold = @threshold WHERE id = @id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", zoneId);
            cmd.Parameters.AddWithValue("threshold", newThreshold);

            await cmd.ExecuteNonQueryAsync();

            var logSql = @"INSERT INTO system_logs (level, message) VALUES (@level, @message)";
            await using var logCmd = new NpgsqlCommand(logSql, conn);
            logCmd.Parameters.AddWithValue("level", "INFO");
            logCmd.Parameters.AddWithValue("message", $"Zone {zoneId}: Adaptive threshold change to {newThreshold}% - {reason}");
            await logCmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update threshold");
        }
    }
}