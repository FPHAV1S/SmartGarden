using System.Net.Http.Json;
using System.Text.Json;
using IrrigationSystem.Web.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace IrrigationSystem.Web.Services;

public class WeatherForecastService
{
    private readonly HttpClient HttpClient;
    private readonly SensorDataService DataService;
    private readonly ILogger<WeatherForecastService> Logger;
    private readonly SemaphoreSlim AdviceLock = new(1, 1);
    private WeatherWateringAdvice? CachedAdvice;
    private DateTimeOffset CacheExpiry = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);

    public WeatherForecastService(HttpClient httpClient, SensorDataService dataService, ILogger<WeatherForecastService> logger)
    {
        HttpClient = httpClient;
        DataService = dataService;
        Logger = logger;
    }

    public async Task<WeatherWateringAdvice> GetAdviceAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && CachedAdvice != null && DateTimeOffset.UtcNow < CacheExpiry)
        {
            return CachedAdvice;
        }

        await AdviceLock.WaitAsync();
        try
        {
            if (!forceRefresh && CachedAdvice != null && DateTimeOffset.UtcNow < CacheExpiry)
            {
                return CachedAdvice;
            }

            var settings = await DataService.GetWeatherSettingsAsync();
            var advice = new WeatherWateringAdvice
            {
                IsWeatherDataAvailable = false,
                LookaheadHours = settings.RainLookaheadHours,
                LastUpdated = DateTime.Now,
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
                    ["hourly"] = "precipitation_probability,precipitation,rain,showers,weather_code",
                    ["forecast_hours"] = Math.Max(settings.RainLookaheadHours, 24).ToString(),
                    ["timezone"] = "auto"
                };

                var requestUri = QueryHelpers.AddQueryString("https://api.open-meteo.com/v1/forecast", query);
                using var response = await HttpClient.GetAsync(requestUri);
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
                var expectedRainMm = lookaheadForecasts.Sum(item => Math.Max(item.Precipitation, Math.Max(item.Rain, item.Showers)));
                var rainLikelySoon = maxRainProbability >= settings.RainProbabilityThresholdPercent || expectedRainMm >= settings.ExpectedRainMmThreshold;

                advice.IsWeatherDataAvailable = true;
                advice.RainLikelySoon = rainLikelySoon;
                advice.MaxRainProbabilityPercent = Convert.ToInt32(Math.Round(maxRainProbability));
                advice.ExpectedRainMm = Convert.ToSingle(expectedRainMm);
                advice.LookaheadHours = settings.RainLookaheadHours;
                advice.ShouldBlockAutomaticWatering = rainLikelySoon;
                advice.LastUpdated = DateTime.Now;
                advice.AdviceText = rainLikelySoon
                    ? $"Rain is likely soon, prefer not watering. Forecast shows {advice.MaxRainProbabilityPercent}% probability and {advice.ExpectedRainMm:F1}mm expected within {settings.RainLookaheadHours} hours."
                    : $"No rain is likely soon. Forecast shows {advice.MaxRainProbabilityPercent}% probability and {advice.ExpectedRainMm:F1}mm expected over the next {settings.RainLookaheadHours} hours.";

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
        var count = new[] { hourly.Time.Count, hourly.PrecipitationProbability.Count, hourly.Precipitation.Count, hourly.Rain.Count, hourly.Showers.Count, hourly.WeatherCode.Count }.Min();

        for (var index = 0; index < count; index++)
        {
            if (!DateTime.TryParse(hourly.Time[index], out var time))
            {
                continue;
            }

            forecasts.Add(new WeatherHourForecast
            {
                Time = time,
                PrecipitationProbability = hourly.PrecipitationProbability[index],
                Precipitation = hourly.Precipitation[index],
                Rain = hourly.Rain[index],
                Showers = hourly.Showers[index],
                WeatherCode = hourly.WeatherCode[index]
            });
        }

        return forecasts;
    }
}
