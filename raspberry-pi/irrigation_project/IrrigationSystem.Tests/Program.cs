using System.Text.Json;
using IrrigationSystem.Web.Controllers;
using IrrigationSystem.Web.Models;
using IrrigationSystem.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

var tests = new TestCase[]
{
    new("SystemSettings defaults match seeded settings", Tests.SystemSettingsDefaultsMatchSeedData),
    new("AuthService hashes passwords with BCrypt", Tests.AuthServiceHashesPasswords),
    new("Sensor API rejects missing request bodies", Tests.SensorApiRejectsMissingRequestBody),
    new("Sensor API rejects invalid zone IDs before database access", Tests.SensorApiRejectsInvalidZoneId),
    new("Sensor API rejects incomplete readings before database access", Tests.SensorApiRejectsIncompleteReadings),
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

    public static Task DatabaseSchemaContainsRequiredTables()
    {
        var schema = File.ReadAllText(FindRepoFile("raspberry-pi", "irrigation_db.sql"));
        var requiredTables = new[]
        {
            "zones",
            "sensor_readings",
            "irrigation_events",
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
            "Host=localhost;Database=irrigation_db;Username=denis;Password=1203",
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

        return new ApiController(dataService, adaptiveService, new TestLogger<ApiController>());
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
