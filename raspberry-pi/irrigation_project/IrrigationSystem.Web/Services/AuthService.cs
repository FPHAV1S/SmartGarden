using Npgsql;
using BCrypt.Net;

namespace IrrigationSystem.Web.Services;

public class AuthService
{
    private readonly string ConnectionString;
    private readonly ILogger<AuthService> Logger;
    private const int MaxFailedAttempts = 3;
    private const int BlockDurationMinutes = 30;

    public AuthService(string connectionString, ILogger<AuthService> logger)
    {
        ConnectionString = connectionString;
        Logger = logger;
    }

    public async Task<bool> ValidateLoginAsync(string username, string password, string ipAddress)
    {
        if (await IsIpBlockedAsync(ipAddress))
        {
            Logger.LogWarning("Login attempt from blocked IP: {Ip}", ipAddress);
            return false;
        }

        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = "SELECT password_hash FROM users WHERE username = @username";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("username", username);

            var passwordHash = await cmd.ExecuteScalarAsync() as string;

            if (passwordHash == null)
            {
                await LogLoginAttemptAsync(ipAddress, username, false);
                return false;
            }

            var isValid = BCrypt.Net.BCrypt.Verify(password, passwordHash);

            await LogLoginAttemptAsync(ipAddress, username, isValid);

            if (isValid)
            {
                await ClearFailedAttemptsAsync(ipAddress);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error validating login");
            return false;
        }
    }

    private async Task<bool> IsIpBlockedAsync(string ipAddress)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT COUNT(*) FROM login_attempts 
                        WHERE ip_address = @ip 
                        AND success = false 
                        AND attempted_at > NOW() - INTERVAL '@minutes minutes'";

            var cmdText = sql.Replace("@minutes", BlockDurationMinutes.ToString());
            await using var cmd = new NpgsqlCommand(cmdText, conn);
            cmd.Parameters.AddWithValue("ip", ipAddress);

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return count >= MaxFailedAttempts;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error checking IP block status");
            return false;
        }
    }

    private async Task LogLoginAttemptAsync(string ipAddress, string username, bool success)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"INSERT INTO login_attempts (ip_address, username, success) 
                        VALUES (@ip, @username, @success)";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ip", ipAddress);
            cmd.Parameters.AddWithValue("username", username);
            cmd.Parameters.AddWithValue("success", success);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error logging login attempt");
        }
    }

    private async Task ClearFailedAttemptsAsync(string ipAddress)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = "DELETE FROM login_attempts WHERE ip_address = @ip AND success = false";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ip", ipAddress);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing failed attempts");
        }
    }

    public async Task<int> GetRemainingAttemptsAsync(string ipAddress)
    {
        try
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            var sql = @"SELECT COUNT(*) FROM login_attempts 
                        WHERE ip_address = @ip 
                        AND success = false 
                        AND attempted_at > NOW() - INTERVAL '@minutes minutes'";

            var cmdText = sql.Replace("@minutes", BlockDurationMinutes.ToString());
            await using var cmd = new NpgsqlCommand(cmdText, conn);
            cmd.Parameters.AddWithValue("ip", ipAddress);

            var failedCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return Math.Max(0, MaxFailedAttempts - failedCount);
        }
        catch
        {
            return MaxFailedAttempts;
        }
    }

    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }
}