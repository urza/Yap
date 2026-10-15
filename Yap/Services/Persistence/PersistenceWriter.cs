using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;

namespace Yap.Services.Persistence;

/// <summary>One write to the database. The writer runs it inside an open transaction.</summary>
public delegate Task Write(ChatDbContext db, CancellationToken ct);

/// <summary>
/// The one path from memory to the database. A service changes its memory, enqueues a <see cref="Write"/>
/// and returns. This background loop drains the queue in order, one transaction per pass, so no request
/// ever waits for the disk and every database concern lives here. Without persistence the queue is a no-op.
/// </summary>
public sealed class PersistenceWriter : BackgroundService
{
    private readonly IDbContextFactory<ChatDbContext>? _db;
    private readonly ILogger<PersistenceWriter> _log;

    // One reader, so the runtime uses its cheapest implementation. Unbounded: a chat this size never
    // outruns SQLite, and the depth is shown on the diagnostics page instead of enforced here.
    private readonly Channel<Write> _queue = Channel.CreateUnbounded<Write>(new UnboundedChannelOptions { SingleReader = true });
    private int _depth;   // counted here because the single-reader channel does not support Reader.Count

    public PersistenceWriter(IServiceProvider services, ILogger<PersistenceWriter> log)
    {
        _db = services.GetService<IDbContextFactory<ChatDbContext>>();   // not registered when persistence is off
        _log = log;
    }

    public bool Enabled => _db is not null;
    public int Depth => Volatile.Read(ref _depth);
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }

    /// <summary>Called by a service after it changed memory. Never awaited by the caller.</summary>
    public void Enqueue(Write write)
    {
        if (Enabled && _queue.Writer.TryWrite(write)) Interlocked.Increment(ref _depth);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        // Not bound to stoppingToken on purpose. StopAsync completes the queue instead, so this loop ends
        // after the last queued write and a normal shutdown loses nothing.
        while (await _queue.Reader.WaitToReadAsync())
        {
            var batch = new List<Write>();
            while (_queue.Reader.TryRead(out var write)) batch.Add(write);
            await RunAsync(batch);
            Interlocked.Add(ref _depth, -batch.Count);   // after the pass: the depth counts writes not yet on disk
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();   // the host can stop twice (WebApplicationFactory does); Complete would throw the second time
        await base.StopAsync(cancellationToken);   // waits for ExecuteAsync up to the host's shutdown timeout
    }

    // Everything that was queued at the moment of the pass goes into one transaction. Under load this turns
    // the many read-state updates of one message into one commit, which is the expensive part on SQLite.
    private async Task RunAsync(List<Write> batch)
    {
        try
        {
            await using var db = await _db!.CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            foreach (var write in batch)
            {
                await write(db, CancellationToken.None);
                // Add and SetValues stay tracked until SaveChanges, while ExecuteUpdate and ExecuteDelete run at
                // once. Saving after every write keeps an edit after its insert and a delete after both.
                await db.SaveChangesAsync();
            }
            await tx.CommitAsync();
        }
        catch (Exception ex) when (batch.Count > 1)
        {
            // One bad write must not take the rest of the batch with it: redo them one at a time.
            _log.LogWarning(ex, "A batch of {Count} database writes failed; retrying them one by one", batch.Count);
            foreach (var write in batch) await RunAsync([write]);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastErrorAt = DateTime.UtcNow;
            _log.LogError(ex, "A database write failed and was dropped. Memory is still correct.");
        }
    }
}
