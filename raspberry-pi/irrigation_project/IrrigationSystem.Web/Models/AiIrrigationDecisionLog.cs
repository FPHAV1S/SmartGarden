namespace IrrigationSystem.Web.Models;

public class AiIrrigationDecisionLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int? SensorReadingId { get; set; }
    public int ZoneId { get; set; }
    public float? MoisturePercent { get; set; }
    public bool AiWasAttempted { get; set; }
    public bool AiShouldWater { get; set; }
    public bool FinalShouldWaterAfterSafety { get; set; }
    public string RecommendedValveState { get; set; } = "OFF";
    public string FinalValveState { get; set; } = "OFF";
    public int RecommendedDurationSeconds { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string LearnedObservation { get; set; } = string.Empty;
    public float? SuggestedMoistureThreshold { get; set; }
    public string RiskLevel { get; set; } = "LOW";
    public bool WasFallbackUsed { get; set; }
    public string? ErrorMessage { get; set; }
    public string SafetyNotes { get; set; } = string.Empty;
}
