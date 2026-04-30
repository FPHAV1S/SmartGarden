using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Services;

public class DemoModeService : BackgroundService
{
    private readonly ILogger<DemoModeService> Logger;
    private readonly IServiceScopeFactory ScopeFactory;
    private readonly Random Rng = new Random();

    private float[] ZoneMoisture = { 35.0f, 40.0f, 38.0f };
    private float[] ZoneTemperature = { 22.0f, 23.0f, 21.5f };
    private float[] ZoneHumidity = { 65.0f, 68.0f, 66.0f };

    public DemoModeService(
        ILogger<DemoModeService> logger,
        IServiceScopeFactory scopeFactory)
    {
        Logger = logger;
        ScopeFactory = scopeFactory;
    }

    public bool IsDemoMode { get; private set; }

    public void EnableDemoMode()
    {
        IsDemoMode = true;
        Logger.LogInformation("🎭 Demo Mode ENABLED - Simulating ESP32 sensors");
    }

    public void DisableDemoMode()
    {
        IsDemoMode = false;
        Logger.LogInformation("Demo Mode DISABLED - Using real sensors");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Demo Mode Service started (inactive by default)");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsDemoMode)
            {
                await GenerateDemoDataAsync();
            }

            await Task.Delay(5000, stoppingToken);
        }
    }

    private async Task GenerateDemoDataAsync()
    {
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var sensorService = scope.ServiceProvider.GetRequiredService<SensorDataService>();

            var zones = await sensorService.EnsureDefaultZonesAsync();
            var demoZones = zones
                .Where(zone => zone.IsActive)
                .Take(ZoneMoisture.Length)
                .ToList();

            if (demoZones.Count == 0)
            {
                Logger.LogWarning("Demo mode has no active zones to generate data for");
                return;
            }

            for (var zoneIndex = 0; zoneIndex < demoZones.Count; zoneIndex++)
            {
                SimulateSensorChanges(zoneIndex);

                await sensorService.InsertSensorReadingAsync(
                    demoZones[zoneIndex].Id,
                    ZoneMoisture[zoneIndex],
                    ZoneTemperature[zoneIndex],
                    ZoneHumidity[zoneIndex]
                );
            }

            Logger.LogInformation("🎭 Demo data generated for all zones");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error generating demo data");
        }
    }

    private void SimulateSensorChanges(int zoneIndex)
    {
        var hour = DateTime.Now.Hour;
        var isDaytime = hour >= 6 && hour <= 20;

        ZoneMoisture[zoneIndex] -= (float)(Rng.NextDouble() * 0.3 + 0.1);

        if (ZoneMoisture[zoneIndex] < 20.0f)
            ZoneMoisture[zoneIndex] = 20.0f;
        if (ZoneMoisture[zoneIndex] > 60.0f)
            ZoneMoisture[zoneIndex] = 60.0f;

        var tempBase = isDaytime ? 25.0f : 18.0f;
        ZoneTemperature[zoneIndex] = tempBase + (float)(Rng.NextDouble() * 4 - 2);

        ZoneHumidity[zoneIndex] = 85.0f - (ZoneTemperature[zoneIndex] - 15.0f) * 1.5f;
        ZoneHumidity[zoneIndex] += (float)(Rng.NextDouble() * 4 - 2);

        ZoneTemperature[zoneIndex] = Math.Clamp(ZoneTemperature[zoneIndex], 15.0f, 35.0f);
        ZoneHumidity[zoneIndex] = Math.Clamp(ZoneHumidity[zoneIndex], 30.0f, 90.0f);
    }

    public void SimulateWatering(int zoneId)
    {
        if (zoneId >= 1 && zoneId <= 3)
        {
            ZoneMoisture[zoneId - 1] += (float)(Rng.NextDouble() * 10 + 10);
            ZoneMoisture[zoneId - 1] = Math.Min(ZoneMoisture[zoneId - 1], 60.0f);

            Logger.LogInformation("🎭 Simulated watering for Zone {ZoneId}", zoneId);
        }
    }
}
