using MQTTnet;
using System.Text;
using System.Text.Json;

namespace IrrigationSystem.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> Logger;
    private readonly DatabaseService Db;
    private IMqttClient? MqttClient;

    public Worker(ILogger<Worker> logger, IConfiguration configuration)
    {
        Logger = logger;
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        Db = new DatabaseService(connectionString, 
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<DatabaseService>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        MqttClient = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer("localhost", 1883)
            .WithClientId("IrrigationWorker")
            .Build();

        MqttClient.ApplicationMessageReceivedAsync += HandleMessageAsync;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!MqttClient.IsConnected)
                {
                    Logger.LogInformation("Connecting to MQTT broker...");
                    var connectResult = await MqttClient.ConnectAsync(options, stoppingToken);

                    if (connectResult.ResultCode == MqttClientConnectResultCode.Success)
                    {
                        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                            .WithTopicFilter("irrigation/zone/+/sensors")
                            .Build();

                        await MqttClient.SubscribeAsync(subscribeOptions, stoppingToken);

                        Logger.LogInformation("Connected to MQTT and subscribed to topics");
                        await Db.LogSystemMessageAsync("INFO", "MQTT Worker started");
                    }
                }

                await Task.Delay(5000, stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "MQTT connection error, retrying in 10 seconds...");
                await Task.Delay(10000, stoppingToken);
            }
        }
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.FirstSpan);

            Logger.LogInformation("Received: {Topic} -> {Payload}", topic, payload);

            if (topic.StartsWith("irrigation/zone/") && topic.EndsWith("/sensors"))
            {
                var zoneId = int.Parse(topic.Split('/')[2]);
                var data = JsonSerializer.Deserialize<SensorData>(payload);

                if (data != null)
                {
                    await Db.InsertSensorReadingAsync(zoneId, data.moisture, data.temperature,
                                                        data.humidity);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling MQTT message");
        }

        await Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken stoppingToken)
    {
        if (MqttClient != null && MqttClient.IsConnected)
        {
            await MqttClient.DisconnectAsync(cancellationToken: stoppingToken);
            Logger.LogInformation("Disconnected from MQTT broker");
        }

        await base.StopAsync(stoppingToken);
    }
}

public class SensorData
{
    public float? moisture { get; set; }
    public float? temperature { get; set; }
    public float? humidity { get; set; }
}
