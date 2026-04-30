namespace IrrigationSystem.Web.Models;

public class Zone
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PlantType { get; set; }
    public float MoistureThreshold { get; set; }
    public bool IsActive { get; set; }
}