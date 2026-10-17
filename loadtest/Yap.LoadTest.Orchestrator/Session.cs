using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Orchestrator;

/// <summary>
/// What the person at the keyboard controls: how many users, which kinds of traffic, the pace. The key thread
/// changes it; the run loop reads a snapshot and makes the workers match. Every change bumps the version, and the
/// run loop starts a new segment in the results for it.
/// </summary>
public sealed class Session(int target, Kinds kinds, Pace pace)
{
    private readonly Lock _gate = new();
    private int _target = target, _version;
    private Kinds _kinds = kinds;
    private Pace _pace = pace;
    private string _typing = "";
    private DateTime _changedAt = DateTime.UtcNow;

    public sealed record Snapshot(int Version, int Target, Kinds Kinds, Pace Pace, string Typing, DateTime ChangedAt)
    {
        public string Label => $"{Target} users, kinds {Traffic.Format(Kinds)}, pace {Pace.ToString().ToLowerInvariant()}";
    }

    public bool Quit { get; private set; }

    public Snapshot Current { get { lock (_gate) return new Snapshot(_version, _target, _kinds, _pace, _typing, _changedAt); } }

    /// <summary>
    /// One key. + and - (or the up and down arrows) step the user count by 50, PageUp and PageDown by 250, digits
    /// and Enter set it exactly. t r l i d switch text, reactions, links, images and DMs. &lt; and &gt; change the
    /// pace. q quits. Every other key does nothing, so a wrong key cannot end the run.
    /// </summary>
    public void Key(ConsoleKeyInfo k)
    {
        lock (_gate)
        {
            switch (k.Key)
            {
                case ConsoleKey.UpArrow: Step(50); return;
                case ConsoleKey.DownArrow: Step(-50); return;
                case ConsoleKey.PageUp: Step(250); return;
                case ConsoleKey.PageDown: Step(-250); return;
                case ConsoleKey.Enter: if (int.TryParse(_typing, out var n)) SetTarget(n); _typing = ""; return;
                case ConsoleKey.Backspace: if (_typing.Length > 0) _typing = _typing[..^1]; return;
                case ConsoleKey.Escape: _typing = ""; return;
            }
            switch (char.ToLowerInvariant(k.KeyChar))
            {
                case >= '0' and <= '9': if (_typing.Length < 5) _typing += k.KeyChar; return;
                case '+' or '=': Step(50); return;
                case '-' or '_': Step(-50); return;
                case 't': Toggle(Kinds.Text); return;
                case 'r': Toggle(Kinds.Reactions); return;
                case 'l': Toggle(Kinds.Links); return;
                case 'i': Toggle(Kinds.Images); return;
                case 'd': Toggle(Kinds.Dms); return;
                case '<' or ',': SetPace(_pace - 1); return;
                case '>' or '.': SetPace(_pace + 1); return;
                case 'q': Quit = true; return;
            }
        }
    }

    private void Step(int by) => SetTarget(_target + by);
    private void SetTarget(int n) { n = Math.Max(0, n); if (n != _target) { _target = n; Changed(); } }
    private void Toggle(Kinds k) { _kinds ^= k; Changed(); }
    private void SetPace(Pace p) { if (Enum.IsDefined(p) && p != _pace) { _pace = p; Changed(); } }
    private void Changed() { _version++; _changedAt = DateTime.UtcNow; }
}
