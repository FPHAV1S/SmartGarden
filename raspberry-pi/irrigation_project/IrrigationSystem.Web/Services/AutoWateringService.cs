using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public class AutoWateringService : BackgroundService
{
    private readonly ILogger<AutoWateringService> Logger;
    private readonly SensorDataService DataService;
    private readonly MqttService MqttService;
    private readonly IrrigationDecisionService DecisionService;
    private readonly int CheckIntervalSeconds = 300;

    public AutoWateringService(
        ILogger<AutoWateringService> logger,
        SensorDataService dataService,
        MqttService mqttService,
        IrrigationDecisionService decisionService)
    {
        Logger = logger;
        DataService = dataService;
        MqttService = mqttService;
        DecisionService = decisionService;
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

            var finalDecision = await DecisionService.EvaluateAsync(reading, "auto-watering-service");
            if (!finalDecision.FinalShouldWaterAfterSafety || finalDecision.RecommendedDurationSeconds <= 0)
            {
                Logger.LogInformation(
                    "Zone {ZoneId}: Final decision is not to water. Reason: {Reason}. Safety: {SafetyNotes}",
                    zone.Id,
                    finalDecision.Reason,
                    string.IsNullOrWhiteSpace(finalDecision.SafetyNotes) ? "none" : finalDecision.SafetyNotes);
                continue;
            }

            Logger.LogInformation(
                "Zone {ZoneId}: Final safe decision allows watering for {Duration}s. Fallback used: {Fallback}",
                zone.Id,
                finalDecision.RecommendedDurationSeconds,
                finalDecision.WasFallbackUsed);

            var triggerReason = finalDecision.AiDecision != null && !finalDecision.WasFallbackUsed
                ? "AI Auto"
                : "Auto";

            await WaterZoneAsync(zone.Id, reading.Moisture.Value, finalDecision.RecommendedDurationSeconds, triggerReason);
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

    private async Task WaterZoneAsync(int zoneId, float moistureBefore, int durationSeconds, string triggerReason)
    {
        var eventId = await DataService.StartIrrigationEventAsync(zoneId, triggerReason, moistureBefore);
        
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
