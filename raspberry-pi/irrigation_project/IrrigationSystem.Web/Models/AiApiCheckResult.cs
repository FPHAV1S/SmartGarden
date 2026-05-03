namespace IrrigationSystem.Web.Models;

public class AiApiCheckResult
{
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public bool IsConfigured { get; set; }
    public bool IsSuccess { get; set; }
    public string Model { get; set; } = string.Empty;
    public string KeySource { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public DateTime? RetryAfter { get; set; }
    public string Message { get; set; } = string.Empty;
}
