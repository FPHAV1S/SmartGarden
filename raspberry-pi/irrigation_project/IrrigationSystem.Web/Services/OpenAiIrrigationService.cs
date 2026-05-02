using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.Extensions.Options;

namespace IrrigationSystem.Web.Services;

public class OpenAiIrrigationService : IAiIrrigationService
{
    private const string SystemPrompt = "You are an irrigation assistant for a student smart garden project. You analyze soil moisture history and recommend safe watering decisions. You must return only valid JSON. You are not allowed to ignore safety limits. Prefer conservative watering. If data is unclear, recommend not watering.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient HttpClient;
    private readonly SensorDataService DataService;
    private readonly IOptions<AiIrrigationOptions> Options;
    private readonly ILogger<OpenAiIrrigationService> Logger;

    public OpenAiIrrigationService(
        HttpClient httpClient,
        SensorDataService dataService,
        IOptions<AiIrrigationOptions> options,
        ILogger<OpenAiIrrigationService> logger)
    {
        HttpClient = httpClient;
        DataService = dataService;
        Options = options;
        Logger = logger;
    }

    public async Task<AiIrrigationDecision> AnalyzeAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        CancellationToken cancellationToken)
    {
        var settings = Options.Value;
        var apiKey = Environment.GetEnvironmentVariable(settings.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"{settings.ApiKeyEnvironmentVariable} is not configured.");
        }

        var userPrompt = await BuildUserPromptAsync(latestReading, recentReadings, cancellationToken);
        var requestBody = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4.1-nano" : settings.Model,
            input = new[]
            {
                new
                {
                    role = "system",
                    content = new[]
                    {
                        new { type = "input_text", text = SystemPrompt }
                    }
                },
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = userPrompt }
                    }
                }
            },
            text = new
            {
                format = CreateJsonSchemaFormat()
            },
            temperature = 0.2,
            max_output_tokens = 600,
            store = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var apiError = ExtractApiError(responseJson);
            throw new InvalidOperationException($"OpenAI API returned {(int)response.StatusCode} {response.ReasonPhrase}: {apiError}");
        }

        var outputText = ExtractOutputText(responseJson);
        if (!AiIrrigationDecisionParser.TryParse(outputText, out var decision, out var parseError))
        {
            throw new InvalidOperationException(parseError);
        }

        Logger.LogInformation(
            "OpenAI irrigation decision received for zone {ZoneId}: shouldWater={ShouldWater}, confidence={Confidence:F2}, risk={RiskLevel}",
            latestReading.ZoneId,
            decision.ShouldWater,
            decision.Confidence,
            decision.RiskLevel);

        return decision;
    }

    private async Task<string> BuildUserPromptAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        CancellationToken cancellationToken)
    {
        var settings = Options.Value;
        var zones = await DataService.GetZonesAsync();
        var zone = zones.FirstOrDefault(z => z.Id == latestReading.ZoneId);
        var systemSettings = await DataService.GetSystemSettingsAsync() ?? new SystemSettings();
        var recentEvents = await DataService.GetRecentIrrigationEventsAsync(
            latestReading.ZoneId,
            Math.Max(1, settings.RecentWateringEvents));
        var patternMemories = await DataService.GetRecentPatternMemoriesAsync(
            Math.Max(1, settings.PatternMemoryCount));

        cancellationToken.ThrowIfCancellationRequested();

        var context = new
        {
            project = "Garden Brain / Smart Irrigation",
            currentDateTime = DateTimeOffset.Now.ToString("O"),
            latestReading = new
            {
                latestReading.Id,
                latestReading.ZoneId,
                MoisturePercent = latestReading.Moisture,
                latestReading.Temperature,
                latestReading.Humidity,
                latestReading.RecordedAt
            },
            recentReadings = recentReadings
                .OrderBy(reading => reading.RecordedAt)
                .TakeLast(40)
                .Select(reading => new
                {
                    reading.Id,
                    reading.ZoneId,
                    MoisturePercent = reading.Moisture,
                    reading.Temperature,
                    reading.Humidity,
                    reading.RecordedAt
                }),
            previousWateringEvents = recentEvents.Select(irrigationEvent => new
            {
                irrigationEvent.Id,
                irrigationEvent.ZoneId,
                irrigationEvent.StartedAt,
                irrigationEvent.EndedAt,
                irrigationEvent.DurationSec,
                irrigationEvent.TriggerReason,
                irrigationEvent.MoistureBefore,
                irrigationEvent.MoistureAfter
            }),
            currentRules = new
            {
                ZoneName = zone?.Name,
                zone?.PlantType,
                MoistureThreshold = zone?.MoistureThreshold,
                ZoneIsActive = zone?.IsActive,
                systemSettings.AutoWateringEnabled,
                systemSettings.DefaultWateringDuration,
                systemSettings.NightModeEnabled,
                systemSettings.NightModeStartHour,
                systemSettings.NightModeEndHour,
                systemSettings.EcoModeEnabled,
                settings.HighMoistureBlockPercent,
                settings.MaxDurationSeconds,
                settings.LowConfidenceThreshold,
                settings.MinimumMinutesBetweenWaterings
            },
            patternMemories = patternMemories.Select(memory => new
            {
                memory.Id,
                memory.CreatedAt,
                memory.Summary,
                memory.SuggestedThreshold,
                memory.AverageMoisture,
                memory.Notes
            }),
            instructions = new[]
            {
                "Return only the requested JSON object.",
                "Prefer OFF when data is unclear.",
                "Do not recommend watering if moisture is already high.",
                "Do not recommend a duration above 120 seconds."
            }
        };

        return "Analyze this irrigation context and return only JSON matching the required schema: " +
            JsonSerializer.Serialize(context, JsonOptions);
    }

    private static object CreateJsonSchemaFormat()
    {
        return new
        {
            type = "json_schema",
            name = "ai_irrigation_decision",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[]
                {
                    "shouldWater",
                    "recommendedValveState",
                    "recommendedDurationSeconds",
                    "confidence",
                    "reason",
                    "learnedObservation",
                    "suggestedMoistureThreshold",
                    "riskLevel"
                },
                properties = new Dictionary<string, object>
                {
                    ["shouldWater"] = new { type = "boolean" },
                    ["recommendedValveState"] = new { type = "string", @enum = new[] { "ON", "OFF" } },
                    ["recommendedDurationSeconds"] = new { type = "integer", minimum = 0, maximum = 120 },
                    ["confidence"] = new { type = "number", minimum = 0, maximum = 1 },
                    ["reason"] = new { type = "string" },
                    ["learnedObservation"] = new { type = "string" },
                    ["suggestedMoistureThreshold"] = new { type = "number", minimum = 0, maximum = 100 },
                    ["riskLevel"] = new { type = "string", @enum = new[] { "LOW", "MEDIUM", "HIGH" } }
                }
            }
        };
    }

    private static string ExtractOutputText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("OpenAI response did not contain output text.");
        }

        foreach (var outputItem in output.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "output_text", StringComparison.Ordinal) &&
                    contentItem.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? string.Empty;
                }
            }
        }

        throw new InvalidOperationException("OpenAI response did not contain output_text content.");
    }

    private static string ExtractApiError(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "No error message returned.";
            }
        }
        catch (JsonException)
        {
        }

        return "No structured error message returned.";
    }
}
