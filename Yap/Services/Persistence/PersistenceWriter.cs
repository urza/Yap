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
    private const int MaxBatchesPerPass = 20;   // 10,000 writes; more than that in one pass is a backlog, not a pass

    // Writes that are the same row again and again keep only the latest one per key, flushed at most once per
    // interval. Read states are the case: a message in a room of a thousand queued one write per recipient, 14,000
    // a second on the 2026-10-18 load test for about 1,000 distinct rows; the writer drained 3,000 to 5,000 a second
    // and the backlog reached 6.3 million writes and 1 GB. After a crash up to one interval of these rows is behind;
    // memory is the truth and the database the copy, so that is in line with the design (memory-first-data-layer).
    private static readonly TimeSpan CoalesceInterval = TimeSpan.FromSeconds(2);
    private readonly Lock _coalesceGate = new();
    private Dictionary<object, Write> _coalesced = new();
    private int _flushFailures;   // flushes in a row whose slice failed whole; the third one is taken apart to find the bad row
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
            // The queue, until it is empty: a read state's channel is queued at its creation, and the row may only be
            // flushed once that insert is on disk. One batch per pass was not enough: a channel queued while the
            // batch ran, or anywhere in a backlog, was still in the queue when the flush followed, and on the
            // 2026-10-19 run the rows failed their foreign key by the thousand. A pass drains up to MaxBatchesPerPass
            // batches; past that the queue is a backlog, the rows wait in memory (the latest state wins) and the
            // load card shows them in the depth. At shutdown the queue is drained whole.
            var emptied = false;
            for (var passes = 0; !more || passes < MaxBatchesPerPass; passes++)
            {
                var batch = new List<Write>(BatchSize);
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var write)) batch.Add(write);
                if (batch.Count == 0) { emptied = true; break; }
                await RunAsync(batch);
                Interlocked.Add(ref _depth, -batch.Count);   // after the pass: the depth counts writes not yet on disk
                if (batch.Count < BatchSize) { emptied = true; break; }
            }
            if (emptied && (!more || DateTime.UtcNow - _lastFlush >= CoalesceInterval)) await FlushCoalescedAsync();
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

    // The latest write of every key, in slices like the queue's batches. A slice that fails stays whole for the
    // next flush: the usual cause is a channel insert queued a moment ago, and by the next flush the queue has
    // drained it. Taking a failed slice apart into single commits is the slow path on SQLite; done on every
    // flush it built the backlog it was meant to clear (2026-10-19). The third failure in a row is taken apart,
    // so a row whose parent is gone for good is found and dropped, and the rest of its slice goes in.
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
            var dropped = await RunAsync(slice.Select(p => p.Value).ToList(), isolate: _flushFailures >= 2);
            lock (_coalesceGate)
            {
                if (dropped is null)   // the slice failed whole and nothing of it was written: every row waits for the next flush
                {
                    _flushFailures++;
                    foreach (var (key, write) in slice) _coalesced.TryAdd(key, write);   // a newer write for the key, if one came meanwhile, wins
                }
                else _flushFailures = 0;
                Volatile.Write(ref _coalescedCount, _coalesced.Count);
            }
        }
    }

    // Up to BatchSize writes go into one transaction: one commit, which is the expensive part on SQLite, for
    // many rows. Returns the writes that failed and were dropped. With <paramref name="isolate"/> off, a batch
    // that fails is not taken apart: nothing of it is written and the result is null, for a caller that can
    // simply try the whole batch again later.
    private async Task<IReadOnlyList<Write>?> RunAsync(List<Write> batch, bool isolate = true)
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
        catch (Exception ex) when (batch.Count > 1 && !isolate)
        {
            _log.LogInformation("A flush of {Count} coalesced rows failed ({Reason}); they wait for the next flush", batch.Count, ex.Message);
            return null;
        }
        catch (Exception ex) when (batch.Count > 1)
        {
            // One bad write must not take the rest of the batch with it: redo them one at a time.
            _log.LogWarning(ex, "A batch of {Count} database writes failed; retrying them one by one", batch.Count);
            var dropped = new List<Write>();
            foreach (var write in batch) dropped.AddRange(await RunAsync([write]) ?? []);   // a single write is always isolated
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
