namespace IrrigationSystem.Web.Models;

public class PatternMemory
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public float? SuggestedThreshold { get; set; }
    public float? AverageMoisture { get; set; }
    public string? Notes { get; set; }
}
