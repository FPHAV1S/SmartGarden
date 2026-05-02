using System.Text.Json;
using IrrigationSystem.Web.Controllers;
using IrrigationSystem.Web.Models;
using IrrigationSystem.Web.Services;
using Microsoft.AspNetCore.Mvc;
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
    new("Low AI confidence falls back to rules", Tests.LowAiConfidenceFallsBackToRules),
    new("API failure falls back to rules", Tests.ApiFailureFallsBackToRules),
    new("Database schema contains required tables", Tests.DatabaseSchemaContainsRequiredTables),
    new("Web appsettings uses the expected local database", Tests.WebAppSettingsUseExpectedDatabase),
    new("ESP32 sketch posts to the web sensor API", Tests.Esp32SketchPostsToWebSensorApi),
    new("ESP32 sketch subscribes to web valve commands", Tests.Esp32SketchSubscribesToWebValveCommands),
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
        Assert.Contains("moisture 65.0%", result.SafetyNotes);

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

    public static Task LowAiConfidenceFallsBackToRules()
    {
        var aiDecision = CreateAiDecision(shouldWater: false, durationSeconds: 0, confidence: 0.2);
        var result = ApplySafety(aiDecision, moisture: 20);

        Assert.True(result.WasFallbackUsed, "Low confidence should use rule fallback.");
        Assert.True(result.FinalShouldWaterAfterSafety, "Low-confidence AI OFF should not override a low-moisture rule fallback.");
        Assert.Contains("below 0.65", result.SafetyNotes);

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

    public static Task DatabaseSchemaContainsRequiredTables()
    {
        var schema = File.ReadAllText(FindRepoFile("raspberry-pi", "irrigation_db.sql"));
        var requiredTables = new[]
        {
            "zones",
            "sensor_readings",
            "irrigation_events",
            "ai_irrigation_decision_logs",
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

        Assert.Equal(
            "Host=localhost;Database=irrigation_db;Username=postgres;Password=1203",
            connectionString,
            "Web app should point at the local irrigation database used by setup.sh.");

        return Task.CompletedTask;
    }

    public static Task Esp32SketchPostsToWebSensorApi()
    {
        var sketch = File.ReadAllText(FindRepoFile("esp32", "esp32.ino"));

        Assert.Contains("const char* ssid = \"GardenBrain\";", sketch);
        Assert.Contains("http://192.168.4.1:5000/api/sensor-readings", sketch);
        Assert.Contains("\\\"zoneId\\\"", sketch);
        Assert.Contains("\\\"temperature\\\"", sketch);
        Assert.Contains("\\\"humidity\\\"", sketch);
        Assert.Contains("\\\"soilMoisture\\\"", sketch);

        return Task.CompletedTask;
    }

    public static Task Esp32SketchSubscribesToWebValveCommands()
    {
        var sketch = File.ReadAllText(FindRepoFile("esp32", "esp32.ino"));
        var libraries = File.ReadAllText(FindRepoFile("esp32", "libraries.txt"));

        Assert.Contains("#include <PubSubClient.h>", sketch);
        Assert.Contains("irrigation/zone/+/valve", sketch);
        Assert.Contains("\\\"action\\\":\\\"open\\\"", sketch);
        Assert.Contains("openValveForDuration", sketch);
        Assert.Contains("setValve(1, false)", sketch);
        Assert.Contains("setValve(2, false)", sketch);
        Assert.Contains("PubSubClient", libraries);

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
            new TestLogger<IrrigationDecisionService>());

        return new ApiController(dataService, adaptiveService, decisionService, new TestLogger<ApiController>());
    }

    private static IrrigationDecisionResult ApplySafety(
        AiIrrigationDecision? aiDecision,
        float moisture = 20,
        bool aiWasAttempted = false,
        string? aiErrorMessage = null)
    {
        var reading = new SensorReading
        {
            Id = 10,
            ZoneId = 1,
            Moisture = moisture,
            Temperature = 24,
            Humidity = 60,
            RecordedAt = DateTime.Now
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
            MinimumMinutesBetweenWaterings = 120
        };
        var fallback = IrrigationSafetyPolicy.BuildRuleBasedDecision(
            reading,
            zone,
            settings,
            Array.Empty<IrrigationEvent>(),
            options);

        return IrrigationSafetyPolicy.Apply(
            reading,
            zone,
            settings,
            Array.Empty<IrrigationEvent>(),
            fallback,
            aiDecision,
            options,
            aiWasAttempted,
            aiErrorMessage);
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
