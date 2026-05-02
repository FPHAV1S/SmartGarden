namespace IrrigationSystem.Web.Models;

public class RuleBasedIrrigationDecision
{
    public bool ShouldWater { get; set; }
    public int RecommendedDurationSeconds { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class IrrigationDecisionResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public int SensorReadingId { get; set; }
    public int ZoneId { get; set; }
    public float? MoisturePercent { get; set; }
    public bool RuleBasedShouldWater { get; set; }
    public string RuleBasedReason { get; set; } = string.Empty;
    public bool AiWasAttempted { get; set; }
    public AiIrrigationDecision? AiDecision { get; set; }
    public bool FinalShouldWaterAfterSafety { get; set; }
    public string RecommendedValveState { get; set; } = "OFF";
    public int RecommendedDurationSeconds { get; set; }
    public bool WasFallbackUsed { get; set; }
    public string? ErrorMessage { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string LearnedObservation { get; set; } = string.Empty;
    public float? SuggestedMoistureThreshold { get; set; }
    public double Confidence { get; set; }
    public string RiskLevel { get; set; } = "LOW";
    public string SafetyNotes { get; set; } = string.Empty;
}
