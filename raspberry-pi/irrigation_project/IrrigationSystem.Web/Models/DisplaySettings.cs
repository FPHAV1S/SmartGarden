namespace IrrigationSystem.Web.Models;

public class DisplaySettings
{
    public string Mode { get; set; } = "zone_detail";
    public int? SelectedZoneId { get; set; }
    public int RefreshInterval { get; set; } = 60;
}