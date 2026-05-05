namespace IrrigationSystem.Web.Models;

public class AiIrrigationOptions
{
    public const string SectionName = "AiIrrigation";
    public const string OpenAiProvider = "OpenAI";
    public const string OllamaProvider = "Ollama";

    public bool EnableAiDecisionMaking { get; set; }
    public string Provider { get; set; } = OllamaProvider;
    public string Model { get; set; } = "qwen3:1.7b";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
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

    public bool UsesOpenAi =>
        string.Equals(Provider, OpenAiProvider, StringComparison.OrdinalIgnoreCase);

    public bool UsesOllama =>
        string.Equals(Provider, OllamaProvider, StringComparison.OrdinalIgnoreCase);

    public bool RequiresApiKey => UsesOpenAi;
}
