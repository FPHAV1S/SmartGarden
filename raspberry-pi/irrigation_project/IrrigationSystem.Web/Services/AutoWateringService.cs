using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public class AutoWateringService : BackgroundService
{
    private readonly ILogger<AutoWateringService> Logger;
    private readonly SensorDataService DataService;
    private readonly MqttService MqttService;
    private readonly int CheckIntervalSeconds = 300;
    private readonly int MinHoursBetweenWaterings = 2;

    public AutoWateringService(
        ILogger<AutoWateringService> logger,
        SensorDataService dataService,
        MqttService mqttService)
    {
        Logger = logger;
        DataService = dataService;
        MqttService = mqttService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Auto-watering service started - checking every {Interval} seconds", CheckIntervalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndWaterZonesAsync();
                    await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error in auto-watering service");
                    await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAndWaterZonesAsync()
    {
        var settings = await DataService.GetSystemSettingsAsync();
        
        if (settings == null || !settings.AutoWateringEnabled)
        {
            Logger.LogInformation("Auto-watering is disabled in settings - skipping check");
            return;
        }

        if (settings.NightModeEnabled && !IsNightMode(settings))
        {
            Logger.LogInformation("Night mode enabled but not in watering hours - skipping");
            return;
        }

        var zones = await DataService.GetZonesAsync();
        var latestReadings = await DataService.GetLatestReadingsAsync();

        foreach (var zone in zones)
        {
            if (!zone.IsActive)
            {
                continue;
            }

            var reading = latestReadings.FirstOrDefault(r => r.ZoneId == zone.Id);

            if (reading == null || !reading.Moisture.HasValue)
            {
                Logger.LogWarning("Zone {ZoneId}: No recent sensor data, skipping auto-watering", zone.Id);
                continue;
            }

            if (reading.Moisture.Value < zone.MoistureThreshold)
            {
                var lastWatering = await GetLastWateringTimeAsync(zone.Id);
                var hoursSinceLastWatering = lastWatering.HasValue 
                    ? (DateTime.Now - lastWatering.Value).TotalHours 
                    : double.MaxValue;

                var minHours = MinHoursBetweenWaterings;
                if (settings.EcoModeEnabled)
                {
                    minHours = (int)(minHours * 1.5);
                    Logger.LogInformation("Zone {ZoneId}: Eco mode active - minimum wait time increased to {Hours}h", zone.Id, minHours);
                }

                if (hoursSinceLastWatering < minHours)
                {
                    Logger.LogInformation("Zone {ZoneId}: Moisture {Moisture}% below threshold {Threshold}%, but watered {Hours:F1}h ago - waiting", 
                        zone.Id, reading.Moisture.Value, zone.MoistureThreshold, hoursSinceLastWatering);
                    continue;
                }

                Logger.LogInformation("Zone {ZoneId}: Moisture {Moisture}% below threshold {Threshold}% - triggering auto-watering", 
                    zone.Id, reading.Moisture.Value, zone.MoistureThreshold);

                await WaterZoneAsync(zone.Id, reading.Moisture.Value, settings.DefaultWateringDuration);
            }
        }
    }

    private bool IsNightMode(SystemSettings settings)
    {
        var currentHour = DateTime.Now.Hour;
        
        if (settings.NightModeStartHour < settings.NightModeEndHour)
        {
            return currentHour >= settings.NightModeStartHour && currentHour < settings.NightModeEndHour;
        }
        else
        {
            return currentHour >= settings.NightModeStartHour || currentHour < settings.NightModeEndHour;
        }
    }

    private async Task<DateTime?> GetLastWateringTimeAsync(int zoneId)
    {
        var recentEvents = await DataService.GetRecentIrrigationEventsAsync(zoneId, 1);
        return recentEvents.FirstOrDefault()?.StartedAt;
    }

    private async Task WaterZoneAsync(int zoneId, float moistureBefore, int durationSeconds)
    {
        var eventId = await DataService.StartIrrigationEventAsync(zoneId, "Auto", moistureBefore);
        
        var success = await MqttService.OpenValveAsync(zoneId, durationSeconds);
        
        if (success)
        {
            Logger.LogInformation("Zone {ZoneId}: Valve opened for {Duration}s", zoneId, durationSeconds);
            
            await DataService.LogSystemMessageAsync("INFO", 
                $"Auto-watering started for Zone {zoneId} (moisture: {moistureBefore:F1}%)");

            await Task.Delay(durationSeconds * 1000);
            
            await MqttService.CloseValveAsync(zoneId);
            
            await Task.Delay(5000);
            
            var newReadings = await DataService.GetLatestReadingsAsync();
            var newReading = newReadings.FirstOrDefault(r => r.ZoneId == zoneId);
            var moistureAfter = newReading?.Moisture;

            await DataService.EndIrrigationEventAsync(eventId, durationSeconds, moistureAfter);
            
            Logger.LogInformation("Zone {ZoneId}: Watering complete - moisture before: {Before:F1}%, after: {After}", 
                zoneId, moistureBefore, moistureAfter.HasValue ? $"{moistureAfter.Value:F1}%" : "N/A");
        }
        else
        {
            Logger.LogError("Zone {ZoneId}: Failed to open valve", zoneId);
        }
    }
}
