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

    // One reader, so the runtime uses its cheapest implementation. Unbounded: the depth is shown on the
    // diagnostics page instead of enforced here, and a backlog drains at a steady rate (see BatchSize).
    private readonly Channel<Write> _queue = Channel.CreateUnbounded<Write>(new UnboundedChannelOptions { SingleReader = true });
    private int _depth;   // counted here because the single-reader channel does not support Reader.Count

    // A commit every 500 writes, not one per pass. A pass that took the whole backlog grew with the
    // backlog, so a writer that fell behind never caught up: on the 2026-10-16 load test the queue stalled
    // for good past about 1,000 writes a second. A bounded batch keeps the disk seconds behind memory,
    // and a restart loses at most one batch.
    private const int BatchSize = 500;

    // Writes that are the same row again and again keep only the latest one per key, flushed at most once per
    // interval. Read states are the case: a message in a room of a thousand queued one write per recipient, 14,000
    // a second on the 2026-10-18 load test for about 1,000 distinct rows; the writer drained 3,000 to 5,000 a second
    // and the backlog reached 6.3 million writes and 1 GB. After a crash up to one interval of these rows is behind;
    // memory is the truth and the database the copy, so that is in line with the design (memory-first-data-layer).
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromSeconds(2);
    private readonly Lock _coalesceGate = new();
    private Dictionary<object, Write> _coalesced = new();
    private readonly Dictionary<object, int> _coalesceFailures = new();   // keys whose flush failed, with the count
    private int _coalescedCount;
    private DateTime _lastFlush = DateTime.UtcNow;
    private bool _closed;   // after StopAsync: a write that arrives now would miss the final flush

    public PersistenceWriter(IServiceProvider services, ILogger<PersistenceWriter> log)
    {
        _db = services.GetService<IDbContextFactory<ChatDbContext>>();   // not registered when persistence is off
        _log = log;
    }

    public bool Enabled => _db is not null;
    public int Depth => Volatile.Read(ref _depth) + Volatile.Read(ref _coalescedCount);   // queued writes plus the rows waiting for a flush
    public string? LastError { get; private set; }
    public DateTime? LastErrorAt { get; private set; }

    /// <summary>Called by a service after it changed memory. Never awaited by the caller.</summary>
    public void Enqueue(Write write)
    {
        if (Enabled && _queue.Writer.TryWrite(write)) Interlocked.Increment(ref _depth);
    }

    /// <summary>
    /// A write of a row that is written again and again: the latest write per <paramref name="key"/> is kept and
    /// goes to the database within <see cref="CoalesceInterval"/>, after the queued writes of that pass. For a row
    /// whose value only matters as its latest state, such as a read state.
    /// </summary>
    public void EnqueueCoalesced(object key, Write write)
    {
        if (!Enabled) return;
        lock (_coalesceGate)
        {
            if (_closed) return;
            _coalesced[key] = write;
            Volatile.Write(ref _coalescedCount, _coalesced.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        // Not bound to stoppingToken on purpose. StopAsync completes the queue instead, so this loop ends
        // after the last queued write and the last flush, and a normal shutdown loses nothing.
        while (true)
        {
            bool more;
            using (var timeout = new CancellationTokenSource(DelayUntilFlush()))
            {
                try { more = await _queue.Reader.WaitToReadAsync(timeout.Token); }
                catch (OperationCanceledException) { more = true; }   // a flush is due; the queue may be empty
            }
            var batch = new List<Write>(BatchSize);
            while (batch.Count < BatchSize && _queue.Reader.TryRead(out var write)) batch.Add(write);
            if (batch.Count > 0)
            {
                await RunAsync(batch);
                Interlocked.Add(ref _depth, -batch.Count);   // after the pass: the depth counts writes not yet on disk
            }
            // The queue first, then the rows: a read state's channel is queued at its creation, so its insert
            // is on disk before the row that points at it, as long as the row waits for the next flush.
            if (!more || DateTime.UtcNow - _lastFlush >= CoalesceInterval) await FlushCoalescedAsync();
            if (!more && _queue.Reader.Completion.IsCompleted && Volatile.Read(ref _coalescedCount) == 0) return;
        }
    }

    private TimeSpan DelayUntilFlush()
    {
        if (Volatile.Read(ref _coalescedCount) == 0) return Timeout.InfiniteTimeSpan;
        var left = _lastFlush + CoalesceInterval - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_coalesceGate) _closed = true;
        _queue.Writer.TryComplete();   // the host can stop twice (WebApplicationFactory does); Complete would throw the second time
        await base.StopAsync(cancellationToken);   // waits for ExecuteAsync up to the host's shutdown timeout
    }

    // The latest write of every key, in batches like the queue. A row whose write failed stays for the next flush:
    // its channel's insert may still have been in the queue (a foreign key), and the queue drains before each
    // flush. A row that fails three flushes in a row is dropped, so a row whose parent is gone cannot retry for ever.
    private async Task FlushCoalescedAsync()
    {
        List<KeyValuePair<object, Write>> rows;
        lock (_coalesceGate)
        {
            _lastFlush = DateTime.UtcNow;
            if (_coalesced.Count == 0) return;
            rows = [.. _coalesced];
            _coalesced = new();
            Volatile.Write(ref _coalescedCount, 0);
        }
        for (var i = 0; i < rows.Count; i += BatchSize)
        {
            var slice = rows.GetRange(i, Math.Min(BatchSize, rows.Count - i));
            var dropped = await RunAsync(slice.Select(p => p.Value).ToList());
            lock (_coalesceGate)
            {
                foreach (var (key, write) in slice)
                {
                    if (!dropped.Contains(write)) { _coalesceFailures.Remove(key); continue; }
                    var failures = _coalesceFailures.GetValueOrDefault(key) + 1;
                    if (failures >= 3) { _coalesceFailures.Remove(key); continue; }
                    _coalesceFailures[key] = failures;
                    _coalesced.TryAdd(key, write);   // a newer write for the key, if one came meanwhile, wins
                }
                Volatile.Write(ref _coalescedCount, _coalesced.Count);
            }
        }
    }

    // Up to BatchSize writes go into one transaction: one commit, which is the expensive part on SQLite, for
    // many rows. Returns the writes that failed and were dropped, so a coalesced flush can keep its rows.
    private async Task<IReadOnlyList<Write>> RunAsync(List<Write> batch)
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
                // The entities of every write so far stay tracked otherwise, and each save runs change detection
                // over all of them: a batch of N then costs N²/2. Cleared, write k costs the same as write 1.
                db.ChangeTracker.Clear();
            }
            await tx.CommitAsync();
            return [];
        }
        catch (Exception ex) when (batch.Count > 1)
        {
            // One bad write must not take the rest of the batch with it: redo them one at a time.
            _log.LogWarning(ex, "A batch of {Count} database writes failed; retrying them one by one", batch.Count);
            var dropped = new List<Write>();
            foreach (var write in batch) dropped.AddRange(await RunAsync([write]));
            return dropped;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            LastErrorAt = DateTime.UtcNow;
            _log.LogError(ex, "A database write failed and was dropped. Memory is still correct.");
            return batch;
        }
    }
}
