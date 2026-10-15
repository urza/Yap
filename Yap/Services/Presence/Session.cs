namespace Yap.Services.Presence;

/// <summary>What other people see (PR-01, PR-05). "Offline" is no connection, or a chosen Invisible.</summary>
public enum EffectiveStatus { Online, Away, Offline }

/// <summary>Why an effective status changed. The client draws a lobby line for Join and Leave only
/// (MR-73), never for Change, so an invisible toggle makes no line (PR-05).</summary>
public enum PresenceChange { Join, Leave, Change }

/// <summary>
/// One hub connection of one person. Sessions are created when a connection opens and removed by the
/// presence sweep after the grace period of the spec. A person has one session per open tab.
/// </summary>
public sealed class Session
{
    public required string ConnectionId { get; init; }
    public required Guid UserId { get; init; }
    public required string Username { get; init; }

    /// <summary>A random id the client keeps in local storage. Tabs on one device share it, so Settings can
    /// list one row per device (AU-19).</summary>
    public required string DeviceId { get; init; }

    public bool IsMobile { get; init; }                  // from the User-Agent at connect (PR-25)
    public string? ClientIp { get; init; }
    public string? ClientIpv4 { get; set; }              // from the opt-in IPv4 beacon; display only, never an auth input
    public string Transport { get; init; } = "";         // WebSockets, ServerSentEvents or LongPolling (AD-26)
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;

    /// <summary>The bot's session: always online, never swept, never listed as a device (PR-09).</summary>
    public bool Permanent { get; init; }

    // Two stamps for two ways to disconnect, because the spec treats them differently: a closed tab is
    // offline after 30 s with no away phase (PR-14), a lost connection is away after 30 s and offline
    // after 5 min (PR-15, PR-16). SignalR tells the two apart.
    public DateTime? ClosedAt { get; set; }
    public DateTime? DroppedAt { get; set; }
    public bool Connected => ClosedAt is null && DroppedAt is null;

    // From the heartbeat (every 10 s visible, 60 s hidden).
    public bool Visible { get; set; } = true;
    public DateTime LastInputAt { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>The conversation this connection has open. For the diagnostics table and for typing.</summary>
    public Guid? Viewing { get; set; }

    public LatencyStats Latency { get; } = new();

    /// <summary>Closes the connection from outside the hub. Captured from HubCallerContext at connect, used
    /// by "sign out all other devices" (AU-20).</summary>
    public Action? Abort { get; init; }
}

/// <summary>
/// Latency of one connection, for the admin diagnostics table (AD-26). Round trips are measured by the
/// client around its heartbeat. Send-to-appear is measured by the client from the tap to the row.
/// Slow events are hub calls that took over 100 ms on the server. Averages are exponentially weighted
/// (0.75 old, 0.25 new), so one good sample does not hide a bad streak.
/// </summary>
public sealed class LatencyStats
{
    public double? LastRttMs { get; private set; }
    public double? AvgRttMs { get; private set; }
    public double? MaxRttMs { get; private set; }
    public int RttSamples { get; private set; }
    public DateTime? RttUpdatedAt { get; private set; }

    public int SlowEventCount { get; private set; }
    public double? MaxEventMs { get; private set; }
    public DateTime? LastSlowEventAt { get; private set; }

    public double? LastSendToAppearMs { get; private set; }
    public double? AvgSendToAppearMs { get; private set; }
    public double? MaxSendToAppearMs { get; private set; }
    public int SendSamples { get; private set; }
    public DateTime? SendTimingAt { get; private set; }

    public void ReportRtt(double ms)
    {
        LastRttMs = ms;
        AvgRttMs = AvgRttMs is { } avg ? 0.75 * avg + 0.25 * ms : ms;
        MaxRttMs = Math.Max(MaxRttMs ?? 0, ms);
        RttSamples++;
        RttUpdatedAt = DateTime.UtcNow;
    }

    public void ReportSlowEvent(double ms)
    {
        SlowEventCount++;
        MaxEventMs = Math.Max(MaxEventMs ?? 0, ms);
        LastSlowEventAt = DateTime.UtcNow;
    }

    public void ReportSendTiming(double ms)
    {
        LastSendToAppearMs = ms;
        AvgSendToAppearMs = AvgSendToAppearMs is { } avg ? 0.75 * avg + 0.25 * ms : ms;
        MaxSendToAppearMs = Math.Max(MaxSendToAppearMs ?? 0, ms);
        SendSamples++;
        SendTimingAt = DateTime.UtcNow;
    }
}
