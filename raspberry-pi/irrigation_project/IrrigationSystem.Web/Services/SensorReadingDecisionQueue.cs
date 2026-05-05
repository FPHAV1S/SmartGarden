using System.Threading.Channels;
using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public sealed class SensorReadingDecisionQueue : BackgroundService
{
    private readonly Channel<SensorReadingDecisionWorkItem> Queue;
    private readonly SensorDataService DataService;
    private readonly IrrigationDecisionService DecisionService;
    private readonly IValveCommandService ValveCommands;
    private readonly ILogger<SensorReadingDecisionQueue> Logger;

    public SensorReadingDecisionQueue(
        SensorDataService dataService,
        IrrigationDecisionService decisionService,
        IValveCommandService valveCommands,
        ILogger<SensorReadingDecisionQueue> logger)
    {
        DataService = dataService;
        DecisionService = decisionService;
        ValveCommands = valveCommands;
        Logger = logger;

        Queue = Channel.CreateBounded<SensorReadingDecisionWorkItem>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    public bool TryQueue(SensorReading reading, string source)
    {
        var workItem = new SensorReadingDecisionWorkItem(
            new SensorReading
            {
                Id = reading.Id,
                ZoneId = reading.ZoneId,
                Moisture = reading.Moisture,
                Temperature = reading.Temperature,
                Humidity = reading.Humidity,
                Pressure = reading.Pressure,
                RecordedAt = reading.RecordedAt
            },
            source);

        return Queue.Writer.TryWrite(workItem);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in Queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var finalDecision = await DecisionService.EvaluateAsync(
                    workItem.Reading,
                    workItem.Source,
                    stoppingToken);

                var command = await TryApplyWateringDecisionAsync(finalDecision);
                Logger.LogInformation(
                    "Processed queued sensor decision for reading {ReadingId}: {Message}",
                    workItem.Reading.Id,
                    command.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Failed to process queued sensor decision for reading {ReadingId}",
                    workItem.Reading.Id);
            }
        }
    }

    private async Task<WateringCommandResult> TryApplyWateringDecisionAsync(IrrigationDecisionResult finalDecision)
    {
        if (!finalDecision.FinalShouldWaterAfterSafety || finalDecision.RecommendedDurationSeconds <= 0)
        {
            return new WateringCommandResult(false, false, "Final decision is OFF; no valve command sent.");
        }

        var durationSeconds = finalDecision.RecommendedDurationSeconds;
        var triggerReason = finalDecision.AiDecision != null && !finalDecision.WasFallbackUsed
            ? "AI Sensor"
            : "Sensor Auto";

        var opened = await ValveCommands.OpenValveAsync(finalDecision.ZoneId, durationSeconds);
        if (!opened)
        {
            Logger.LogWarning(
                "Final decision allowed watering for zone {ZoneId}, but MQTT open command failed.",
                finalDecision.ZoneId);
            return new WateringCommandResult(true, false, "Final decision allowed watering, but MQTT open command failed.");
        }

        var eventId = await DataService.StartIrrigationEventAsync(
            finalDecision.ZoneId,
            triggerReason,
            finalDecision.MoisturePercent);

        if (eventId > 0)
        {
            _ = CompleteIrrigationEventLaterAsync(eventId, finalDecision.ZoneId, durationSeconds);
        }

        Logger.LogInformation(
            "Final decision opened valve for zone {ZoneId} for {DurationSeconds}s from sensor reading {SensorReadingId}.",
            finalDecision.ZoneId,
            durationSeconds,
            finalDecision.SensorReadingId);

        return new WateringCommandResult(true, true, $"Valve open command sent for {durationSeconds}s.");
    }

    private async Task CompleteIrrigationEventLaterAsync(int eventId, int zoneId, int durationSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
            await ValveCommands.CloseValveAsync(zoneId);
            await Task.Delay(TimeSpan.FromSeconds(5));

            var newReadings = await DataService.GetLatestReadingsAsync();
            var newReading = newReadings.FirstOrDefault(reading => reading.ZoneId == zoneId);

            await DataService.EndIrrigationEventAsync(eventId, durationSeconds, newReading?.Moisture);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to complete irrigation event {EventId} for zone {ZoneId}", eventId, zoneId);
        }
    }
}

public sealed record SensorReadingDecisionWorkItem(SensorReading Reading, string Source);

public sealed record WateringCommandResult(
    bool WasNeeded,
    bool WasSent,
    string Message);
