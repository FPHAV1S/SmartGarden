using Microsoft.AspNetCore.Mvc;
using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly SensorDataService DataService;
    private readonly AdaptiveWateringService AdaptiveService;
    private readonly IrrigationDecisionService DecisionService;
    private readonly ILogger<ApiController> Logger;

    public ApiController(
        SensorDataService dataService,
        AdaptiveWateringService adaptiveService,
        IrrigationDecisionService decisionService,
        ILogger<ApiController> logger)
    {
        DataService = dataService;
        AdaptiveService = adaptiveService;
        DecisionService = decisionService;
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
        if (!zones.Any(zone => zone.Id == request.ZoneId))
        {
            return BadRequest(new { message = $"Zone {request.ZoneId} does not exist" });
        }

        var reading = await DataService.InsertSensorReadingAsync(
            request.ZoneId,
            moisture.Value,
            request.Temperature.Value,
            request.Humidity.Value);

        var finalDecision = await DecisionService.EvaluateAsync(
            reading,
            $"sensor-api:{(string.IsNullOrWhiteSpace(request.Device) ? "unknown" : request.Device)}",
            HttpContext.RequestAborted);

        Logger.LogInformation(
            "Received sensor reading from {Device} for zone {ZoneId}: moisture={Moisture}, temperature={Temperature}, humidity={Humidity}",
            string.IsNullOrWhiteSpace(request.Device) ? "unknown device" : request.Device,
            request.ZoneId,
            moisture.Value,
            request.Temperature.Value,
            request.Humidity.Value);

        return Ok(new
        {
            success = true,
            zoneId = request.ZoneId,
            moisture = moisture.Value,
            temperature = request.Temperature.Value,
            humidity = request.Humidity.Value,
            finalDecision
        });
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
    public int? Rssi { get; set; }
    public string? Ip { get; set; }
}
