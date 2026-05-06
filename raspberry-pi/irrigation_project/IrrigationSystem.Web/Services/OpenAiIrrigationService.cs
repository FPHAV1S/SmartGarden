using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.Extensions.Options;

namespace IrrigationSystem.Web.Services;

public class OpenAiIrrigationService : IAiIrrigationService
{
    private const string SystemPrompt = "You are an irrigation assistant for a student smart garden project. You analyze soil moisture history and recommend safe watering decisions. You must return only valid JSON. You are not allowed to ignore safety limits. Prefer conservative watering. If data is unclear, recommend not watering. Do not include markdown, explanations outside JSON, or thinking tags.";
    private static readonly TimeSpan OllamaAnalysisTimeout = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient HttpClient;
    private readonly SensorDataService DataService;
    private readonly IOptions<AiIrrigationOptions> Options;
    private readonly OpenAiApiKeyProvider ApiKeyProvider;
    private readonly ILogger<OpenAiIrrigationService> Logger;
    private readonly WeatherForecastService Weather;

    public OpenAiIrrigationService(
        HttpClient httpClient,
        SensorDataService dataService,
        IOptions<AiIrrigationOptions> options,
        OpenAiApiKeyProvider apiKeyProvider,
        ILogger<OpenAiIrrigationService> logger,
        WeatherForecastService weather)
    {
        HttpClient = httpClient;
        DataService = dataService;
        Options = options;
        ApiKeyProvider = apiKeyProvider;
        Logger = logger;
        Weather = weather;
    }

    public async Task<AiIrrigationDecision> AnalyzeAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        CancellationToken cancellationToken)
    {
        var settings = await DataService.GetAiIrrigationOptionsAsync(Options.Value);

        return settings.UsesOllama
            ? await AnalyzeWithOllamaAsync(latestReading, recentReadings, settings, cancellationToken)
            : await AnalyzeWithOpenAiAsync(latestReading, recentReadings, settings, cancellationToken);
    }

    private async Task<AiIrrigationDecision> AnalyzeWithOpenAiAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        AiIrrigationOptions settings,
        CancellationToken cancellationToken)
    {
        var keyLookup = ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable);
        if (!keyLookup.IsConfigured || string.IsNullOrWhiteSpace(keyLookup.ApiKey))
        {
            throw new InvalidOperationException($"{keyLookup.Source} is not configured.");
        }

        var userPrompt = await BuildUserPromptAsync(latestReading, recentReadings, settings, cancellationToken);
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
                format = CreateOpenAiJsonSchemaFormat()
            },
            temperature = 0.2,
            max_output_tokens = 600,
            store = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyLookup.ApiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var apiError = ExtractApiError(responseJson);
            throw new OpenAiApiException((int)response.StatusCode, response.ReasonPhrase, apiError);
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

    private async Task<AiIrrigationDecision> AnalyzeWithOllamaAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        AiIrrigationOptions settings,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OllamaAnalysisTimeout);

        try
        {
            var userPrompt = await BuildUserPromptAsync(latestReading, recentReadings, settings, timeout.Token);
            var requestBody = new
            {
                model = string.IsNullOrWhiteSpace(settings.Model) ? "qwen3:1.7b" : settings.Model,
                stream = false,
                think = false,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = userPrompt }
                },
                format = CreateDecisionSchema(),
                options = new
                {
                    temperature = 0.2,
                    num_predict = 360
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildOllamaChatUri(settings));
            request.Content = JsonContent.Create(requestBody, options: JsonOptions);

            using var response = await HttpClient.SendAsync(request, timeout.Token);
            var responseJson = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                var apiError = ExtractOllamaError(responseJson);
                throw new InvalidOperationException(
                    $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {apiError}");
            }

            var outputText = ExtractOllamaOutputText(responseJson);
            if (!AiIrrigationDecisionParser.TryParse(outputText, out var decision, out var parseError))
            {
                throw new InvalidOperationException(parseError);
            }

            Logger.LogInformation(
                "Ollama irrigation decision received for zone {ZoneId}: shouldWater={ShouldWater}, confidence={Confidence:F2}, risk={RiskLevel}",
                latestReading.ZoneId,
                decision.ShouldWater,
                decision.Confidence,
                decision.RiskLevel);

            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Ollama irrigation analysis timed out after {OllamaAnalysisTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<string> BuildUserPromptAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        AiIrrigationOptions settings,
        CancellationToken cancellationToken)
    {
        var zones = await DataService.GetZonesAsync();
        var zone = zones.FirstOrDefault(z => z.Id == latestReading.ZoneId);
        var systemSettings = await DataService.GetSystemSettingsAsync() ?? new SystemSettings();
        var recentEvents = await DataService.GetRecentIrrigationEventsAsync(
            latestReading.ZoneId,
            Math.Max(1, settings.RecentWateringEvents));
        var patternMemories = await DataService.GetRecentPatternMemoriesAsync(
            Math.Max(1, settings.PatternMemoryCount));

        cancellationToken.ThrowIfCancellationRequested();
        
        var weatherSettings = await DataService.GetWeatherSettingsAsync();
        var weatherAdvice = await Weather.GetAdviceAsync();
        var weatherForecast = new {
            enabled = weatherSettings.EnableWeatherIntegration,
            dataAvailable = weatherAdvice.IsWeatherDataAvailable,
            location = BuildWeatherLocation(weatherSettings),
            latitude = weatherSettings.Latitude,
            longitude = weatherSettings.Longitude,
            rainLikelySoon = weatherAdvice.RainLikelySoon,
            maxRainProbabilityPercent = weatherAdvice.MaxRainProbabilityPercent,
            expectedRainMm = weatherAdvice.ExpectedRainMm,
            hasTemperatureData = weatherAdvice.HasTemperatureData,
            currentTemperatureC = weatherAdvice.CurrentTemperatureC,
            averageTemperatureC = weatherAdvice.AverageTemperatureC,
            minTemperatureC = weatherAdvice.MinTemperatureC,
            maxTemperatureC = weatherAdvice.MaxTemperatureC,
            lookaheadHours = weatherAdvice.LookaheadHours,
            rainProbabilityThresholdPercent = weatherSettings.RainProbabilityThresholdPercent,
            expectedRainMmThreshold = weatherSettings.ExpectedRainMmThreshold,
            advice = weatherAdvice.AdviceText
        };

        var context = new
        {
            project = "Garden Brain / Smart Irrigation",
            currentDateTime = DateTimeOffset.UtcNow.ToString("O"),
            latestReading = new
            {
                latestReading.Id,
                latestReading.ZoneId,
                MoisturePercent = latestReading.Moisture,
                latestReading.Temperature,
                latestReading.Humidity,
                PressureHpa = latestReading.Pressure,
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
                    PressureHpa = reading.Pressure,
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
            weatherForecast = weatherForecast,
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
                settings.EnableWateringCooldown,
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
                "Do not recommend a duration above 120 seconds.",
                "Prefer not watering when rain is likely soon.",
                "Mention weather in the reason when it affects the decision.",
                "Treat unavailable weather data as neutral; do not block watering solely because the forecast API is unavailable."
            }
        };

        return "Analyze this irrigation context and return only JSON matching the required schema: " +
            JsonSerializer.Serialize(context, JsonOptions);
    }

    private static string BuildWeatherLocation(WeatherIntegrationSettings settings)
    {
        var isDefaultBurgas =
            Math.Abs(settings.Latitude - 42.5048) < 0.0001 &&
            Math.Abs(settings.Longitude - 27.4626) < 0.0001;

        return isDefaultBurgas
            ? "Burgas, Bulgaria"
            : FormattableString.Invariant($"lat {settings.Latitude:F4}, lon {settings.Longitude:F4}");
    }

    private static object CreateOpenAiJsonSchemaFormat()
    {
        return new
        {
            type = "json_schema",
            name = "ai_irrigation_decision",
            strict = true,
            schema = CreateDecisionSchema()
        };
    }

    private static object CreateDecisionSchema()
    {
        return new
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
        };
    }

    private static Uri BuildOllamaChatUri(AiIrrigationOptions settings)
    {
        var baseUrl = string.IsNullOrWhiteSpace(settings.OllamaBaseUrl)
            ? "http://localhost:11434"
            : settings.OllamaBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = $"http://{baseUrl}";
        }

        var baseUri = new Uri($"{baseUrl}/", UriKind.Absolute);

        return new Uri(baseUri, "api/chat");
    }

    private static string ExtractOllamaOutputText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return TrimToJsonObject(content.GetString() ?? string.Empty);
        }

        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.String)
        {
            return TrimToJsonObject(response.GetString() ?? string.Empty);
        }

        throw new InvalidOperationException("Ollama response did not contain message content.");
    }

    private static string TrimToJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : text;
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

    private static string ExtractOllamaError(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "No error message returned.";
            }
        }
        catch (JsonException)
        {
        }

        return "No structured error message returned.";
    }
}
