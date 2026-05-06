using System.Text.Json.Serialization;

namespace IrrigationSystem.Web.Models;

public class WeatherForecastResult
{
    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = string.Empty;

    [JsonPropertyName("hourly")]
    public WeatherForecastHourly Hourly { get; set; } = new();
}

public class WeatherForecastHourly
{
    [JsonPropertyName("time")]
    public List<string> Time { get; set; } = new();

    [JsonPropertyName("temperature_2m")]
    public List<double> Temperature2m { get; set; } = new();

    [JsonPropertyName("precipitation_probability")]
    public List<double> PrecipitationProbability { get; set; } = new();

    [JsonPropertyName("precipitation")]
    public List<double> Precipitation { get; set; } = new();

    [JsonPropertyName("rain")]
    public List<double> Rain { get; set; } = new();

    [JsonPropertyName("showers")]
    public List<double> Showers { get; set; } = new();

    [JsonPropertyName("weather_code")]
    public List<int> WeatherCode { get; set; } = new();
}

public class WeatherHourForecast
{
    public DateTime Time { get; set; }
    public double? TemperatureC { get; set; }
    public double PrecipitationProbability { get; set; }
    public double Precipitation { get; set; }
    public double Rain { get; set; }
    public double Showers { get; set; }
    public int WeatherCode { get; set; }
}

public class WeatherWateringAdvice
{
    public bool IsIntegrationEnabled { get; set; }
    public bool IsWeatherDataAvailable { get; set; }
    public bool RainLikelySoon { get; set; }
    public int MaxRainProbabilityPercent { get; set; }
    public float ExpectedRainMm { get; set; }
    public bool HasTemperatureData { get; set; }
    public float? CurrentTemperatureC { get; set; }
    public float? AverageTemperatureC { get; set; }
    public float? MinTemperatureC { get; set; }
    public float? MaxTemperatureC { get; set; }
    public int LookaheadHours { get; set; }
    public string AdviceText { get; set; } = string.Empty;
    public bool ShouldBlockAutomaticWatering { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class WeatherIntegrationSettings
{
    public bool EnableWeatherIntegration { get; set; } = true;
    public double Latitude { get; set; } = 42.5048;
    public double Longitude { get; set; } = 27.4626;
    public int RainProbabilityThresholdPercent { get; set; } = 60;
    public int RainLookaheadHours { get; set; } = 6;
    public float ExpectedRainMmThreshold { get; set; } = 1.0f;
    public int ConfirmedWeatherPauseHours { get; set; } = 6;
}

public class ActiveWateringBlock
{
    public int Id { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime ActiveUntil { get; set; }
    public DateTime CreatedAt { get; set; }
}
