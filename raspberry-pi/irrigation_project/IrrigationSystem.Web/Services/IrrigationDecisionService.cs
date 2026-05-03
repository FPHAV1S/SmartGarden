using IrrigationSystem.Web.Models;
using Microsoft.Extensions.Options;

namespace IrrigationSystem.Web.Services;

public class IrrigationDecisionService
{
    private readonly SensorDataService DataService;
    private readonly IAiIrrigationService AiService;
    private readonly IOptions<AiIrrigationOptions> Options;
    private readonly OpenAiApiKeyProvider ApiKeyProvider;
    private readonly AiApiHealthService AiApiHealth;
    private readonly ILogger<IrrigationDecisionService> Logger;

    public IrrigationDecisionService(
        SensorDataService dataService,
        IAiIrrigationService aiService,
        IOptions<AiIrrigationOptions> options,
        OpenAiApiKeyProvider apiKeyProvider,
        AiApiHealthService aiApiHealth,
        ILogger<IrrigationDecisionService> logger)
    {
        DataService = dataService;
        AiService = aiService;
        Options = options;
        ApiKeyProvider = apiKeyProvider;
        AiApiHealth = aiApiHealth;
        Logger = logger;
    }

    public async Task<IrrigationDecisionResult> EvaluateAsync(
        SensorReading latestReading,
        string source,
        CancellationToken cancellationToken = default)
    {
        var settings = await DataService.GetAiIrrigationOptionsAsync(Options.Value);
        var zone = await DataService.GetZoneAsync(latestReading.ZoneId);
        var systemSettings = await DataService.GetSystemSettingsAsync() ?? new SystemSettings();
        var recentReadings = await DataService.GetZoneHistoryAsync(
            latestReading.ZoneId,
            Math.Max(1, settings.RecentHistoryHours));
        var recentEvents = await DataService.GetRecentIrrigationEventsAsync(
            latestReading.ZoneId,
            Math.Max(1, settings.RecentWateringEvents));

        var fallbackDecision = IrrigationSafetyPolicy.BuildRuleBasedDecision(
            latestReading,
            zone,
            systemSettings,
            recentEvents,
            settings);

        AiIrrigationDecision? aiDecision = null;
        var aiWasAttempted = false;
        string? aiErrorMessage = null;

        var keyLookup = ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable);
        var activeApiPause = AiApiHealth.GetActivePause();

        if (!settings.EnableAiDecisionMaking)
        {
            aiErrorMessage = "AI decision making is disabled; using rule fallback.";
        }
        else if (!keyLookup.IsConfigured)
        {
            aiErrorMessage = $"{keyLookup.Source} is not configured; using rule fallback.";
        }
        else if (activeApiPause != null)
        {
            aiErrorMessage = $"{activeApiPause.Message} Next automatic AI retry after {activeApiPause.RetryAfter:HH:mm}.";
        }
        else if (await IsAiCallCoolingDownAsync(settings))
        {
            aiErrorMessage = $"AI call skipped because the minimum interval is {settings.MinimumMinutesBetweenAiCalls} minutes; using rule fallback.";
        }
        else
        {
            aiWasAttempted = true;
            Logger.LogInformation(
                "Calling AI irrigation analysis from {Source} for zone {ZoneId} with latest moisture {Moisture}",
                source,
                latestReading.ZoneId,
                latestReading.Moisture);

            try
            {
                aiDecision = await AiService.AnalyzeAsync(latestReading, recentReadings, cancellationToken);
            }
            catch (OpenAiApiException ex)
            {
                var apiResult = await AiApiHealth.RecordOpenAiFailureAsync(ex, settings);
                aiErrorMessage = apiResult.Message;
                Logger.LogWarning(
                    ex,
                    "AI irrigation analysis failed for zone {ZoneId}; falling back to rule-based logic",
                    latestReading.ZoneId);
            }
            catch (Exception ex)
            {
                aiErrorMessage = ex.Message;
                Logger.LogWarning(
                    ex,
                    "AI irrigation analysis failed for zone {ZoneId}; falling back to rule-based logic",
                    latestReading.ZoneId);
            }
        }

        var finalDecision = IrrigationSafetyPolicy.Apply(
            latestReading,
            zone,
            systemSettings,
            recentEvents,
            fallbackDecision,
            aiDecision,
            settings,
            aiWasAttempted,
            aiErrorMessage);

        await DataService.InsertAiDecisionLogAsync(finalDecision);
        await TrySavePatternMemoryAsync(finalDecision, recentReadings, settings);

        Logger.LogInformation(
            "Final irrigation decision for zone {ZoneId}: moisture={Moisture}, finalShouldWater={FinalShouldWater}, duration={Duration}s, fallback={Fallback}, safetyNotes={SafetyNotes}",
            finalDecision.ZoneId,
            finalDecision.MoisturePercent,
            finalDecision.FinalShouldWaterAfterSafety,
            finalDecision.RecommendedDurationSeconds,
            finalDecision.WasFallbackUsed,
            string.IsNullOrWhiteSpace(finalDecision.SafetyNotes) ? "none" : finalDecision.SafetyNotes);

        return finalDecision;
    }

    private async Task<bool> IsAiCallCoolingDownAsync(AiIrrigationOptions settings)
    {
        if (settings.MinimumMinutesBetweenAiCalls <= 0)
        {
            return false;
        }

        var latestAttempt = await DataService.GetLatestAttemptedAiDecisionAsync();
        if (latestAttempt == null)
        {
            return false;
        }

        return DateTime.Now - latestAttempt.Timestamp < TimeSpan.FromMinutes(settings.MinimumMinutesBetweenAiCalls);
    }

    private async Task TrySavePatternMemoryAsync(
        IrrigationDecisionResult finalDecision,
        List<SensorReading> recentReadings,
        AiIrrigationOptions settings)
    {
        if (finalDecision.AiDecision == null ||
            finalDecision.WasFallbackUsed ||
            finalDecision.Confidence < settings.LowConfidenceThreshold ||
            string.IsNullOrWhiteSpace(finalDecision.LearnedObservation))
        {
            return;
        }

        var averageMoisture = recentReadings
            .Where(reading => reading.Moisture.HasValue)
            .Select(reading => reading.Moisture!.Value)
            .DefaultIfEmpty(finalDecision.MoisturePercent ?? 0)
            .Average();

        await DataService.InsertPatternMemoryAsync(
            finalDecision.LearnedObservation,
            finalDecision.SuggestedMoistureThreshold,
            averageMoisture,
            $"Zone {finalDecision.ZoneId}: {finalDecision.Reason}");
    }
}
