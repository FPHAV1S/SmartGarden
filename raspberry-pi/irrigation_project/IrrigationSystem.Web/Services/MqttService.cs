using MQTTnet;
using System.Text;
using System.Text.Json;

namespace IrrigationSystem.Web.Services;

public class MqttService
{
    private readonly IMqttClient MqttClient;
    private readonly MqttClientOptions Options;
    private readonly SemaphoreSlim ConnectLock = new(1, 1);
    private readonly ILogger<MqttService> Logger;

    public MqttService(ILogger<MqttService> logger)
    {
        Logger = logger;
        var factory = new MqttClientFactory();
        MqttClient = factory.CreateMqttClient();

        Options = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId($"IrrigationWeb-{Environment.MachineName}-{Environment.ProcessId}")
            .Build();

        _ = Task.Run(async () => await EnsureConnectedAsync());
    }

    private async Task<bool> EnsureConnectedAsync()
    {
        if (MqttClient.IsConnected)
        {
            return true;
        }

        await ConnectLock.WaitAsync();
        try
        {
            if (MqttClient.IsConnected)
            {
                return true;
            }

            var result = await MqttClient.ConnectAsync(Options);
            Logger.LogInformation("MQTT client connected: {ResultCode}", result.ResultCode);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to MQTT broker");
            return false;
        }
        finally
        {
            ConnectLock.Release();
        }
    }

    public async Task<bool> OpenValveAsync(int zoneId, int durationSeconds)
    {
        try
        {
            if (!await EnsureConnectedAsync())
            {
                return false;
            }

            var command = new { action = "open", duration = durationSeconds };
            var payload = JsonSerializer.Serialize(command);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"irrigation/zone/{zoneId}/valve")
                .WithPayload(payload)
                .Build();

            await MqttClient.PublishAsync(message);
            Logger.LogInformation("Sent valve open command to zone {ZoneId} for {Duration}s", zoneId, durationSeconds);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send valve command");
            return false;
        }
    }

    public async Task<bool> CloseValveAsync(int zoneId)
    {
        try
        {
            if (!await EnsureConnectedAsync())
            {
                return false;
            }

            var command = new { action = "close" };
            var payload = JsonSerializer.Serialize(command);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic($"irrigation/zone/{zoneId}/valve")
                .WithPayload(payload)
                .Build();

            await MqttClient.PublishAsync(message);
            Logger.LogInformation("Sent valve close command to zone {ZoneId}", zoneId);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send valve command");
            return false;
        }
    }
}
