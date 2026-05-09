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
    private readonly WeatherForecastService Weather;
    private readonly SemaphoreSlim AiAnalysisLock = new(1, 1);

    public IrrigationDecisionService(
        SensorDataService dataService,
        IAiIrrigationService aiService,
        IOptions<AiIrrigationOptions> options,
        OpenAiApiKeyProvider apiKeyProvider,
        AiApiHealthService aiApiHealth,
        ILogger<IrrigationDecisionService> logger,
        WeatherForecastService weather)
    {
        DataService = dataService;
        AiService = aiService;
        Options = options;
        ApiKeyProvider = apiKeyProvider;
        AiApiHealth = aiApiHealth;
        Logger = logger;
        Weather = weather;
    }

    public async Task<IrrigationDecisionResult> EvaluateAsync(
        SensorReading latestReading,
        string source,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateCoreAsync(
            latestReading,
            source,
            persistResult: true,
            forceAiAttempt: false,
            cancellationToken);
    }

    public async Task<IrrigationDecisionResult> PreviewAsync(
        SensorReading latestReading,
        string source,
        CancellationToken cancellationToken = default)
    {
        return await EvaluateCoreAsync(
            latestReading,
            source,
            persistResult: false,
            forceAiAttempt: true,
            cancellationToken);
    }

    private async Task<IrrigationDecisionResult> EvaluateCoreAsync(
        SensorReading latestReading,
        string source,
        bool persistResult,
        bool forceAiAttempt,
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
        var weatherAdvice = await Weather.GetAdviceAsync();

        var fallbackDecision = IrrigationSafetyPolicy.BuildRuleBasedDecision(
            latestReading,
            zone,
            systemSettings,
            recentEvents,
            settings);

        AiIrrigationDecision? aiDecision = null;
        var aiWasAttempted = false;
        string? aiErrorMessage = null;

        var keyLookup = settings.RequiresApiKey
            ? ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable)
            : null;
        var activeApiPause = forceAiAttempt ? null : AiApiHealth.GetActivePause(settings);
        var aiLockTaken = false;

        if (!settings.EnableAiDecisionMaking)
        {
            aiErrorMessage = "AI decision making is disabled.";
        }
        else if (settings.RequiresApiKey && keyLookup?.IsConfigured != true)
        {
            aiErrorMessage = $"{keyLookup?.Source ?? "OpenAI API key"} is not configured.";
        }
        else if (activeApiPause != null)
        {
            aiErrorMessage = $"{activeApiPause.Message} Next automatic AI retry after {FormatLocalTime(activeApiPause.RetryAfter)}.";
        }
        else if (!forceAiAttempt && await IsAiCallCoolingDownAsync(settings))
        {
            aiErrorMessage = $"AI call skipped because the minimum interval is {settings.MinimumMinutesBetweenAiCalls} minutes.";
        }
        else if (!forceAiAttempt && !AiAnalysisLock.Wait(0))
        {
            aiErrorMessage = "AI call skipped because another AI analysis is already running.";
        }
        else
        {
            try
            {
                if (forceAiAttempt)
                {
                    await AiAnalysisLock.WaitAsync(cancellationToken);
                }

                aiLockTaken = true;
                aiWasAttempted = true;
                Logger.LogInformation(
                    "Calling {Provider} irrigation analysis from {Source} for zone {ZoneId} with latest moisture {Moisture}",
                    settings.Provider,
                    source,
                    latestReading.ZoneId,
                    latestReading.Moisture);

                aiDecision = await AiService.AnalyzeAsync(latestReading, recentReadings, cancellationToken);
            }
            catch (OpenAiApiException ex)
            {
                var apiResult = await AiApiHealth.RecordOpenAiFailureAsync(ex, settings);
                aiErrorMessage = apiResult.Message;
                Logger.LogWarning(
                    ex,
                    "AI irrigation analysis failed for zone {ZoneId}",
                    latestReading.ZoneId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (settings.UsesOllama)
            {
                var apiResult = await AiApiHealth.RecordOllamaFailureAsync(ex, settings);
                aiErrorMessage = apiResult.Message;
                if (ex is HttpRequestException or TimeoutException)
                {
                    Logger.LogWarning(
                        "AI irrigation analysis failed for zone {ZoneId}: {Message}",
                        latestReading.ZoneId,
                        apiResult.Message);
                }
                else
                {
                    Logger.LogWarning(
                        ex,
                        "AI irrigation analysis failed for zone {ZoneId}",
                        latestReading.ZoneId);
                }
            }
            catch (Exception ex)
            {
                aiErrorMessage = ex.Message;
                Logger.LogWarning(
                    ex,
                    "AI irrigation analysis failed for zone {ZoneId}",
                    latestReading.ZoneId);
            }
            finally
            {
                if (aiLockTaken)
                {
                    AiAnalysisLock.Release();
                }
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
            aiErrorMessage,
            weatherAdvice);

        finalDecision = SuppressFallbackDecision(finalDecision, aiErrorMessage);

        if (persistResult && ShouldPersistAiDecision(finalDecision))
        {
            await DataService.InsertAiDecisionLogAsync(finalDecision);
            await TrySavePatternMemoryAsync(finalDecision, recentReadings, settings);
        }

        if (!finalDecision.WasFallbackUsed)
        {
            Logger.LogInformation(
                "Final AI irrigation decision for zone {ZoneId}: moisture={Moisture}, finalShouldWater={FinalShouldWater}, duration={Duration}s, safetyNotes={SafetyNotes}",
                finalDecision.ZoneId,
                finalDecision.MoisturePercent,
                finalDecision.FinalShouldWaterAfterSafety,
                finalDecision.RecommendedDurationSeconds,
                string.IsNullOrWhiteSpace(finalDecision.SafetyNotes) ? "none" : finalDecision.SafetyNotes);
        }

        return finalDecision;
    }

    private static bool ShouldPersistAiDecision(IrrigationDecisionResult finalDecision)
    {
        return finalDecision.AiDecision != null && !finalDecision.WasFallbackUsed;
    }

    private static IrrigationDecisionResult SuppressFallbackDecision(
        IrrigationDecisionResult finalDecision,
        string? aiErrorMessage)
    {
        if (!finalDecision.WasFallbackUsed)
        {
            return finalDecision;
        }

        finalDecision.FinalShouldWaterAfterSafety = false;
        finalDecision.RecommendedValveState = "OFF";
        finalDecision.RecommendedDurationSeconds = 0;
        finalDecision.Reason = "AI did not produce an accepted decision, so automatic watering stayed off.";
        finalDecision.SafetyNotes = string.IsNullOrWhiteSpace(aiErrorMessage)
            ? "AI did not produce an accepted decision."
            : aiErrorMessage;

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

        if (settings.CooldownsResetAt.HasValue && latestAttempt.Timestamp <= settings.CooldownsResetAt.Value)
        {
            return false;
        }

        return DateTime.UtcNow - latestAttempt.Timestamp < TimeSpan.FromMinutes(settings.MinimumMinutesBetweenAiCalls);
    }

    private static string FormatLocalTime(DateTime? utcTime)
    {
        return utcTime.HasValue
            ? DateTime.SpecifyKind(utcTime.Value, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm")
            : "unknown";
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
