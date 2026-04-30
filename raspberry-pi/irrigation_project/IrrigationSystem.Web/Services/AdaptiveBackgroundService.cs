namespace IrrigationSystem.Web.Services;

public class AdaptiveBackgroundService : BackgroundService
{
    private readonly ILogger<AdaptiveBackgroundService> Logger;
    private readonly AdaptiveWateringService AdaptiveService;

    public AdaptiveBackgroundService(ILogger<AdaptiveBackgroundService> logger, AdaptiveWateringService adaptiveService)
    {
        Logger = logger;
        AdaptiveService = adaptiveService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Adaptive background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                await AdaptiveService.RunAdaptiveAnalysisAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in adaptive background service");
            }
        }
    }
}