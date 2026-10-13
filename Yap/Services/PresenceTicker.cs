namespace Yap.Services;

/// <summary>
/// Publishes changed presence/typing views every 100 ms. Disconnect timing belongs to ChatService.
/// </summary>
public sealed class PresenceTicker(PresenceService presence, ILogger<PresenceTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                try
                {
                    presence.Tick();
                }
                catch (Exception error) { logger.LogError(error, "Presence publication failed"); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
