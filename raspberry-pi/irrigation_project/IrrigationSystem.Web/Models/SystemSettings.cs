namespace IrrigationSystem.Web.Models;

public class SystemSettings
{
    public int Id { get; set; }
    public bool AutoWateringEnabled { get; set; } = true;
    public string SystemMode { get; set; } = "auto";
    public int DefaultWateringDuration { get; set; } = 10;
    public bool NightModeEnabled { get; set; }
    public int NightModeStartHour { get; set; } = 18;
    public int NightModeEndHour { get; set; } = 8;
    public bool EcoModeEnabled { get; set; }
}