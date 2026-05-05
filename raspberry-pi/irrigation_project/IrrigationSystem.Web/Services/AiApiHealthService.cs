using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.Extensions.Options;

namespace IrrigationSystem.Web.Services;

public class AiApiHealthService
{
    private static readonly TimeSpan OpenAiCheckTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OllamaCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OllamaFailurePause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuotaOrRateLimitPause = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory HttpClientFactory;
    private readonly SensorDataService DataService;
    private readonly IOptions<AiIrrigationOptions> Options;
    private readonly OpenAiApiKeyProvider ApiKeyProvider;
    private readonly ILogger<AiApiHealthService> Logger;
    private readonly SemaphoreSlim CheckLock = new(1, 1);

    private AiApiCheckResult? LastResult;

    public AiApiHealthService(
        IHttpClientFactory httpClientFactory,
        SensorDataService dataService,
        IOptions<AiIrrigationOptions> options,
        OpenAiApiKeyProvider apiKeyProvider,
        ILogger<AiApiHealthService> logger)
    {
        HttpClientFactory = httpClientFactory;
        DataService = dataService;
        Options = options;
        ApiKeyProvider = apiKeyProvider;
        Logger = logger;
    }

    public AiApiCheckResult? GetLastResult()
    {
        return LastResult;
    }

    public AiApiCheckResult? GetActivePause(AiIrrigationOptions? settings = null)
    {
        var result = LastResult;
        if (result == null ||
            result.IsSuccess ||
            !result.RetryAfter.HasValue ||
            result.RetryAfter.Value <= DateTime.Now)
        {
            return null;
        }

        if (settings != null && !ResultMatchesSettings(result, settings))
        {
            return null;
        }

        return result;
    }

    public async Task<AiApiCheckResult> RecordOpenAiFailureAsync(
        OpenAiApiException exception,
        AiIrrigationOptions settings)
    {
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4.1-nano" : settings.Model.Trim();
        var keyLookup = ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable);
        var retryAfter = exception.IsQuotaOrRateLimited
            ? DateTime.Now.Add(QuotaOrRateLimitPause)
            : (DateTime?)null;

        var message = exception.IsQuotaExceeded
            ? $"OpenAI API quota is exhausted for this account/project. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
            : exception.IsQuotaOrRateLimited
                ? $"OpenAI API returned 429 Too Many Requests. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
                : exception.Message;

        return await StoreAndLogAsync(new AiApiCheckResult
        {
            IsConfigured = keyLookup.IsConfigured,
            IsSuccess = false,
            Model = model,
            KeySource = keyLookup.Source,
            StatusCode = exception.StatusCode,
            RetryAfter = retryAfter,
            Message = message
        });
    }

    public async Task<AiApiCheckResult> RecordOllamaFailureAsync(
        Exception exception,
        AiIrrigationOptions settings)
    {
        var model = GetConfiguredModel(settings);
        var baseUrl = GetOllamaBaseUrl(settings);
        var retryAfter = DateTime.Now.Add(OllamaFailurePause);
        var message = exception is TimeoutException
            ? $"Ollama analysis timed out at {baseUrl}. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
            : $"Ollama analysis failed at {baseUrl}: {exception.Message}. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues.";

        return await StoreAndLogAsync(new AiApiCheckResult
        {
            IsConfigured = true,
            IsSuccess = false,
            Model = model,
            KeySource = baseUrl,
            RetryAfter = retryAfter,
            Message = message
        });
    }

    public async Task<AiApiCheckResult> CheckAsync(
        AiIrrigationOptions? settingsOverride = null,
        CancellationToken cancellationToken = default)
    {
        await CheckLock.WaitAsync(cancellationToken);
        try
        {
            var settings = settingsOverride ?? await DataService.GetAiIrrigationOptionsAsync(Options.Value);
            return settings.UsesOllama
                ? await CheckOllamaAsync(settings, cancellationToken)
                : await CheckOpenAiAsync(settings, cancellationToken);
        }
        finally
        {
            CheckLock.Release();
        }
    }

    private async Task<AiApiCheckResult> CheckOpenAiAsync(
        AiIrrigationOptions settings,
        CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4.1-nano" : settings.Model.Trim();
        var keyLookup = ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable);

        if (!keyLookup.IsConfigured || string.IsNullOrWhiteSpace(keyLookup.ApiKey))
        {
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = false,
                IsSuccess = false,
                Model = model,
                KeySource = keyLookup.Source,
                Message = $"No OpenAI API key found. Configure {keyLookup.Source}."
            });
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OpenAiCheckTimeout);

        var requestBody = new
        {
            model,
            input = "Reply with exactly OK.",
            temperature = 0,
            max_output_tokens = 16,
            store = false
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyLookup.ApiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        try
        {
            var client = HttpClientFactory.CreateClient("OpenAI");
            using var response = await client.SendAsync(request, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                return await StoreAndLogAsync(new AiApiCheckResult
                {
                    IsConfigured = true,
                    IsSuccess = true,
                    Model = model,
                    KeySource = keyLookup.Source,
                    StatusCode = (int)response.StatusCode,
                    Message = $"OpenAI API key works, billing/quota is usable, and model {model} responded."
                });
            }

            var statusCode = (int)response.StatusCode;
            var apiError = ExtractApiError(responseBody);
            var retryAfter = statusCode == 429
                ? DateTime.Now.Add(QuotaOrRateLimitPause)
                : (DateTime?)null;
            var message = statusCode == 429 && apiError.Contains("quota", StringComparison.OrdinalIgnoreCase)
                ? $"OpenAI API quota is exhausted for this account/project. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
                : $"OpenAI API check failed with {statusCode} {response.ReasonPhrase}: {apiError}";

            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = keyLookup.Source,
                StatusCode = statusCode,
                RetryAfter = retryAfter,
                Message = message
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = keyLookup.Source,
                Message = $"OpenAI API check timed out after {OpenAiCheckTimeout.TotalSeconds:0} seconds."
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "OpenAI API health check failed");
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = keyLookup.Source,
                Message = $"OpenAI API check failed: {ex.Message}"
            });
        }
    }

    private async Task<AiApiCheckResult> CheckOllamaAsync(
        AiIrrigationOptions settings,
        CancellationToken cancellationToken)
    {
        var model = GetConfiguredModel(settings);
        var baseUrl = GetOllamaBaseUrl(settings);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OllamaCheckTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildOllamaApiUri(baseUrl, "api/tags"));

            var client = HttpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeout.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                if (!OllamaModelExists(responseBody, model))
                {
                    var retryAfter = DateTime.Now.Add(OllamaFailurePause);
                    return await StoreAndLogAsync(new AiApiCheckResult
                    {
                        IsConfigured = true,
                        IsSuccess = false,
                        Model = model,
                        KeySource = baseUrl,
                        StatusCode = (int)response.StatusCode,
                        RetryAfter = retryAfter,
                        Message = $"Ollama is reachable at {baseUrl}, but model {model} is not installed. Run 'ollama pull {model}'. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
                    });
                }

                return await StoreAndLogAsync(new AiApiCheckResult
                {
                    IsConfigured = true,
                    IsSuccess = true,
                    Model = model,
                    KeySource = baseUrl,
                    StatusCode = (int)response.StatusCode,
                    Message = $"Ollama is reachable at {baseUrl} and model {model} is installed."
                });
            }

            var failedRetryAfter = DateTime.Now.Add(OllamaFailurePause);
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = baseUrl,
                StatusCode = (int)response.StatusCode,
                RetryAfter = failedRetryAfter,
                Message = $"Ollama check failed with {(int)response.StatusCode} {response.ReasonPhrase}: {ExtractOllamaError(responseBody)}. AI decisions are paused until {failedRetryAfter:HH:mm}; rule fallback continues."
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var retryAfter = DateTime.Now.Add(OllamaFailurePause);
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = baseUrl,
                RetryAfter = retryAfter,
                Message = $"Ollama check timed out after {OllamaCheckTimeout.TotalSeconds:0} seconds at {baseUrl}. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
            });
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Ollama health check failed");
            var retryAfter = DateTime.Now.Add(OllamaFailurePause);
            return await StoreAndLogAsync(new AiApiCheckResult
            {
                IsConfigured = true,
                IsSuccess = false,
                Model = model,
                KeySource = baseUrl,
                RetryAfter = retryAfter,
                Message = $"Ollama check failed at {baseUrl}: {ex.Message}. AI decisions are paused until {retryAfter:HH:mm}; rule fallback continues."
            });
        }
    }

    private async Task<AiApiCheckResult> StoreAndLogAsync(AiApiCheckResult result)
    {
        result.CheckedAt = DateTime.Now;
        LastResult = result;

        await DataService.LogSystemMessageAsync(
            result.IsSuccess ? "INFO" : "WARNING",
            $"AI provider check: {result.Message}");

        return result;
    }

    private bool ResultMatchesSettings(AiApiCheckResult result, AiIrrigationOptions settings)
    {
        var expectedModel = GetConfiguredModel(settings);
        if (!string.Equals(result.Model, expectedModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expectedSource = settings.UsesOllama
            ? GetOllamaBaseUrl(settings)
            : ApiKeyProvider.GetApiKey(settings.ApiKeyEnvironmentVariable).Source;

        return string.Equals(result.KeySource, expectedSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetConfiguredModel(AiIrrigationOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Model))
        {
            return settings.Model.Trim();
        }

        return settings.UsesOpenAi ? "gpt-4.1-nano" : "qwen3:1.7b";
    }

    private static string GetOllamaBaseUrl(AiIrrigationOptions settings)
    {
        return string.IsNullOrWhiteSpace(settings.OllamaBaseUrl)
            ? "http://localhost:11434"
            : settings.OllamaBaseUrl.Trim().TrimEnd('/');
    }

    private static Uri BuildOllamaApiUri(string baseUrl, string relativePath)
    {
        if (!baseUrl.Contains("://", StringComparison.Ordinal))
        {
            baseUrl = $"http://{baseUrl}";
        }

        var baseUri = new Uri($"{baseUrl.TrimEnd('/')}/", UriKind.Absolute);
        return new Uri(baseUri, relativePath);
    }

    private static bool OllamaModelExists(string responseJson, string expectedModel)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var model in models.EnumerateArray())
            {
                if (ModelPropertyMatches(model, "name", expectedModel) ||
                    ModelPropertyMatches(model, "model", expectedModel))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool ModelPropertyMatches(JsonElement model, string propertyName, string expectedModel)
    {
        return model.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            IsSameOllamaModel(property.GetString(), expectedModel);
    }

    private static bool IsSameOllamaModel(string? candidate, string expected)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        var candidateName = candidate.Trim();
        var expectedName = expected.Trim();

        return string.Equals(candidateName, expectedName, StringComparison.OrdinalIgnoreCase) ||
            !expectedName.Contains(':', StringComparison.Ordinal) &&
                string.Equals(candidateName, $"{expectedName}:latest", StringComparison.OrdinalIgnoreCase) ||
            !candidateName.Contains(':', StringComparison.Ordinal) &&
                string.Equals($"{candidateName}:latest", expectedName, StringComparison.OrdinalIgnoreCase);
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
