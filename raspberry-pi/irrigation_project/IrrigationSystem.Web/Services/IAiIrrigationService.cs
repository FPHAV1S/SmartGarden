using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public interface IAiIrrigationService
{
    Task<AiIrrigationDecision> AnalyzeAsync(
        SensorReading latestReading,
        List<SensorReading> recentReadings,
        CancellationToken cancellationToken);
}
