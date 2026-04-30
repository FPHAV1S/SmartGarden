using Npgsql;
using System.Text.Json;

namespace IrrigationSystem.Worker;

public class DatabaseService
{
    private readonly string ConnectionString;
    private readonly ILogger<DatabaseService> Logger;

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        ConnectionString = connectionString;
        Logger = logger;
    }

    public async Task InsertSensorReadingAsync(int zoneId, float? moisture, float? temperature,
                                               float? humidity)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO sensor_readings (zone_id, moisture, temperature, humidity)
                        VALUES (@zone_id, @moisture, @temperature, @humidity)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("zone_id", zoneId);
            cmd.Parameters.AddWithValue("moisture", (object?)moisture ?? DBNull.Value);
            cmd.Parameters.AddWithValue("temperature", (object?)temperature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("humidity", (object?)humidity ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
            Logger.LogInformation("Inserted sensor reading for zone {ZoneId}", zoneId);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to insert sensor reading for zone {ZoneId}", zoneId);
        }
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
}