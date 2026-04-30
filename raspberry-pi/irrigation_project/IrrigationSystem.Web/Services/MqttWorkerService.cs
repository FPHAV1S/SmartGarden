using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace IrrigationSystem.Web.Services;

public class MqttWorkerService : BackgroundService
{
    private readonly ILogger<MqttWorkerService> Logger;
    private readonly IServiceScopeFactory ScopeFactory;
    private IMqttClient? MqttClient;

    public MqttWorkerService(
        ILogger<MqttWorkerService> logger,
        IServiceScopeFactory scopeFactory)
    {
        Logger = logger;
        ScopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("MQTT Worker Service starting...");

        var factory = new MqttClientFactory();
        MqttClient = factory.CreateMqttClient();

        MqttClient.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                Logger.LogInformation("Received message on topic: {Topic}", topic);

                var parts = topic.Split('/');
                if (parts.Length >= 3 && parts[0] == "garden" && parts[2] == "sensors")
                {
                    var zoneIdStr = parts[1].Replace("zone", "");
                    if (int.TryParse(zoneIdStr, out int zoneId))
                    {
                        await ProcessSensorDataAsync(zoneId, payload);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error processing MQTT message");
            }
        };

        MqttClient.DisconnectedAsync += async e =>
        {
            Logger.LogWarning("MQTT disconnected. Reconnecting in 5 seconds...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            try
            {
                await MqttClient.ConnectAsync(GetOptions(), stoppingToken);
            }
            catch
            {
                Logger.LogError("Reconnection failed.");
            }
        };

        try
        {
            await MqttClient.ConnectAsync(GetOptions(), stoppingToken);
            Logger.LogInformation("Connected to MQTT broker");

            await MqttClient.SubscribeAsync("garden/+/sensors");
            Logger.LogInformation("Subscribed to garden/+/sensors");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to MQTT broker");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }

        if (MqttClient.IsConnected)
        {
            await MqttClient.DisconnectAsync();
        }

        MqttClient?.Dispose();
    }

    private MqttClientOptions GetOptions()
    {
        return new MqttClientOptionsBuilder()
            .WithClientId("IrrigationWorker")
            .WithTcpServer("localhost", 1883)
            .Build();
    }

    private async Task ProcessSensorDataAsync(int zoneId, string payload)
    {
        try
        {
            var data = JsonSerializer.Deserialize<SensorData>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data == null)
            {
                Logger.LogWarning("Failed to parse sensor data: {Payload}", payload);
                return;
            }

            using var scope = ScopeFactory.CreateScope();
            var sensorService = scope.ServiceProvider.GetRequiredService<SensorDataService>();

            await sensorService.InsertSensorReadingAsync(
                zoneId,
                data.Moisture,
                data.Temperature,
                data.Humidity
            );

            Logger.LogInformation(
                "Saved sensor reading for Zone {ZoneId}: Moisture={Moisture}%, Temp={Temp}°C, Humidity={Humidity}%",
                zoneId, data.Moisture, data.Temperature, data.Humidity);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error saving sensor data for zone {ZoneId}", zoneId);
        }
    }

    private class SensorData
    {
        public float Moisture { get; set; }
        public float Temperature { get; set; }
        public float Humidity { get; set; }
    }
}
