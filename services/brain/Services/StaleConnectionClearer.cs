namespace BrainService.Services;

using BrainService.Services.Audio;

public class StaleConnectionClearer(
    IServiceProvider services,
    AudioGraphFactory audioGraphFactory,
    ILogger<StaleConnectionClearer> logger) : BackgroundService
{
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var registry = scope.ServiceProvider.GetRequiredService<NodeRegistryService>();
                var staleSessionIds = await registry.CleanupStaleConnectionsAsync();

                foreach (var sessionId in staleSessionIds)
                {
                    logger.LogInformation(
                        "Stopping audio graph for stale session {SessionId}", sessionId);
                    await audioGraphFactory.StopSessionAsync(sessionId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during stale connection cleanup");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}
