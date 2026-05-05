namespace IrrigationSystem.Web.Services;

public class AiApiStartupCheckService : BackgroundService
{
    private readonly AiApiHealthService HealthService;
    private readonly ILogger<AiApiStartupCheckService> Logger;

    public AiApiStartupCheckService(
        AiApiHealthService healthService,
        ILogger<AiApiStartupCheckService> logger)
    {
        HealthService = healthService;
        Logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            var result = await HealthService.CheckAsync(cancellationToken: stoppingToken);

            if (result.IsSuccess)
            {
                Logger.LogInformation("AI provider startup check passed: {Message}", result.Message);
            }
            else
            {
                Logger.LogWarning("AI provider startup check failed: {Message}", result.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "AI provider startup check failed unexpectedly");
        }
    }
}
