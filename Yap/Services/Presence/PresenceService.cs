using Yap.Models.Accounts;
using Yap.Services.Accounts;
using Yap.Services.Admin;

namespace Yap.Services.Presence;

/// <summary>
/// Who is online, away or offline, and who types. The hub reports connections, heartbeats, status picks and
/// typing. One sweep every 10 seconds applies the grace periods of the spec. Every change of what others
/// see raises one event, after the lock. See docs/architecture/presence.md.
///
/// Three kinds of fact: the chosen status lives on <see cref="User.Status"/>; the sessions and the automatic
/// away flag live here, in memory only.
/// </summary>
public sealed class PresenceService(UserService users, LoadCounters load, ILogger<PresenceService> log) : BackgroundService
{
    private readonly ILogger<PresenceService> _log = log;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);    // PR-10, PR-13: "within 10 seconds"
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(30);       // PR-14, PR-17: a reload shows nothing
    private static readonly TimeSpan DropToAway = TimeSpan.FromSeconds(30);       // PR-15
    private static readonly TimeSpan DropToOffline = TimeSpan.FromMinutes(5);     // PR-16
    private static readonly TimeSpan IdleToAway = TimeSpan.FromMinutes(5);        // PR-10
    private static readonly TimeSpan HeartbeatStale = TimeSpan.FromSeconds(90);   // a session with no heartbeat has no live client behind it
    private static readonly TimeSpan ForegroundWindow = TimeSpan.FromSeconds(35); // visible and heartbeated within ~3 beats
    private static readonly TimeSpan InputFresh = TimeSpan.FromSeconds(30);       // input this recent ends an automatic away
    private static readonly TimeSpan TypingTtl = TimeSpan.FromSeconds(3);         // PR-20
    private static readonly TimeSpan BeaconNonceTtl = TimeSpan.FromMinutes(5);
    private const int RecentlyClosedCap = 50;                                     // AD-26

    // Everything below is read and changed under this lock. Nothing inside the lock does I/O or raises an
    // event; a transition collects what it caused and raises it after the lock, in order.
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly HashSet<Guid> _autoAway = new();
    private readonly Dictionary<Guid, Dictionary<string, (string Username, DateTime At)>> _typing = new(); // channel -> connection
    private readonly Queue<Session> _recentlyClosed = new();
    private readonly Dictionary<string, (string ConnectionId, DateTime At)> _beaconNonces = new();
    private int _totalConnections;

    /// <summary>What others see changed: username, the new effective status, and why.</summary>
    public event Action<string, EffectiveStatus, PresenceChange>? OnPresenceChanged;

    /// <summary>A person started or stopped typing in a conversation: channel id, username, on.</summary>
    public event Action<Guid, string, bool>? OnTypingChanged;

    /// <summary>The person picked a status; their other devices update the menu (PR-01).</summary>
    public event Action<Guid, UserStatus>? OnChosenStatusChanged;

    // ---- connections -----------------------------------------------------------------------------------

    public void Connect(Session session)
    {
        EffectiveStatus before, after;
        lock (_gate)
        {
            before = EffectiveLocked(session.UserId);
            _sessions[session.ConnectionId] = session;
            _totalConnections++;
            after = EffectiveLocked(session.UserId);
        }
        Announce(session.Username, before, after);
    }

    /// <summary>The connection ended. Nothing changes yet: the sweep applies the grace period, so a reload
    /// is silent (PR-17).</summary>
    public void Disconnect(string connectionId, bool closedByClient)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var s) || !s.Connected) return;
            if (closedByClient) s.ClosedAt = DateTime.UtcNow;
            else s.DroppedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Removes a session at once. Used by "sign out all other devices" (AU-20) and by the sweep.</summary>
    public void Remove(string connectionId)
    {
        var changes = new Changes();
        lock (_gate)
        {
            if (_sessions.TryGetValue(connectionId, out var s)) RemoveLocked(s, changes);
        }
        changes.Raise(this);
    }

    public void RegisterPermanent(Guid userId, string username) =>
        Connect(new Session { ConnectionId = $"permanent:{username}", UserId = userId, Username = username, DeviceId = "none", Permanent = true });

    // ---- heartbeat and status --------------------------------------------------------------------------

    /// <summary>The client's liveness report: visible, seconds since the last input, the previous round trip
    /// and the send-to-appear samples since the last beat (AD-26). Recent input ends an automatic away at
    /// once (PR-13).</summary>
    public void Heartbeat(string connectionId, bool visible, double idleSeconds, double? lastRttMs, IEnumerable<double>? sendToAppearMs)
    {
        if (!double.IsFinite(idleSeconds)) return;                 // client input is untrusted
        idleSeconds = Math.Clamp(idleSeconds, 0, 86400);

        var changes = new Changes();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var s)) return;
            var now = DateTime.UtcNow;
            s.Visible = visible;
            s.LastInputAt = now.AddSeconds(-idleSeconds);
            s.LastHeartbeatAt = now;
            if (lastRttMs is { } rtt && double.IsFinite(rtt)) s.Latency.ReportRtt(rtt);
            foreach (var ms in sendToAppearMs ?? []) if (double.IsFinite(ms)) s.Latency.ReportSendTiming(ms);

            if (idleSeconds < InputFresh.TotalSeconds && _autoAway.Contains(s.UserId))
            {
                var before = EffectiveLocked(s.UserId);
                _autoAway.Remove(s.UserId);
                changes.AddPresence(s.Username, before, EffectiveLocked(s.UserId));
            }
        }
        changes.Raise(this);
    }

    public void ReportSlowEvent(string connectionId, double ms)
    {
        lock (_gate) { if (_sessions.TryGetValue(connectionId, out var s)) s.Latency.ReportSlowEvent(ms); }
    }

    /// <summary>A pick in the status menu. Account-wide, so every device of the person follows (PR-01). A
    /// pick also ends an automatic away (PR-13).</summary>
    public void SetChosenStatus(Guid userId, UserStatus status)
    {
        var user = users.GetById(userId);
        if (user is null) return;

        var changes = new Changes();
        lock (_gate)
        {
            var before = EffectiveLocked(userId);
            users.SetStatus(userId, status);       // memory and an enqueued write; no I/O
            _autoAway.Remove(userId);
            // A pick is never a join or a leave, so an invisible toggle makes no lobby line (PR-05).
            changes.AddPresence(user.Username, before, EffectiveLocked(userId), PresenceChange.Change);
        }
        OnChosenStatusChanged?.Invoke(userId, status);
        changes.Raise(this);
    }

    /// <summary>Online, Away, Invisible or Offline for the admin's table. Only the admin sees Invisible as such (PR-05, AD-04).</summary>
    public string StatusPill(Guid userId) => EffectiveOf(userId) switch
    {
        EffectiveStatus.Online => "online",
        EffectiveStatus.Away => "away",
        _ => users.GetById(userId)?.Status == UserStatus.Invisible && HasSession(userId) ? "invisible" : "offline",
    };

    // ---- the sweep --------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                Sweep(DateTime.UtcNow);
                load.Sweep(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);   // grows with the session count; the load card shows it
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One pass of the rules. The tests call it with a chosen clock.</summary>
    internal void Sweep(DateTime now)
    {
        var changes = new Changes();
        lock (_gate)
        {
            foreach (var s in _sessions.Values.Where(s => !s.Permanent).ToList())
            {
                if (s.ClosedAt is { } closed && now - closed > CloseGrace) RemoveLocked(s, changes);
                else if (s.DroppedAt is { } dropped && now - dropped > DropToOffline) RemoveLocked(s, changes);
            }

            foreach (var userId in _sessions.Values.Where(s => !s.Permanent).Select(s => s.UserId).Distinct().ToList())
            {
                if (_autoAway.Contains(userId)) continue;
                if (users.GetById(userId)?.Status != UserStatus.Online) continue;   // a chosen Away or Invisible never changes by itself (PR-12)

                var mine = _sessions.Values.Where(s => s.UserId == userId).ToList();
                var connected = mine.Where(s => s.Connected).ToList();
                var anyVisible = connected.Any(s => s.Visible && now - s.LastHeartbeatAt <= ForegroundWindow);
                var allIdle = connected.Count > 0 && connected.All(s => now - s.LastInputAt >= IdleToAway || now - s.LastHeartbeatAt > HeartbeatStale);
                var droppedAWhile = mine.Any(s => s.DroppedAt is { } d && now - d > DropToAway);

                if (allIdle || (droppedAWhile && !anyVisible))   // PR-10, PR-15
                {
                    var before = EffectiveLocked(userId);
                    _autoAway.Add(userId);
                    changes.AddPresence(mine[0].Username, before, EffectiveLocked(userId));
                }
            }
        }
        changes.Raise(this);
    }

    // ---- typing -----------------------------------------------------------------------------------------

    /// <summary>One connection started or stopped typing. The person counts as typing while any connection of theirs
    /// typed within the last 3 s. Typing counts for the conversation the connection has open, and for no other: a
    /// typist shows only to the people who share the conversation (PR-19), and a channel id from the client is not
    /// an entry ticket.</summary>
    public void SetTyping(string connectionId, Guid channelId, bool on)
    {
        var changes = new Changes();
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var s) || s.Viewing != channelId) return;
            var now = DateTime.UtcNow;
            var typists = GetTypistsLocked(channelId);
            var was = IsTypingLocked(typists, s.Username, now);
            if (on) typists[connectionId] = (s.Username, now);
            else typists.Remove(connectionId);
            var isNow = IsTypingLocked(typists, s.Username, now);
            // Every 'on' goes out, not only the first: the client repeats it every 2 s and the readers drop a
            // typist 3.5 s after the last one they saw (PR-20).
            if (on ? isNow : was != isNow) changes.AddTyping(channelId, s.Username, isNow);
        }
        changes.Raise(this);
    }

    /// <summary>A send ends typing for all connections of the person (IB-59).</summary>
    public void StopTyping(Guid channelId, string username)
    {
        var changes = new Changes();
        lock (_gate)
        {
            if (!_typing.TryGetValue(channelId, out var typists)) return;
            var was = IsTypingLocked(typists, username, DateTime.UtcNow);
            foreach (var key in typists.Where(t => t.Value.Username == username).Select(t => t.Key).ToList())
                typists.Remove(key);
            if (was) changes.AddTyping(channelId, username, false);
        }
        changes.Raise(this);
    }

    /// <summary>Who types in a conversation right now, in the order they started, without the viewer (PR-21).</summary>
    public List<string> Typists(Guid channelId, string? except = null)
    {
        lock (_gate)
        {
            if (!_typing.TryGetValue(channelId, out var typists)) return [];
            var now = DateTime.UtcNow;
            foreach (var stale in typists.Where(t => now - t.Value.At > TypingTtl).Select(t => t.Key).ToList())
                typists.Remove(stale);
            return typists.Values.OrderBy(t => t.At).Select(t => t.Username)
                .Where(u => except is null || !u.Equals(except, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        }
    }

    public void ForgetChannel(Guid channelId)
    {
        lock (_gate) _typing.Remove(channelId);
    }

    // ---- questions other services ask ------------------------------------------------------------------

    /// <summary>People with Yap open on at least one device right now (NT-03).</summary>
    public HashSet<Guid> ConnectedUserIds()
    {
        lock (_gate) return _sessions.Values.Where(s => s.Connected && !s.Permanent).Select(s => s.UserId).ToHashSet();
    }

    /// <summary>Online, and looking at Yap on some device. The one condition that suppresses push (NT-26).</summary>
    public bool IsOnlineAndVisible(Guid userId)
    {
        lock (_gate)
            return EffectiveLocked(userId) == EffectiveStatus.Online && _sessions.Values.Any(s => s.UserId == userId && s.Connected && s.Visible);
    }

    public bool HasConnectedSessionFromIp(Guid userId, string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        lock (_gate) return _sessions.Values.Any(s => s.UserId == userId && s.Connected && s.ClientIp == ip);
    }

    public bool IsMobile(Guid userId)
    {
        lock (_gate) return _sessions.Values.Any(s => s.UserId == userId && s.IsMobile);
    }

    /// <summary>Any session at all, connected or within its grace. The admin page shows Invisible for such a person (AD-04).</summary>
    public bool HasSession(Guid userId)
    {
        lock (_gate) return _sessions.Values.Any(s => s.UserId == userId);
    }

    public EffectiveStatus EffectiveOf(Guid userId)
    {
        lock (_gate) return EffectiveLocked(userId);
    }

    /// <summary>Everyone who is online or away, for the bootstrap (SH-29). Invisible people are absent.</summary>
    public Dictionary<string, EffectiveStatus> Effective()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, EffectiveStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _sessions.Values.GroupBy(s => s.UserId))
            {
                var presence = EffectiveLocked(group.Key);
                if (presence != EffectiveStatus.Offline) result[group.First().Username] = presence;
            }
            return result;
        }
    }

    /// <summary>Ends a connection from the server side: the hub drops it, and presence forgets it now rather than at
    /// the disconnect that follows. A sign-out ends the device's other tabs, sign-out-others the other devices.</summary>
    public void End(Session session)
    {
        session.Abort?.Invoke();
        Remove(session.ConnectionId);
    }

    public Session? Get(string connectionId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(connectionId);
    }

    public List<Session> SessionsOf(Guid userId)
    {
        lock (_gate) return _sessions.Values.Where(s => s.UserId == userId && !s.Permanent).ToList();
    }

    public List<Session> All()
    {
        lock (_gate) return _sessions.Values.Where(s => !s.Permanent).ToList();
    }

    public List<Session> RecentlyClosed()
    {
        lock (_gate) return _recentlyClosed.Reverse().ToList();   // newest first
    }

    public (int Connected, int Disconnected, int TotalCreated) Stats()
    {
        lock (_gate)
        {
            var live = _sessions.Values.Where(s => !s.Permanent).ToList();
            return (live.Count(s => s.Connected), live.Count(s => !s.Connected), _totalConnections);
        }
    }

    public void SetViewing(string connectionId, Guid? channelId)
    {
        lock (_gate) { if (_sessions.TryGetValue(connectionId, out var s)) s.Viewing = channelId; }
    }

    /// <summary>One line per session of a person, for the push and unread audits.</summary>
    public string Describe(Guid userId)
    {
        var now = DateTime.UtcNow;
        List<string> lines;
        lock (_gate)
            lines = _sessions.Values.Where(s => s.UserId == userId && !s.Permanent)
                .Select(s => $"{(s.IsMobile ? "mobile" : "desktop")} {(s.Connected ? "connected" : "disconnected")} {(s.Visible ? "visible" : "hidden")} idle={(int)(now - s.LastInputAt).TotalSeconds}s")
                .ToList();
        return lines.Count == 0 ? "no sessions" : string.Join(", ", lines);
    }

    // ---- the IPv4 beacon --------------------------------------------------------------------------------
    // The beacon URL is anonymous and shows up in proxy logs, so it carries a single-use nonce instead of a
    // connection id. The recorded address is display-only and never feeds an auth decision.

    /// <summary>A nonce for the IPv4 beacon of one connection, or null when the connection is not this person's: the
    /// beacon writes the address onto that session, so only the session's own person may ask for one.</summary>
    public string? CreateBeaconNonce(string connectionId, Guid userId)
    {
        var nonce = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            if (!_sessions.TryGetValue(connectionId, out var s) || s.UserId != userId) return null;
            foreach (var stale in _beaconNonces.Where(n => DateTime.UtcNow - n.Value.At > BeaconNonceTtl).Select(n => n.Key).ToList())
                _beaconNonces.Remove(stale);
            _beaconNonces[nonce] = (connectionId, DateTime.UtcNow);
        }
        return nonce;
    }

    public void RecordBeaconIpv4(string nonce, string ipv4)
    {
        lock (_gate)
        {
            if (_beaconNonces.Remove(nonce, out var entry) && DateTime.UtcNow - entry.At <= BeaconNonceTtl
                && _sessions.TryGetValue(entry.ConnectionId, out var s))
                s.ClientIpv4 = ipv4;
        }
    }

    // ---- inside the lock --------------------------------------------------------------------------------

    private EffectiveStatus EffectiveLocked(Guid userId)
    {
        if (!_sessions.Values.Any(s => s.UserId == userId)) return EffectiveStatus.Offline;
        return users.GetById(userId)?.Status switch
        {
            UserStatus.Invisible => EffectiveStatus.Offline,                                  // PR-05
            UserStatus.Away => EffectiveStatus.Away,
            _ => _autoAway.Contains(userId) ? EffectiveStatus.Away : EffectiveStatus.Online,          // PR-04
        };
    }

    private void RemoveLocked(Session s, Changes changes)
    {
        var before = EffectiveLocked(s.UserId);
        _sessions.Remove(s.ConnectionId);
        foreach (var (channelId, typists) in _typing)
        {
            if (!typists.Remove(s.ConnectionId)) continue;
            if (!IsTypingLocked(typists, s.Username, DateTime.UtcNow)) changes.AddTyping(channelId, s.Username, false);
        }
        if (!s.Permanent)
        {
            _recentlyClosed.Enqueue(s);
            while (_recentlyClosed.Count > RecentlyClosedCap) _recentlyClosed.Dequeue();
        }
        if (!_sessions.Values.Any(x => x.UserId == s.UserId)) _autoAway.Remove(s.UserId);
        changes.AddPresence(s.Username, before, EffectiveLocked(s.UserId));
    }

    private Dictionary<string, (string Username, DateTime At)> GetTypistsLocked(Guid channelId)
    {
        if (!_typing.TryGetValue(channelId, out var typists)) _typing[channelId] = typists = new();
        return typists;
    }

    private static bool IsTypingLocked(Dictionary<string, (string Username, DateTime At)> typists, string username, DateTime now) =>
        typists.Values.Any(t => t.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && now - t.At <= TypingTtl);

    private void Announce(string username, EffectiveStatus before, EffectiveStatus after)
    {
        var changes = new Changes();
        changes.AddPresence(username, before, after);
        changes.Raise(this);
    }

    /// <summary>What a transition caused, collected under the lock and raised after it.</summary>
    private sealed class Changes
    {
        private readonly List<(string Username, EffectiveStatus After, PresenceChange Kind)> _presence = [];
        private readonly List<(Guid ChannelId, string Username, bool On)> _typing = [];

        public void AddPresence(string username, EffectiveStatus before, EffectiveStatus after, PresenceChange? kind = null)
        {
            if (before == after) return;
            kind ??= before == EffectiveStatus.Offline ? PresenceChange.Join
                     : after == EffectiveStatus.Offline ? PresenceChange.Leave
                     : PresenceChange.Change;
            _presence.Add((username, after, kind.Value));
        }

        public void AddTyping(Guid channelId, string username, bool on) => _typing.Add((channelId, username, on));

        public void Raise(PresenceService owner)
        {
            foreach (var (username, after, kind) in _presence)
            {
                owner._log.LogDebug("EffectiveStatus {User}: {EffectiveStatus} ({Kind})", username, after, kind);
                owner.OnPresenceChanged?.Invoke(username, after, kind);
            }
            foreach (var (channelId, username, on) in _typing)
                owner.OnTypingChanged?.Invoke(channelId, username, on);
        }
    }
}
