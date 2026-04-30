namespace IrrigationSystem.Web.Models;

public class SensorReading
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public float? Moisture { get; set; }
    public float? Temperature { get; set; }
    public float? Humidity { get; set; }
    public DateTime RecordedAt { get; set; }
}