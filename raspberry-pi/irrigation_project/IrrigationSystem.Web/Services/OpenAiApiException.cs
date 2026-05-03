namespace IrrigationSystem.Web.Services;

public sealed class OpenAiApiException : InvalidOperationException
{
    public OpenAiApiException(int statusCode, string? reasonPhrase, string apiError)
        : base($"OpenAI API returned {statusCode} {reasonPhrase}: {apiError}")
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase ?? string.Empty;
        ApiError = apiError;
    }

    public int StatusCode { get; }
    public string ReasonPhrase { get; }
    public string ApiError { get; }

    public bool IsQuotaOrRateLimited => StatusCode == 429;

    public bool IsQuotaExceeded =>
        IsQuotaOrRateLimited &&
        ApiError.Contains("quota", StringComparison.OrdinalIgnoreCase);
}
