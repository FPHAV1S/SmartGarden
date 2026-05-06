using Microsoft.AspNetCore.Mvc;
using IrrigationSystem.Web.Models;
using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly SensorDataService DataService;
    private readonly AdaptiveWateringService AdaptiveService;
    private readonly SensorReadingDecisionQueue DecisionQueue;
    private readonly ILogger<ApiController> Logger;

    public ApiController(
        SensorDataService dataService,
        AdaptiveWateringService adaptiveService,
        SensorReadingDecisionQueue decisionQueue,
        ILogger<ApiController> logger)
    {
        DataService = dataService;
        AdaptiveService = adaptiveService;
        DecisionQueue = decisionQueue;
        Logger = logger;
    }

    [HttpGet("zones")]
    public async Task<IActionResult> GetZones()
    {
        var zones = await DataService.GetZonesAsync();
        return Ok(zones);
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatestReadings()
    {
        var readings = await DataService.GetLatestReadingsAsync();
        return Ok(readings);
    }

    [HttpPost("sensor-readings")]
    public async Task<IActionResult> CreateSensorReading([FromBody] SensorReadingRequest? request)
    {
        if (request == null)
        {
            return BadRequest(new { message = "Request body is required" });
        }

        var moisture = request.Moisture ?? request.SoilMoisture;

        if (request.ZoneId <= 0)
        {
            return BadRequest(new { message = "zoneId must be greater than 0" });
        }

        if (!moisture.HasValue || !request.Temperature.HasValue || !request.Humidity.HasValue)
        {
            return BadRequest(new { message = "temperature, humidity, and soilMoisture are required" });
        }

        var zones = await DataService.EnsureDefaultZonesAsync();
        if (zones.Count == 0)
        {
            return Problem(
                title: "Sensor storage is not ready",
                detail: "No zones are available. Check the database connection and schema setup.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var zone = zones.FirstOrDefault(zone => zone.Id == request.ZoneId);
        if (zone == null)
        {
            return BadRequest(new { message = $"Zone {request.ZoneId} does not exist" });
        }

        var deviceName = NormalizeDeviceName(request.Device);
        if (deviceName?.Length > 80)
        {
            return BadRequest(new { message = "device must be 80 characters or fewer" });
        }

        if (!SensorDeviceMatchesZone(zone, deviceName))
        {
            return BadRequest(new
            {
                message = $"Zone {request.ZoneId} expects sensor device '{zone.SensorDeviceName}', but received '{(string.IsNullOrWhiteSpace(deviceName) ? "unknown device" : deviceName)}'"
            });
        }

        SensorReading reading;
        try
        {
            reading = await DataService.InsertSensorReadingAsync(
                request.ZoneId,
                moisture.Value,
                request.Temperature.Value,
                request.Humidity.Value,
                request.Pressure,
                deviceName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to store sensor reading from {Device} for zone {ZoneId}",
                string.IsNullOrWhiteSpace(deviceName) ? "unknown device" : deviceName,
                request.ZoneId);

            return Problem(
                title: "Failed to store sensor reading",
                detail: "The reading payload was valid, but the server could not save it. Check PostgreSQL and the web app logs.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var decisionQueued = DecisionQueue.TryQueue(
            reading,
            $"sensor-api:{(string.IsNullOrWhiteSpace(deviceName) ? "unknown" : deviceName)}");

        Logger.LogInformation(
            "Received sensor reading from {Device} for zone {ZoneId}: moisture={Moisture}, temperature={Temperature}, humidity={Humidity}, pressure={Pressure}, decisionQueued={DecisionQueued}",
            string.IsNullOrWhiteSpace(deviceName) ? "unknown device" : deviceName,
            request.ZoneId,
            moisture.Value,
            request.Temperature.Value,
            request.Humidity.Value,
            request.Pressure,
            decisionQueued);

        return Ok(new
        {
            success = true,
            readingId = reading.Id,
            zoneId = request.ZoneId,
            device = deviceName,
            moisture = moisture.Value,
            temperature = request.Temperature.Value,
            humidity = request.Humidity.Value,
            pressure = request.Pressure,
            decisionQueued
        });
    }

    private static string? NormalizeDeviceName(string? device)
    {
        return string.IsNullOrWhiteSpace(device)
            ? null
            : device.Trim();
    }

    private static bool SensorDeviceMatchesZone(Zone zone, string? deviceName)
    {
        return string.IsNullOrWhiteSpace(zone.SensorDeviceName) ||
            string.Equals(zone.SensorDeviceName.Trim(), deviceName, StringComparison.OrdinalIgnoreCase);
    }

    [HttpGet("ai/latest-decision")]
    public async Task<IActionResult> GetLatestAiDecision()
    {
        var decision = await DataService.GetLatestAiDecisionAsync();
        if (decision == null)
        {
            return NotFound(new { message = "No AI irrigation decisions have been logged yet" });
        }

        return Ok(decision);
    }

    [HttpGet("ai/history")]
    public async Task<IActionResult> GetAiDecisionHistory([FromQuery] int count = 20)
    {
        var decisions = await DataService.GetAiDecisionHistoryAsync(count);
        return Ok(decisions);
    }

    [HttpGet("zone/{zoneId}/history")]
    public async Task<IActionResult> GetZoneHistory(int zoneId, [FromQuery] int hours = 24)
    {
        var readings = await DataService.GetZoneHistoryAsync(zoneId, hours);
        return Ok(readings);
    }

    [HttpPost("adaptive/run")]
    public async Task<IActionResult> RunAdaptiveAnalysis()
    {
        await AdaptiveService.RunAdaptiveAnalysisAsync();
        return Ok(new { message = "Adaptive analysis completed" });
    }
}

public class SensorReadingRequest
{
    public int ZoneId { get; set; } = 1;
    public float? Moisture { get; set; }
    public float? SoilMoisture { get; set; }
    public float? Temperature { get; set; }
    public float? Humidity { get; set; }
    public string? Device { get; set; }
    public int? ReadingCount { get; set; }
    public float? Pressure { get; set; }
    public bool? Bme280Ready { get; set; }
    public string? Bme280Status { get; set; }
    public int? SoilRaw { get; set; }
    public bool? Valve1 { get; set; }
    public bool? Valve2 { get; set; }
    public int? Rssi { get; set; }
    public string? Ip { get; set; }
}
