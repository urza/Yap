namespace Yap.LoadTest.Worker;

/// <summary>
/// The virtual users of this process. The orchestrator sets a target with "users N". This loop starts users from
/// the slice at a fixed rate until N run, or stops the last ones at once. A stopped user disconnects cleanly, and
/// a user started again later signs in with the same account.
/// </summary>
public sealed class Crowd(Context ctx, IReadOnlyList<SavedUser> slice, int firstIndex, double usersPerSecond)
{
    private readonly List<(CancellationTokenSource Cts, Task Task)> _running = [];
    private readonly List<Task> _all = [];
    private int _target;

    public void SetTarget(int users) => Volatile.Write(ref _target, Math.Clamp(users, 0, slice.Count));

    public async Task RunAsync(CancellationToken ct)
    {
        var gap = usersPerSecond > 0 ? TimeSpan.FromSeconds(1 / usersPerSecond) : TimeSpan.Zero;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var target = Volatile.Read(ref _target);
                if (_running.Count < target)
                {
                    var i = _running.Count;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var task = new VirtualUser(ctx, slice[i].Username, slice[i].Token, firstIndex + i).RunAsync(cts.Token);
                    _running.Add((cts, task));
                    _all.Add(task);
                    await Task.Delay(gap, ct);   // people arrive one after the other, not as a herd
                }
                else if (_running.Count > target)
                {
                    var last = _running[^1];
                    _running.RemoveAt(_running.Count - 1);
                    last.Cts.Cancel();   // its loops end and its hub connection closes; the task finishes on its own
                }
                else await Task.Delay(100, ct);   // a new target arrives on stdin at any moment
            }
        }
        catch (OperationCanceledException) { }
        foreach (var (cts, _) in _running) cts.Cancel();
        try { await Task.WhenAll(_all); } catch { /* a user's last exception does not matter at the end */ }
    }
}
