using System.Text.Json;
using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public class WeatherForecastService
{
    private readonly HttpClient HttpClient;
    private readonly SensorDataService DataService;
    private readonly ILogger<WeatherForecastService> Logger;
    
    private WeatherWateringAdvice? CachedAdvice;
    private DateTime LastFetchTime = DateTime.MinValue;
    private readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(20);
    private readonly SemaphoreSlim FetchLock = new(1, 1);

    public WeatherForecastService(IHttpClientFactory httpClientFactory, SensorDataService dataService, ILogger<WeatherForecastService> logger)
    {
        HttpClient = httpClientFactory.CreateClient("Weather");
        DataService = dataService;
        Logger = logger;
    }

    public async Task<WeatherWateringAdvice> GetAdviceAsync(bool forceRefresh = false)
    {
        var settings = await DataService.GetWeatherSettingsAsync();
        
        if (!settings.EnableWeatherIntegration)
        {
            return CreateFallbackAdvice("Weather integration is disabled.");
        }

        if (!forceRefresh && CachedAdvice != null && DateTime.Now - LastFetchTime < CacheDuration)
        {
            return CachedAdvice;
        }

        await FetchLock.WaitAsync();
        try
        {
            if (!forceRefresh && CachedAdvice != null && DateTime.Now - LastFetchTime < CacheDuration)
            {
                return CachedAdvice;
            }

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={settings.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={settings.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&hourly=precipitation_probability,precipitation,rain,showers,weather_code&forecast_hours={Math.Max(1, settings.RainLookaheadHours)}&timezone=auto";
            
            var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Open-Meteo API failed with status {Status}", response.StatusCode);
                return CreateFallbackAdvice("Failed to fetch weather data from Open-Meteo.");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            if (!doc.RootElement.TryGetProperty("hourly", out var hourly))
            {
                return CreateFallbackAdvice("Invalid weather data format received.");
            }

            var probabilities = hourly.GetProperty("precipitation_probability").EnumerateArray().Select(e => e.GetInt32()).ToList();
            var precipitations = hourly.GetProperty("precipitation").EnumerateArray().Select(e => e.GetSingle()).ToList();

            var maxProb = probabilities.Count > 0 ? probabilities.Max() : 0;
            var totalPrecip = precipitations.Count > 0 ? precipitations.Sum() : 0f;

            var rainLikely = maxProb >= settings.RainProbabilityThresholdPercent || totalPrecip >= settings.ExpectedRainMmThreshold;

            CachedAdvice = new WeatherWateringAdvice
            {
                IsWeatherDataAvailable = true,
                RainLikelySoon = rainLikely,
                MaxRainProbabilityPercent = maxProb,
                ExpectedRainMm = totalPrecip,
                LookaheadHours = settings.RainLookaheadHours,
                ShouldBlockAutomaticWatering = rainLikely,
                LastUpdated = DateTime.Now,
                AdviceText = rainLikely 
                    ? $"Rain is likely in the next {settings.RainLookaheadHours} hours ({maxProb}% prob, {totalPrecip:F1}mm expected)." 
                    : $"No significant rain expected in the next {settings.RainLookaheadHours} hours."
            };
            
            LastFetchTime = DateTime.Now;
            return CachedAdvice;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error fetching weather data.");
            return CreateFallbackAdvice("Error fetching weather data.");
        }
        finally
        {
            FetchLock.Release();
        }
    }
    
    private WeatherWateringAdvice CreateFallbackAdvice(string reason)
    {
        return new WeatherWateringAdvice
        {
            IsWeatherDataAvailable = false,
            AdviceText = reason,
            LastUpdated = DateTime.Now
        };
    }
}