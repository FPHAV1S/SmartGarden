using System.Text.Json.Serialization;

namespace IrrigationSystem.Web.Models;

public class AiIrrigationDecision
{
    [JsonPropertyName("shouldWater")]
    public bool ShouldWater { get; set; }

    [JsonPropertyName("recommendedValveState")]
    public string RecommendedValveState { get; set; } = "OFF";

    [JsonPropertyName("recommendedDurationSeconds")]
    public int RecommendedDurationSeconds { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("learnedObservation")]
    public string LearnedObservation { get; set; } = string.Empty;

    [JsonPropertyName("suggestedMoistureThreshold")]
    public float SuggestedMoistureThreshold { get; set; }

    [JsonPropertyName("riskLevel")]
    public string RiskLevel { get; set; } = "LOW";
}
