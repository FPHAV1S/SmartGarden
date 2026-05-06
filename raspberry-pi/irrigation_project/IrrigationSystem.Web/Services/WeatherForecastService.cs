using System.Globalization;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace IrrigationSystem.Web.Services;

public class WeatherForecastService
{
    public const string ClientName = "OpenMeteo";

    private readonly IHttpClientFactory HttpClientFactory;
    private readonly SensorDataService DataService;
    private readonly ILogger<WeatherForecastService> Logger;
    private readonly SemaphoreSlim AdviceLock = new(1, 1);
    private WeatherWateringAdvice? CachedAdvice;
    private DateTimeOffset CacheExpiry = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);
    private static readonly WeatherIntegrationSettings DefaultSettings = new();

    public WeatherForecastService(IHttpClientFactory httpClientFactory, SensorDataService dataService, ILogger<WeatherForecastService> logger)
    {
        HttpClientFactory = httpClientFactory;
        DataService = dataService;
        Logger = logger;
    }

    public void ClearCache()
    {
        CachedAdvice = null;
        CacheExpiry = DateTimeOffset.MinValue;
    }

    public async Task<WeatherWateringAdvice> GetAdviceAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && CachedAdvice != null && DateTimeOffset.UtcNow < CacheExpiry)
        {
            CachedAdvice.LastUpdated = DateTime.UtcNow;
            return CachedAdvice;
        }

        await AdviceLock.WaitAsync();
        try
        {
            if (!forceRefresh && CachedAdvice != null && DateTimeOffset.UtcNow < CacheExpiry)
            {
                CachedAdvice.LastUpdated = DateTime.UtcNow;
                return CachedAdvice;
            }

            var settings = NormalizeSettings(await DataService.GetWeatherSettingsAsync());
            var advice = new WeatherWateringAdvice
            {
                IsIntegrationEnabled = settings.EnableWeatherIntegration,
                IsWeatherDataAvailable = false,
                LookaheadHours = settings.RainLookaheadHours,
                LastUpdated = DateTime.UtcNow,
                AdviceText = "Weather integration is disabled or unavailable.",
                ShouldBlockAutomaticWatering = false
            };

            if (!settings.EnableWeatherIntegration)
            {
                advice.AdviceText = "Weather integration is disabled.";
                CachedAdvice = advice;
                CacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);
                return advice;
            }

            try
            {
                var query = new Dictionary<string, string?>
                {
                    ["latitude"] = settings.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["longitude"] = settings.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["hourly"] = "temperature_2m,precipitation_probability,precipitation,rain,showers,weather_code",
                    ["forecast_hours"] = Math.Max(settings.RainLookaheadHours, 24).ToString(CultureInfo.InvariantCulture),
                    ["timezone"] = "auto"
                };

                var requestUri = QueryHelpers.AddQueryString("forecast", query);
                var httpClient = HttpClientFactory.CreateClient(ClientName);
                using var response = await httpClient.GetAsync(requestUri);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning("Weather API request failed with status {StatusCode}: {Body}", response.StatusCode, responseText);
                    advice.AdviceText = "Weather forecast unavailable; continuing with normal irrigation safety.";
                    CachedAdvice = advice;
                    CacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);
                    return advice;
                }

                var forecast = JsonSerializer.Deserialize<WeatherForecastResult>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (forecast?.Hourly == null || forecast.Hourly.Time.Count == 0)
                {
                    Logger.LogWarning("Weather API returned no hourly forecast data.");
                    advice.AdviceText = "Weather forecast returned no data; continuing with normal irrigation safety.";
                    CachedAdvice = advice;
                    CacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);
                    return advice;
                }

                var hourlyForecasts = BuildHourlyForecasts(forecast.Hourly);
                var limit = Math.Min(settings.RainLookaheadHours, hourlyForecasts.Count);
                var lookaheadForecasts = hourlyForecasts.Take(limit).ToList();

                if (lookaheadForecasts.Count == 0)
                {
                    advice.AdviceText = "Weather forecast does not contain enough hourly data; continuing with normal irrigation safety.";
                    CachedAdvice = advice;
                    CacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);
                    return advice;
                }

                var maxRainProbability = lookaheadForecasts.Max(item => item.PrecipitationProbability);
                var expectedRainMm = lookaheadForecasts.Sum(GetExpectedRainMm);
                var temperatureValues = lookaheadForecasts
                    .Select(item => item.TemperatureC)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToList();
                var rainLikelySoon = maxRainProbability >= settings.RainProbabilityThresholdPercent || expectedRainMm >= settings.ExpectedRainMmThreshold;

                advice.IsIntegrationEnabled = settings.EnableWeatherIntegration;
                advice.IsWeatherDataAvailable = true;
                advice.RainLikelySoon = rainLikelySoon;
                advice.MaxRainProbabilityPercent = Convert.ToInt32(Math.Round(maxRainProbability));
                advice.ExpectedRainMm = Convert.ToSingle(expectedRainMm);
                advice.HasTemperatureData = temperatureValues.Count > 0;
                advice.CurrentTemperatureC = lookaheadForecasts.FirstOrDefault(item => item.TemperatureC.HasValue)?.TemperatureC is double currentTemp
                    ? Convert.ToSingle(currentTemp)
                    : null;
                advice.AverageTemperatureC = temperatureValues.Count > 0
                    ? Convert.ToSingle(temperatureValues.Average())
                    : null;
                advice.MinTemperatureC = temperatureValues.Count > 0
                    ? Convert.ToSingle(temperatureValues.Min())
                    : null;
                advice.MaxTemperatureC = temperatureValues.Count > 0
                    ? Convert.ToSingle(temperatureValues.Max())
                    : null;
                advice.LookaheadHours = settings.RainLookaheadHours;
                advice.ShouldBlockAutomaticWatering = rainLikelySoon;
                advice.LastUpdated = DateTime.UtcNow;
                advice.AdviceText = rainLikelySoon
                    ? $"Rain is likely soon, prefer not watering. Forecast shows {advice.MaxRainProbabilityPercent}% probability and {FormatRainMm(advice.ExpectedRainMm)} expected within {settings.RainLookaheadHours} hours."
                    : $"No rain is likely soon. Forecast shows {advice.MaxRainProbabilityPercent}% probability and {FormatRainMm(advice.ExpectedRainMm)} expected over the next {settings.RainLookaheadHours} hours.";

                CachedAdvice = advice;
                CacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);
                return advice;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to fetch weather forecast.");
                advice.AdviceText = "Weather forecast failed; continuing with normal irrigation safety.";
                CachedAdvice = advice;
                CacheExpiry = DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(5));
                return advice;
            }
        }
        finally
        {
            AdviceLock.Release();
        }
    }

    private static List<WeatherHourForecast> BuildHourlyForecasts(WeatherForecastHourly hourly)
    {
        var forecasts = new List<WeatherHourForecast>();
        var count = hourly.Time.Count;

        for (var index = 0; index < count; index++)
        {
            if (!DateTime.TryParse(hourly.Time[index], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
            {
                continue;
            }

            forecasts.Add(new WeatherHourForecast
            {
                Time = time,
                TemperatureC = GetNullableValueOrDefault(hourly.Temperature2m, index),
                PrecipitationProbability = GetValueOrDefault(hourly.PrecipitationProbability, index),
                Precipitation = GetValueOrDefault(hourly.Precipitation, index),
                Rain = GetValueOrDefault(hourly.Rain, index),
                Showers = GetValueOrDefault(hourly.Showers, index),
                WeatherCode = GetValueOrDefault(hourly.WeatherCode, index)
            });
        }

        return forecasts;
    }

    private static WeatherIntegrationSettings NormalizeSettings(WeatherIntegrationSettings settings)
    {
        return new WeatherIntegrationSettings
        {
            EnableWeatherIntegration = settings.EnableWeatherIntegration,
            Latitude = NormalizeCoordinate(settings.Latitude, -90, 90, DefaultSettings.Latitude),
            Longitude = NormalizeCoordinate(settings.Longitude, -180, 180, DefaultSettings.Longitude),
            RainProbabilityThresholdPercent = Math.Clamp(settings.RainProbabilityThresholdPercent, 0, 100),
            RainLookaheadHours = Math.Clamp(settings.RainLookaheadHours, 1, 48),
            ExpectedRainMmThreshold = Math.Clamp(settings.ExpectedRainMmThreshold, 0, 100),
            ConfirmedWeatherPauseHours = Math.Clamp(settings.ConfirmedWeatherPauseHours, 1, 72)
        };
    }

    private static double NormalizeCoordinate(double value, double min, double max, double fallback)
    {
        return double.IsFinite(value) && value >= min && value <= max
            ? value
            : fallback;
    }

    private static double GetExpectedRainMm(WeatherHourForecast forecast)
    {
        var rainComponents = Math.Max(0, forecast.Rain) + Math.Max(0, forecast.Showers);
        return rainComponents > 0
            ? rainComponents
            : Math.Max(0, forecast.Precipitation);
    }

    private static string FormatRainMm(float rainMm)
    {
        return $"{rainMm.ToString("F1", CultureInfo.InvariantCulture)} mm";
    }

    private static double GetValueOrDefault(IReadOnlyList<double> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : 0;
    }

    private static double? GetNullableValueOrDefault(IReadOnlyList<double> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : null;
    }

    private static int GetValueOrDefault(IReadOnlyList<int> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : 0;
    }
}
