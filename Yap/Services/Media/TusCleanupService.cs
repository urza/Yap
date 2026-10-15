using tusdotnet.Stores;

namespace Yap.Services.Media;

/// <summary>
/// Removes the unfinished tus uploads that passed their expiry (a day after the last chunk, TusEndpoints). The
/// store only marks them; without a sweep an abandoned upload keeps its disk for ever, and a person can fill the
/// volume by starting uploads and never finishing them.
/// </summary>
public sealed class TusCleanupService(UploadPaths paths, ILogger<TusCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                Directory.CreateDirectory(paths.TusTemp);
                var removed = await new TusDiskStore(paths.TusTemp).RemoveExpiredFilesAsync(stoppingToken);
                if (removed > 0) log.LogInformation("Removed {Count} expired upload(s)", removed);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogWarning(e, "The sweep of expired uploads failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
