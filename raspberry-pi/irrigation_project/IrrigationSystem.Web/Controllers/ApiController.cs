using Microsoft.AspNetCore.Mvc;
using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Controllers;

[ApiController]
[Route("api")]
public class ApiController : ControllerBase
{
    private readonly SensorDataService DataService;
    private readonly AdaptiveWateringService AdaptiveService;
    private readonly ILogger<ApiController> Logger;

    public ApiController(SensorDataService dataService, AdaptiveWateringService adaptiveService, ILogger<ApiController> logger)
    {
        DataService = dataService;
        AdaptiveService = adaptiveService;
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