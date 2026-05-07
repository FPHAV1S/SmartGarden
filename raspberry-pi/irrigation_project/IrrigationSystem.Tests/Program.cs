using System.Text.Json;
using IrrigationSystem.Web.Controllers;
using IrrigationSystem.Web.Models;
using IrrigationSystem.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var tests = new TestCase[]
{
    new("SystemSettings defaults match seeded settings", Tests.SystemSettingsDefaultsMatchSeedData),
    new("AuthService hashes passwords with BCrypt", Tests.AuthServiceHashesPasswords),
    new("Sensor API rejects missing request bodies", Tests.SensorApiRejectsMissingRequestBody),
    new("Sensor API rejects invalid zone IDs before database access", Tests.SensorApiRejectsInvalidZoneId),
    new("Sensor API rejects incomplete readings before database access", Tests.SensorApiRejectsIncompleteReadings),
    new("AI response parser accepts valid JSON", Tests.AiParserAcceptsValidJson),
    new("Invalid AI JSON falls back to rules", Tests.InvalidAiJsonFallsBackToRules),
    new("High moisture safety blocks watering", Tests.HighMoistureSafetyBlocksWatering),
    new("AI duration is clamped to max safety limit", Tests.AiDurationIsClampedToMaxSafetyLimit),
    new("Zero-duration watering decisions are forced off", Tests.ZeroDurationWateringDecisionsAreForcedOff),
    new("Low AI confidence falls back to rules", Tests.LowAiConfidenceFallsBackToRules),
    new("Watering cooldown blocks repeated automatic watering when enabled", Tests.WateringCooldownBlocksRepeatedAutomaticWateringWhenEnabled),
    new("Watering cooldown can be disabled", Tests.WateringCooldownCanBeDisabled),
    new("Cooldown reset allows a fresh automatic watering decision", Tests.CooldownResetAllowsFreshAutomaticWateringDecision),
    new("API failure falls back to rules", Tests.ApiFailureFallsBackToRules),
    new("Open-Meteo hourly JSON maps to weather model", Tests.OpenMeteoHourlyJsonMapsToWeatherModel),
    new("Weather forecast exposes temperature for verification", Tests.WeatherForecastExposesTemperatureForVerification),
    new("Weather forecast safety blocks automatic watering", Tests.WeatherForecastSafetyBlocksAutomaticWatering),
    new("Database schema contains required tables", Tests.DatabaseSchemaContainsRequiredTables),
    new("Web appsettings uses the expected local database", Tests.WebAppSettingsUseExpectedDatabase),
    new("Setup script keeps the PostgreSQL password aligned with appsettings", Tests.SetupScriptKeepsDatabasePasswordAligned),
    new("Setup and docs use postgres as the default web user", Tests.SetupAndDocsUsePostgresWebLogin),
    new("Dashboard moisture bar uses CSS-safe percentages", Tests.DashboardMoistureBarUsesCssSafePercentages),
    new("History chart averages dense readings", Tests.HistoryChartAveragesDenseReadings),
    new("Zone management uses configured zones and supports creation", Tests.ZoneManagementUsesConfiguredZonesAndSupportsCreation),
    new("Database cleanup supports one-second retention", Tests.DatabaseCleanupSupportsOneSecondRetention),
    new("History and login cutoffs are parameterized", Tests.HistoryAndLoginCutoffsAreParameterized),
    new("Generated build outputs are ignored", Tests.GeneratedBuildOutputsAreIgnored),
    new("Settings page exposes AI management", Tests.SettingsPageExposesAiManagement),
    new("Fallback-only AI decisions stay out of AI logs", Tests.FallbackOnlyAiDecisionsStayOutOfAiLogs),
    new("ESP32 sketch posts to the web sensor API", Tests.Esp32SketchPostsToWebSensorApi),
    new("ESP32 sketch subscribes to web valve commands", Tests.Esp32SketchSubscribesToWebValveCommands),
    new("Sensor API applies safe watering decisions through MQTT", Tests.SensorApiAppliesSafeWateringDecisionsThroughMqtt),
    new("Watering events start only after MQTT valve commands succeed", Tests.WateringEventsStartAfterMqttOpenSucceeds),
    new("Web host allows slow embedded request bodies", Tests.WebHostAllowsSlowEmbeddedRequestBodies),
    new("Web host is configured for port 5000", Tests.WebHostUsesPort5000)
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine($"     {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"All {tests.Length} tests passed."
    : $"{failed} of {tests.Length} tests failed.");

return failed == 0 ? 0 : 1;

internal sealed record TestCase(string Name, Func<Task> Run);

internal static class Tests
{
    public static Task SystemSettingsDefaultsMatchSeedData()
    {
        var settings = new SystemSettings();

        Assert.True(settings.AutoWateringEnabled, "Auto watering should be enabled by default.");
        Assert.Equal("auto", settings.SystemMode, "Default system mode should match setup.sh.");
        Assert.Equal(10, settings.DefaultWateringDuration, "Default watering duration should match setup.sh.");
        Assert.False(settings.NightModeEnabled, "Night mode should be disabled by default.");
        Assert.Equal(18, settings.NightModeStartHour, "Night mode start hour should match setup.sh.");
        Assert.Equal(8, settings.NightModeEndHour, "Night mode end hour should match setup.sh.");
        Assert.False(settings.EcoModeEnabled, "Eco mode should be disabled by default.");

        return Task.CompletedTask;
    }

    public static Task AuthServiceHashesPasswords()
    {
        var service = new AuthService("Host=unused", new TestLogger<AuthService>());
        var hash = service.HashPassword("garden-secret");

        Assert.True(BCrypt.Net.BCrypt.Verify("garden-secret", hash), "Generated hash should validate the original password.");
        Assert.False(BCrypt.Net.BCrypt.Verify("wrong-secret", hash), "Generated hash should reject a different password.");

        return Task.CompletedTask;
    }

    public static async Task SensorApiRejectsMissingRequestBody()
    {
        var controller = CreateApiController();

        var result = await controller.CreateSensorReading(null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Request body is required", Serialize(badRequest.Value));
    }

    public static async Task SensorApiRejectsInvalidZoneId()
    {
        var controller = CreateApiController();
        var request = new SensorReadingRequest
        {
            ZoneId = 0,
            SoilMoisture = 44.5f,
            Temperature = 24.2f,
            Humidity = 61.0f
        };

        var result = await controller.CreateSensorReading(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("zoneId must be greater than 0", Serialize(badRequest.Value));
    }

    public static async Task SensorApiRejectsIncompleteReadings()
    {
        var controller = CreateApiController();
        var request = new SensorReadingRequest
        {
            ZoneId = 1,
            SoilMoisture = 44.5f,
            Temperature = 24.2f
        };

        var result = await controller.CreateSensorReading(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("temperature, humidity, and soilMoisture are required", Serialize(badRequest.Value));
    }

    public static Task AiParserAcceptsValidJson()
    {
        var json = """
        {
          "shouldWater": true,
          "recommendedValveState": "ON",
          "recommendedDurationSeconds": 10,
          "confidence": 0.82,
          "reason": "Soil moisture is falling and below the safe range.",
          "learnedObservation": "This zone dries quickly after several low readings.",
          "suggestedMoistureThreshold": 30,
          "riskLevel": "LOW"
        }
        """;

        var parsed = AiIrrigationDecisionParser.TryParse(json, out var decision, out var error);

        Assert.True(parsed, $"Expected valid AI JSON to parse. Error: {error}");
        Assert.True(decision.ShouldWater, "Parsed AI decision should recommend watering.");
        Assert.Equal("ON", decision.RecommendedValveState, "Valve state should be normalized.");
        Assert.Equal(10, decision.RecommendedDurationSeconds, "Duration should match AI JSON.");

        return Task.CompletedTask;
    }

    public static Task InvalidAiJsonFallsBackToRules()
    {
        var parsed = AiIrrigationDecisionParser.TryParse("{\"shouldWater\":\"yes\"}", out var _, out var parseError);
        Assert.False(parsed, "Invalid AI JSON should be rejected.");

        var result = ApplySafety(null, aiErrorMessage: parseError);

        Assert.True(result.WasFallbackUsed, "Invalid AI JSON should use rule fallback.");
        Assert.True(result.FinalShouldWaterAfterSafety, "Rule fallback should still allow watering when moisture is low.");
        Assert.Contains("shouldWater must be a boolean", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task HighMoistureSafetyBlocksWatering()
    {
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 30, confidence: 0.95);
        var result = ApplySafety(aiDecision, moisture: 65);

        Assert.False(result.FinalShouldWaterAfterSafety, "Safety must block watering when moisture is already high.");
        Assert.Equal("OFF", result.RecommendedValveState, "High moisture should force the valve off.");
        Assert.Contains($"moisture {65f:F1}%", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task AiDurationIsClampedToMaxSafetyLimit()
    {
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 180, confidence: 0.95);
        var result = ApplySafety(aiDecision, moisture: 20);

        Assert.True(result.FinalShouldWaterAfterSafety, "AI watering should be allowed when moisture is low and confidence is high.");
        Assert.Equal(120, result.RecommendedDurationSeconds, "Safety must clamp duration to 120 seconds.");
        Assert.Contains("clamped watering duration", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task ZeroDurationWateringDecisionsAreForcedOff()
    {
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 0, confidence: 0.95);
        var result = ApplySafety(aiDecision, moisture: 20);

        Assert.False(result.FinalShouldWaterAfterSafety, "Safety should not allow an ON decision with zero duration.");
        Assert.Equal("OFF", result.RecommendedValveState, "Zero-duration watering should force the valve off.");
        Assert.Equal(0, result.RecommendedDurationSeconds, "Zero-duration watering should keep duration at zero.");
        Assert.Contains("recommended duration was 0 seconds", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task LowAiConfidenceFallsBackToRules()
    {
        var aiDecision = CreateAiDecision(shouldWater: false, durationSeconds: 0, confidence: 0.2);
        var result = ApplySafety(aiDecision, moisture: 20);

        Assert.True(result.WasFallbackUsed, "Low confidence should use rule fallback.");
        Assert.True(result.FinalShouldWaterAfterSafety, "Low-confidence AI OFF should not override a low-moisture rule fallback.");
        Assert.Contains($"below {0.65:F2}", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task WateringCooldownBlocksRepeatedAutomaticWateringWhenEnabled()
    {
        var recentEvents = new[]
        {
            new IrrigationEvent
            {
                Id = 25,
                ZoneId = 1,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                DurationSec = 10,
                TriggerReason = "Auto"
            }
        };
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 30, confidence: 0.95);

        var result = ApplySafety(aiDecision, moisture: 20, recentEvents: recentEvents);

        Assert.False(result.FinalShouldWaterAfterSafety, "Cooldown should block repeated automatic watering by default.");
        Assert.Equal("OFF", result.RecommendedValveState, "Cooldown should force the final valve state off.");
        Assert.Contains("cooldown is 120 minutes", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task WateringCooldownCanBeDisabled()
    {
        var recentEvents = new[]
        {
            new IrrigationEvent
            {
                Id = 26,
                ZoneId = 1,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                DurationSec = 10,
                TriggerReason = "Auto"
            }
        };
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 30, confidence: 0.95);

        var result = ApplySafety(
            aiDecision,
            moisture: 20,
            recentEvents: recentEvents,
            enableWateringCooldown: false);

        Assert.True(result.FinalShouldWaterAfterSafety, "Disabling the cooldown should allow an otherwise safe watering decision.");
        Assert.Equal("ON", result.RecommendedValveState, "Disabled cooldown should not force the final valve state off.");
        Assert.False(result.SafetyNotes.Contains("cooldown", StringComparison.OrdinalIgnoreCase), "Disabled cooldown should not add cooldown safety notes.");

        return Task.CompletedTask;
    }

    public static Task CooldownResetAllowsFreshAutomaticWateringDecision()
    {
        var recentEvents = new[]
        {
            new IrrigationEvent
            {
                Id = 27,
                ZoneId = 1,
                StartedAt = DateTime.UtcNow.AddMinutes(-10),
                DurationSec = 10,
                TriggerReason = "Auto"
            }
        };
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 30, confidence: 0.95);

        var result = ApplySafety(
            aiDecision,
            moisture: 20,
            recentEvents: recentEvents,
            cooldownsResetAt: DateTime.UtcNow.AddMinutes(-1));

        Assert.True(result.FinalShouldWaterAfterSafety, "Resetting cooldowns should ignore watering events before the reset timestamp.");
        Assert.Equal("ON", result.RecommendedValveState, "Cooldown reset should let an otherwise safe decision open the valve.");
        Assert.False(result.SafetyNotes.Contains("cooldown", StringComparison.OrdinalIgnoreCase), "Reset cooldown should not report a stale cooldown block.");

        return Task.CompletedTask;
    }

    public static Task ApiFailureFallsBackToRules()
    {
        var result = ApplySafety(null, aiWasAttempted: true, aiErrorMessage: "OpenAI API returned 503 Service Unavailable");

        Assert.True(result.WasFallbackUsed, "API failures should use rule fallback.");
        Assert.True(result.FinalShouldWaterAfterSafety, "Rule fallback should still produce a final decision.");
        Assert.Contains("OpenAI API returned 503", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task OpenMeteoHourlyJsonMapsToWeatherModel()
    {
        var json = """
        {
          "timezone": "Europe/Sofia",
          "hourly": {
            "time": ["2026-05-05T12:00"],
            "temperature_2m": [24.6],
            "precipitation_probability": [82],
            "precipitation": [3.4],
            "rain": [2.1],
            "showers": [1.3],
            "weather_code": [61]
          }
        }
        """;

        var forecast = JsonSerializer.Deserialize<WeatherForecastResult>(json);

        Assert.Equal("Europe/Sofia", forecast?.Timezone, "Timezone should map from Open-Meteo JSON.");
        Assert.Equal(1, forecast?.Hourly.PrecipitationProbability.Count ?? 0, "Precipitation probability should map from snake_case JSON.");
        Assert.Equal(82d, forecast!.Hourly.PrecipitationProbability[0], "Rain probability should be preserved.");
        Assert.Equal(24.6d, forecast.Hourly.Temperature2m[0], "Hourly forecast temperature should map from temperature_2m JSON.");
        Assert.Equal(3.4d, forecast.Hourly.Precipitation[0], "Expected precipitation should be preserved.");
        Assert.Equal(61, forecast.Hourly.WeatherCode[0], "Weather code should be preserved.");

        return Task.CompletedTask;
    }

    public static Task WeatherForecastExposesTemperatureForVerification()
    {
        var weatherService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "WeatherForecastService.cs"));
        var weatherModels = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Models",
            "WeatherModels.cs"));
        var settingsPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Settings.razor"));
        var dashboardPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Index.razor"));

        Assert.Contains("temperature_2m,precipitation_probability", weatherService);
        Assert.Contains("Temperature2m", weatherModels);
        Assert.Contains("CurrentTemperatureC", weatherModels);
        Assert.Contains("AverageTemperatureC", weatherModels);
        Assert.Contains("Forecast temperature now:", settingsPage);
        Assert.Contains("Range: @FormatTemperatureRange", settingsPage);
        Assert.Contains("Forecast Temp:", dashboardPage);

        return Task.CompletedTask;
    }

    public static Task WeatherForecastSafetyBlocksAutomaticWatering()
    {
        var aiDecision = CreateAiDecision(shouldWater: true, durationSeconds: 30, confidence: 0.95);
        var weatherAdvice = new WeatherWateringAdvice
        {
            IsIntegrationEnabled = true,
            IsWeatherDataAvailable = true,
            RainLikelySoon = true,
            MaxRainProbabilityPercent = 82,
            ExpectedRainMm = 3.4f,
            LookaheadHours = 6,
            AdviceText = "Rain is likely soon, prefer not watering.",
            ShouldBlockAutomaticWatering = true
        };

        var result = ApplySafety(aiDecision, moisture: 20, aiWasAttempted: true, weatherAdvice: weatherAdvice);

        Assert.False(result.FinalShouldWaterAfterSafety, "Weather safety should block automatic watering even when AI recommends watering.");
        Assert.Equal("OFF", result.RecommendedValveState, "Weather safety should force the valve off.");
        Assert.Contains("weather forecasts rain", result.SafetyNotes);

        return Task.CompletedTask;
    }

    public static Task DatabaseSchemaContainsRequiredTables()
    {
        var schema = File.ReadAllText(FindRepoFile("raspberry-pi", "irrigation_db.sql"));
        var requiredTables = new[]
        {
            "zones",
            "sensor_readings",
            "irrigation_events",
            "ai_irrigation_decision_logs",
            "ai_irrigation_settings",
            "pattern_memory",
            "system_settings",
            "system_logs",
            "users",
            "login_attempts"
        };

        foreach (var table in requiredTables)
        {
            Assert.Contains($"CREATE TABLE public.{table}", schema);
        }

        Assert.Contains("FOREIGN KEY (zone_id) REFERENCES public.zones(id)", schema);
        Assert.Contains("users_username_key UNIQUE (username)", schema);
        Assert.Contains("pressure double precision", schema);
        Assert.Contains("sensor_device_name character varying(80)", schema);
        Assert.Contains("device character varying(80)", schema);
        Assert.Contains("cooldowns_reset_at timestamp without time zone", schema);
        Assert.Contains("INSERT INTO public.system_settings", schema);
        Assert.Contains("OPENAI_API_KEY", File.ReadAllText(FindRepoFile("README.md")));

        return Task.CompletedTask;
    }

    public static Task WebAppSettingsUseExpectedDatabase()
    {
        var appsettingsPath = FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));

        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("DefaultConnection")
            .GetString();
        var aiSettings = document.RootElement.GetProperty("AiIrrigation");

        Assert.Equal(
            "Host=localhost;Database=irrigation_db;Username=postgres;Password=1203",
            connectionString,
            "Web app should point at the local irrigation database used by setup.sh.");
        Assert.Equal(
            "Ollama",
            aiSettings.GetProperty("Provider").GetString(),
            "Web app should default to the local Ollama provider.");
        Assert.Equal(
            "qwen3:1.7b",
            aiSettings.GetProperty("Model").GetString(),
            "Web app should default to the installed local Qwen model.");
        Assert.Equal(
            "http://localhost:11434",
            aiSettings.GetProperty("OllamaBaseUrl").GetString(),
            "Web app should point at the default local Ollama API.");
        Assert.True(
            aiSettings.GetProperty("EnableWateringCooldown").GetBoolean(),
            "Watering cooldown should be enabled by default.");

        return Task.CompletedTask;
    }

    public static Task SetupScriptKeepsDatabasePasswordAligned()
    {
        var setup = File.ReadAllText(FindRepoFile("raspberry-pi", "setup.sh"));
        var appsettings = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "appsettings.json"));
        var workerAppsettings = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Worker",
            "appsettings.json"));

        Assert.Contains("DB_PASSWORD=\"1203\"", setup);
        Assert.Contains("ALTER USER postgres PASSWORD '$DB_PASSWORD';", setup);
        Assert.Contains("Password=1203", appsettings);
        Assert.Contains("Password=1203", workerAppsettings);
        Assert.False(setup.Contains("PASSWORD 'postgres';"), "setup.sh should not reset the database role to a different password.");

        return Task.CompletedTask;
    }

    public static Task SetupAndDocsUsePostgresWebLogin()
    {
        var setup = File.ReadAllText(FindRepoFile("raspberry-pi", "setup.sh"));
        var readme = File.ReadAllText(FindRepoFile("README.md"));

        Assert.Contains("VALUES ('postgres'", setup);
        Assert.Contains("Username: postgres", readme);
        Assert.Contains("Password: 12344321", readme);
        Assert.False(readme.Contains("Username: denis"), "README should document postgres as the default web login.");

        return Task.CompletedTask;
    }

    public static Task DashboardMoistureBarUsesCssSafePercentages()
    {
        var dashboard = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Index.razor"));

        Assert.Contains("GetMoistureWidthStyle(reading.Moisture)", dashboard);
        Assert.Contains("Pressure:", dashboard);
        Assert.Contains("CultureInfo.InvariantCulture", dashboard);
        Assert.Contains("Math.Clamp(moisture ?? 0, 0, 100)", dashboard);

        return Task.CompletedTask;
    }

    public static Task HistoryChartAveragesDenseReadings()
    {
        var historyPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "History.razor"));

        Assert.Contains("Chart Average:", historyPage);
        Assert.Contains("ChartBucketMinutes = 10", historyPage);
        Assert.Contains("<option value=\"10\">10 minutes</option>", historyPage);
        Assert.Contains("BuildAveragedChartData(HistoryData, ChartBucketMinutes)", historyPage);
        Assert.Contains("BuildMetricChartData", historyPage);
        Assert.Contains("AverageNullable", historyPage);
        Assert.Contains("metric-pressure", historyPage);
        Assert.Contains("PressureChartData", historyPage);
        Assert.Contains("ValueProperty=\"Value\"", historyPage);
        Assert.Contains("Pressure = AverageNullable", historyPage);
        Assert.Contains("Pressure (hPa)", historyPage);
        Assert.Contains("No pressure data available.", historyPage);
        Assert.Contains("Chart Points:", historyPage);
        Assert.False(historyPage.Contains("ChartData.Insert(0"), "History chart should not inject artificial extra points.");

        return Task.CompletedTask;
    }

    public static Task ZoneManagementUsesConfiguredZonesAndSupportsCreation()
    {
        var historyPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "History.razor"));
        var settingsPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Settings.razor"));
        var zonesPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Zones.razor"));
        var dataService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorDataService.cs"));

        Assert.Contains("Zones = await DataService.GetZonesAsync()", historyPage);
        Assert.Contains("@foreach (var zone in Zones)", historyPage);
        Assert.Contains("disabled=\"@(Zones.Count == 0)\"", historyPage);
        Assert.Contains("RefreshHistory", historyPage);
        Assert.False(historyPage.Contains("<option value=\"2\">Zone 2</option>"), "History zone filter should come from configured zones.");
        Assert.False(historyPage.Contains("<option value=\"3\">Zone 3</option>"), "History zone filter should come from configured zones.");

        Assert.Contains("Zones = await DataService.GetZonesAsync();", settingsPage);
        Assert.Contains("TotalZones = Zones.Count;", settingsPage);
        Assert.False(settingsPage.Contains("<span class=\"info-value\">3</span>"), "System information should not hardcode three zones.");

        Assert.Contains("Add Zone", zonesPage);
        Assert.Contains("Sensor ESP Name", zonesPage);
        Assert.Contains("@bind=\"zone.SensorDeviceName\"", zonesPage);
        Assert.Contains("@bind=\"NewZone.SensorDeviceName\"", zonesPage);
        Assert.Contains("CreateZoneAsync", zonesPage);
        Assert.Contains("DeleteZone", zonesPage);
        Assert.Contains("Delete this zone and its history?", zonesPage);
        Assert.Contains("Zone name is required.", zonesPage);
        Assert.Contains("ResetNewZone", zonesPage);

        Assert.Contains("CreateZoneAsync", dataService);
        Assert.Contains("DeleteZoneAsync", dataService);
        Assert.Contains("DELETE FROM sensor_readings WHERE zone_id = @id", dataService);
        Assert.Contains("DELETE FROM zones WHERE id = @id", dataService);
        Assert.Contains("sensor_device_name", dataService);
        Assert.Contains("RETURNING id, name, plant_type, moisture_threshold, is_active, sensor_device_name", dataService);
        Assert.Contains("VALUES ('Zone 1', NULL, 30.0, true, 'esp32-zone-1')", dataService);
        Assert.False(dataService.Contains("('Zone 2'"), "Default seeding should create only one starter zone.");
        Assert.False(dataService.Contains("('Zone 3'"), "Default seeding should create only one starter zone.");

        return Task.CompletedTask;
    }

    public static Task DatabaseCleanupSupportsOneSecondRetention()
    {
        var settingsPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Settings.razor"));
        var dataService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorDataService.cs"));

        Assert.Contains("Database Management", settingsPage);
        Assert.Contains("<option value=\"1\">1 second</option>", settingsPage);
        Assert.Contains("CleanupRetentionSeconds", settingsPage);
        Assert.Contains("TimeSpan.FromSeconds(CleanupRetentionSeconds)", settingsPage);
        Assert.False(settingsPage.Contains("DaysToKeep"), "Database cleanup should no longer be limited to whole days.");
        Assert.Contains("CleanOldDataAsync(TimeSpan retention)", dataService);
        Assert.Contains("NpgsqlDbType.Timestamp", dataService);
        Assert.Contains("recorded_at < @cutoff", dataService);
        Assert.False(dataService.Contains("INTERVAL '{daysToKeep} days'"), "Database cleanup should use a parameterized cutoff timestamp.");

        return Task.CompletedTask;
    }

    public static Task HistoryAndLoginCutoffsAreParameterized()
    {
        var dataService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorDataService.cs"));
        var authService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "AuthService.cs"));

        Assert.Contains("Math.Clamp(hours, 1, 168)", dataService);
        Assert.Contains("recorded_at >= @cutoff", dataService);
        Assert.False(dataService.Contains("INTERVAL '{hours} hours'"), "History lookup should use a parameterized cutoff timestamp.");
        Assert.Contains("attempted_at > @cutoff", authService);
        Assert.False(authService.Contains("INTERVAL '@minutes minutes'"), "Login throttling should use a parameterized cutoff timestamp.");
        Assert.False(authService.Contains("Replace(\"@minutes\""), "Login throttling should not build interval SQL by replacement.");

        return Task.CompletedTask;
    }

    public static Task GeneratedBuildOutputsAreIgnored()
    {
        var gitignore = File.ReadAllText(FindRepoFile(".gitignore"));

        Assert.Contains("**/bin/", gitignore);
        Assert.Contains("**/obj/", gitignore);
        Assert.Contains("**/.test-bin/", gitignore);

        return Task.CompletedTask;
    }

    public static Task SettingsPageExposesAiManagement()
    {
        var settingsPage = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Settings.razor"));
        var dataService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorDataService.cs"));
        var decisionService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "IrrigationDecisionService.cs"));

        Assert.Contains("AI Decision Management", settingsPage);
        Assert.Contains("Enable AI Decision Making", settingsPage);
        Assert.Contains("AI Provider", settingsPage);
        Assert.Contains("Ollama Base URL", settingsPage);
        Assert.Contains("API Key Environment Variable Name", settingsPage);
        Assert.Contains("Enable Watering Cooldown", settingsPage);
        Assert.Contains("Test AI Provider", settingsPage);
        Assert.Contains("Reset Cooldowns", settingsPage);
        Assert.Contains("AI Situation Test", settingsPage);
        Assert.Contains("Test AI Situation", settingsPage);
        Assert.Contains("LooksLikeSecretApiKey", settingsPage);
        Assert.Contains("ApiKeyProvider.GetApiKey", settingsPage);
        Assert.Contains("SaveAiSettings", settingsPage);
        Assert.Contains("ResetAiCooldowns", settingsPage);
        Assert.Contains("TestAiSituation", settingsPage);
        Assert.Contains("ai_irrigation_settings", dataService);
        Assert.Contains("UpdateAiIrrigationOptionsAsync", dataService);
        Assert.Contains("ResetAiIrrigationCooldownsAsync", dataService);
        Assert.Contains("EnsureSystemSettingsRowAsync", dataService);
        Assert.Contains("cooldowns_reset_at", dataService);
        Assert.Contains("enable_watering_cooldown", dataService);
        Assert.Contains("NormalizeAiProvider", dataService);
        Assert.Contains("NormalizeOllamaBaseUrl", dataService);
        Assert.Contains("NormalizeApiKeyEnvironmentVariableName", dataService);
        Assert.Contains("GetAiIrrigationOptionsAsync(Options.Value)", decisionService);
        Assert.Contains("PreviewAsync", decisionService);
        Assert.Contains("CooldownsResetAt", decisionService);
        Assert.Contains("settings.RequiresApiKey", decisionService);
        Assert.Contains("AiApiHealth.GetActivePause", decisionService);
        Assert.Contains("RecordOpenAiFailureAsync", decisionService);
        Assert.Contains("RecordOllamaFailureAsync", decisionService);
        Assert.Contains("AiAnalysisLock", decisionService);
        Assert.Contains("ResetPause", settingsPage);

        var program = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Program.cs"));
        var project = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "IrrigationSystem.Web.csproj"));

        Assert.Contains("OpenAiApiKeyProvider", program);
        Assert.Contains("AiApiHealthService", program);
        Assert.Contains("AiApiStartupCheckService", program);
        Assert.Contains("UserSecretsId", project);

        var healthService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "AiApiHealthService.cs"));
        var aiService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "OpenAiIrrigationService.cs"));

        Assert.Contains("HttpMethod.Post, \"responses\"", healthService);
        Assert.Contains("api/tags", healthService);
        Assert.Contains("QuotaOrRateLimitPause", healthService);
        Assert.Contains("OllamaFailurePause", healthService);
        Assert.Contains("OllamaAnalysisTimeout", aiService);
        Assert.Contains("api/chat", aiService);

        return Task.CompletedTask;
    }

    public static Task FallbackOnlyAiDecisionsStayOutOfAiLogs()
    {
        var decisionService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "IrrigationDecisionService.cs"));
        var queueService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorReadingDecisionQueue.cs"));
        var autoWateringService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "AutoWateringService.cs"));
        var healthService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "AiApiHealthService.cs"));

        Assert.Contains("persistResult && ShouldPersistAiDecision(finalDecision)", decisionService);
        Assert.Contains("finalDecision.AiDecision != null && !finalDecision.WasFallbackUsed", decisionService);
        Assert.Contains("SuppressFallbackDecision(finalDecision, aiErrorMessage)", decisionService);
        Assert.Contains("FinalShouldWaterAfterSafety = false", decisionService);
        Assert.Contains("AI did not produce an accepted decision, so automatic watering stayed off.", decisionService);
        Assert.Contains("if (!finalDecision.WasFallbackUsed)", decisionService);
        Assert.Contains("Final AI irrigation decision", decisionService);
        Assert.False(decisionService.Contains("fallback={Fallback}"), "Fallback-only decisions should not be written to the final decision console log.");

        Assert.Contains("if (!finalDecision.WasFallbackUsed)", queueService);
        Assert.Contains("Processed queued AI sensor decision", queueService);
        Assert.Contains("Final AI decision opened valve", queueService);
        Assert.Contains("Final AI decision", autoWateringService);

        Assert.False(healthService.Contains("rule fallback continues"), "Provider failure logs should not advertise rule fallback.");

        return Task.CompletedTask;
    }

    public static Task Esp32SketchPostsToWebSensorApi()
    {
        var sketch = File.ReadAllText(FindRepoFile("esp32", "esp32.ino"));

        Assert.Contains("const char* ssid = \"GardenBrain\";", sketch);
        Assert.Contains("const char* password = \"GardenBrain123\";", sketch);
        Assert.Contains("const char* espDeviceName = \"esp32-zone-1\";", sketch);
        Assert.Contains("const int sensorZoneId = 1;", sketch);
        Assert.Contains("const char* laptopServerIp = \"10.163.174.210\";", sketch);
        Assert.Contains("Using DHCP from the phone hotspot.", sketch);
        Assert.Contains("sensorApiUrl()", sketch);
        Assert.Contains("\\\"device\\\":\\\"\" + String(espDeviceName)", sketch);
        Assert.Contains("\\\"zoneId\\\"", sketch);
        Assert.Contains("\\\"temperature\\\"", sketch);
        Assert.Contains("\\\"humidity\\\"", sketch);
        Assert.Contains("\\\"pressure\\\"", sketch);
        Assert.Contains("\\\"soilMoisture\\\"", sketch);
        Assert.Contains("http.errorToString", sketch);
        Assert.Contains("http.setReuse(false)", sketch);

        return Task.CompletedTask;
    }

    public static Task Esp32SketchSubscribesToWebValveCommands()
    {
        var sketch = File.ReadAllText(FindRepoFile("esp32", "esp32.ino"));
        var libraries = File.ReadAllText(FindRepoFile("esp32", "libraries.txt"));

        Assert.Contains("#include <PubSubClient.h>", sketch);
        Assert.Contains("irrigation/zone/+/valve", sketch);
        Assert.Contains("ZoneValveMapping", sketch);
        Assert.Contains("zoneValveMap", sketch);
        Assert.Contains("localValveForZone(commandZoneId)", sketch);
        Assert.Contains("\\\"action\\\":\\\"open\\\"", sketch);
        Assert.Contains("openValveForDuration", sketch);
        Assert.Contains("setValve(1, false)", sketch);
        Assert.Contains("setValve(2, false)", sketch);
        Assert.Contains("PubSubClient", libraries);

        return Task.CompletedTask;
    }

    public static Task SensorApiAppliesSafeWateringDecisionsThroughMqtt()
    {
        var apiController = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Controllers",
            "ApiController.cs"));
        var decisionQueue = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "SensorReadingDecisionQueue.cs"));
        var mqttService = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "MqttService.cs"));
        var program = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Program.cs"));

        Assert.Contains("IValveCommandService", mqttService);
        Assert.Contains("SensorReadingDecisionQueue", apiController);
        Assert.Contains("SensorDeviceMatchesZone", apiController);
        Assert.Contains("device must be 80 characters or fewer", apiController);
        Assert.Contains("expects sensor device", apiController);
        Assert.Contains("device = deviceName", apiController);
        Assert.Contains("DecisionQueue.TryQueue", apiController);
        Assert.Contains("IValveCommandService", decisionQueue);
        Assert.Contains("TryApplyWateringDecisionAsync", decisionQueue);
        Assert.Contains("ValveCommands.OpenValveAsync(finalDecision.ZoneId, durationSeconds)", decisionQueue);
        Assert.Contains("CompleteIrrigationEventLaterAsync", decisionQueue);
        Assert.Contains("AddSingleton<IValveCommandService>", program);
        Assert.Contains("AddSingleton<SensorReadingDecisionQueue>", program);
        Assert.Contains("AddHostedService(sp => sp.GetRequiredService<SensorReadingDecisionQueue>())", program);

        return Task.CompletedTask;
    }

    public static Task WateringEventsStartAfterMqttOpenSucceeds()
    {
        var dashboard = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Pages",
            "Index.razor"));
        var autoWatering = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Services",
            "AutoWateringService.cs"));

        Assert.True(
            dashboard.IndexOf("ValveCommands.OpenValveAsync", StringComparison.Ordinal) <
            dashboard.IndexOf("StartIrrigationEventAsync", StringComparison.Ordinal),
            "Manual watering should not create an irrigation event until the MQTT open command succeeds.");
        Assert.True(
            autoWatering.IndexOf("ValveCommands.OpenValveAsync", StringComparison.Ordinal) <
            autoWatering.IndexOf("StartIrrigationEventAsync", StringComparison.Ordinal),
            "Auto-watering should not create an irrigation event until the MQTT open command succeeds.");

        return Task.CompletedTask;
    }

    public static Task WebHostUsesPort5000()
    {
        var program = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Program.cs"));

        Assert.Contains("UseUrls(\"http://0.0.0.0:5000\")", program);

        return Task.CompletedTask;
    }

    public static Task WebHostAllowsSlowEmbeddedRequestBodies()
    {
        var program = File.ReadAllText(FindRepoFile(
            "raspberry-pi",
            "irrigation_project",
            "IrrigationSystem.Web",
            "Program.cs"));

        Assert.Contains("ConfigureKestrel", program);
        Assert.Contains("MinRequestBodyDataRate = null", program);

        return Task.CompletedTask;
    }

    private static ApiController CreateApiController()
    {
        const string unusedConnectionString = "Host=unused;Database=unused;Username=unused;Password=unused";

        var dataService = new SensorDataService(
            unusedConnectionString,
            new TestLogger<SensorDataService>());
        var adaptiveService = new AdaptiveWateringService(
            unusedConnectionString,
            new TestLogger<AdaptiveWateringService>(),
            dataService);
        var decisionService = new IrrigationDecisionService(
            dataService,
            new ThrowingAiService(),
            Options.Create(new AiIrrigationOptions()),
            new OpenAiApiKeyProvider(new ConfigurationBuilder().Build()),
            new AiApiHealthService(
                new TestHttpClientFactory(),
                dataService,
                Options.Create(new AiIrrigationOptions()),
                new OpenAiApiKeyProvider(new ConfigurationBuilder().Build()),
                new TestLogger<AiApiHealthService>()),
            new TestLogger<IrrigationDecisionService>(),
            new WeatherForecastService(new TestHttpClientFactory(), dataService, new TestLogger<WeatherForecastService>()));
        var decisionQueue = new SensorReadingDecisionQueue(
            dataService,
            decisionService,
            new FakeValveCommandService(),
            new TestLogger<SensorReadingDecisionQueue>());

        return new ApiController(
            dataService,
            adaptiveService,
            decisionQueue,
            new TestLogger<ApiController>());
    }

    private static IrrigationDecisionResult ApplySafety(
        AiIrrigationDecision? aiDecision,
        float moisture = 20,
        bool aiWasAttempted = false,
        string? aiErrorMessage = null,
        WeatherWateringAdvice? weatherAdvice = null,
        IReadOnlyList<IrrigationEvent>? recentEvents = null,
        bool enableWateringCooldown = true,
        DateTime? cooldownsResetAt = null)
    {
        recentEvents ??= Array.Empty<IrrigationEvent>();
        var reading = new SensorReading
        {
            Id = 10,
            ZoneId = 1,
            Moisture = moisture,
            Temperature = 24,
            Humidity = 60,
            RecordedAt = DateTime.UtcNow
        };
        var zone = new Zone
        {
            Id = 1,
            Name = "Zone 1",
            PlantType = "Tomatoes",
            MoistureThreshold = 30,
            IsActive = true
        };
        var settings = new SystemSettings
        {
            AutoWateringEnabled = true,
            DefaultWateringDuration = 10
        };
        var options = new AiIrrigationOptions
        {
            EnableAiDecisionMaking = true,
            LowConfidenceThreshold = 0.65,
            HighMoistureBlockPercent = 60,
            MaxDurationSeconds = 120,
            EnableWateringCooldown = enableWateringCooldown,
            MinimumMinutesBetweenWaterings = 120,
            CooldownsResetAt = cooldownsResetAt
        };
        var fallback = IrrigationSafetyPolicy.BuildRuleBasedDecision(
            reading,
            zone,
            settings,
            recentEvents,
            options);

        return IrrigationSafetyPolicy.Apply(
            reading,
            zone,
            settings,
            recentEvents,
            fallback,
            aiDecision,
            options,
            aiWasAttempted,
            aiErrorMessage,
            weatherAdvice);
    }

    private static AiIrrigationDecision CreateAiDecision(bool shouldWater, int durationSeconds, double confidence)
    {
        return new AiIrrigationDecision
        {
            ShouldWater = shouldWater,
            RecommendedValveState = shouldWater ? "ON" : "OFF",
            RecommendedDurationSeconds = durationSeconds,
            Confidence = confidence,
            Reason = "AI test recommendation.",
            LearnedObservation = "Test pattern memory.",
            SuggestedMoistureThreshold = 30,
            RiskLevel = "LOW"
        };
    }

    private static string Serialize(object? value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (current != null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(pathParts)}");
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }

    public static void Contains(string expectedText, string actualText)
    {
        if (!actualText.Contains(expectedText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text not found: {expectedText}");
        }
    }

    public static T IsType<T>(object? value)
    {
        if (value is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Expected type {typeof(T).Name}, got {value?.GetType().Name ?? "null"}.");
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return false;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(new TestHttpMessageHandler())
        {
            BaseAddress = new Uri("https://api.openai.com/v1/")
        };
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}

internal sealed class FakeValveCommandService : IValveCommandService
{
    public Task<bool> OpenValveAsync(int zoneId, int durationSeconds)
    {
        return Task.FromResult(true);
    }

    public Task<bool> CloseValveAsync(int zoneId)
    {
        return Task.FromResult(true);
    }
}

internal sealed class ThrowingAiService : IAiIrrigationService
{
    public Task<AiIrrigationDecision> AnalyzeAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("AI should not be called by controller validation tests.");
    }
}
