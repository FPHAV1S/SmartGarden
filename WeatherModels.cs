namespace IrrigationSystem.Web.Models;

public class WeatherIntegrationSettings
{
    public int Id { get; set; } = 1;
    public bool EnableWeatherIntegration { get; set; } = true;
    public double Latitude { get; set; } = 42.5048;
    public double Longitude { get; set; } = 27.4626;
    public int RainProbabilityThresholdPercent { get; set; } = 60;
    public int RainLookaheadHours { get; set; } = 6;
    public float ExpectedRainMmThreshold { get; set; } = 1.0f;
    public int ConfirmedWeatherPauseHours { get; set; } = 6;
}

public class WeatherWateringAdvice
{
    public bool IsWeatherDataAvailable { get; set; }
    public bool RainLikelySoon { get; set; }
    public int MaxRainProbabilityPercent { get; set; }
    public float ExpectedRainMm { get; set; }
    public int LookaheadHours { get; set; }
    public string AdviceText { get; set; } = string.Empty;
    public bool ShouldBlockAutomaticWatering { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class ActiveWateringBlock
{
    public int Id { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ActiveUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}