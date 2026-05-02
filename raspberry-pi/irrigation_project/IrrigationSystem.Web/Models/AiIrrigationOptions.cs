namespace IrrigationSystem.Web.Models;

public class AiIrrigationOptions
{
    public const string SectionName = "AiIrrigation";

    public bool EnableAiDecisionMaking { get; set; }
    public string Model { get; set; } = "gpt-4.1-nano";
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENAI_API_KEY";
    public int MinimumMinutesBetweenAiCalls { get; set; } = 15;
    public int RecentHistoryHours { get; set; } = 24;
    public int RecentWateringEvents { get; set; } = 10;
    public int PatternMemoryCount { get; set; } = 5;
    public float HighMoistureBlockPercent { get; set; } = 60.0f;
    public int MaxDurationSeconds { get; set; } = 120;
    public double LowConfidenceThreshold { get; set; } = 0.65;
    public int MinimumMinutesBetweenWaterings { get; set; } = 120;
    public int DefaultFallbackDurationSeconds { get; set; } = 10;
}
