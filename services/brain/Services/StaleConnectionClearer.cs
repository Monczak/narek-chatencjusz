namespace BrainService.Services;

public class StaleConnectionClearer(IServiceProvider services, ILogger<StaleConnectionClearer> logger) : BackgroundService
{
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = services.CreateScope())
                {
                    var registry = scope.ServiceProvider.GetRequiredService<NodeRegistryService>();
                    await registry.CleanupStaleConnectionsAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during stale connection cleanup loop");
            }
            
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}