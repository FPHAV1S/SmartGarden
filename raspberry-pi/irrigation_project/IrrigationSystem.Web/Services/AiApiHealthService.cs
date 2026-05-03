using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.Extensions.Options;

namespace IrrigationSystem.Web.Services;

public class AiApiHealthService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
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

    public AiApiCheckResult? GetActivePause()
    {
        var result = LastResult;
        if (result == null ||
            result.IsSuccess ||
            !result.RetryAfter.HasValue ||
            result.RetryAfter.Value <= DateTime.Now)
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

    public async Task<AiApiCheckResult> CheckAsync(
        AiIrrigationOptions? settingsOverride = null,
        CancellationToken cancellationToken = default)
    {
        await CheckLock.WaitAsync(cancellationToken);
        try
        {
            var settings = settingsOverride ?? await DataService.GetAiIrrigationOptionsAsync(Options.Value);
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
            timeout.CancelAfter(CheckTimeout);

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
                    Message = $"OpenAI API check timed out after {CheckTimeout.TotalSeconds:0} seconds."
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
        finally
        {
            CheckLock.Release();
        }
    }

    private async Task<AiApiCheckResult> StoreAndLogAsync(AiApiCheckResult result)
    {
        result.CheckedAt = DateTime.Now;
        LastResult = result;

        await DataService.LogSystemMessageAsync(
            result.IsSuccess ? "INFO" : "WARNING",
            $"OpenAI API check: {result.Message}");

        return result;
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
