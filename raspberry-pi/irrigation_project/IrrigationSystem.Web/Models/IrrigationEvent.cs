namespace IrrigationSystem.Web.Models;

public class IrrigationEvent
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSec { get; set; }
    public string? TriggerReason { get; set; }
    public float? MoistureBefore { get; set; }
    public float? MoistureAfter { get; set; }
}