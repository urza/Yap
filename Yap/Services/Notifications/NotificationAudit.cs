using System.Collections.Concurrent;
using Yap.Services.Presence;

namespace Yap.Services.Notifications;

/// <summary>
/// In-memory ring buffers answering "why didn't my phone buzz?" and "which device cleared my
/// unread badge?" from the admin Diagnostics tab — the two questions Debug-level logs kept eating.
/// Bounded ring buffers; lost on restart by design.
/// </summary>
public class NotificationAudit
{
    private const int Cap = 200;

    public record PushDecision(DateTime At, string From, string To, string Outcome, string Sessions, int Subscriptions, int TotalUnread);
    public record PushResult(DateTime At, string To, int Sent, int Failed, int Total, bool Muted, string? Note);
    public record UnreadChange(DateTime At, string User, string Channel, string Kind, int Count, string Source, string SessionState);

    private readonly ConcurrentQueue<PushDecision> _decisions = new();
    private readonly ConcurrentQueue<PushResult> _results = new();
    private readonly ConcurrentQueue<UnreadChange> _unread = new();
    /// <summary>The push/don't-push verdict made for a DM, with the session snapshot that drove it.</summary>
    public void RecordPushDecision(string from, string to, string outcome, string sessions, int subscriptions, int totalUnread) =>
        Append(_decisions, new PushDecision(DateTime.UtcNow, from, to, outcome, sessions, subscriptions, totalUnread));

    /// <summary>What actually happened when a push was attempted (covers DM sends and test sends).</summary>
    public void RecordPushResult(string to, int sent, int failed, int total, bool muted, string? note = null) =>
        Append(_results, new PushResult(DateTime.UtcNow, to, sent, failed, total, muted, note));

    /// <summary>A DM unread-count transition: "+1" on message arrival, "clear" on mark-read.</summary>
    public void RecordUnreadChange(string user, string channel, string kind, int count, string source, string sessionState) =>
        Append(_unread, new UnreadChange(DateTime.UtcNow, user, channel, kind, count, source, sessionState));

    /// <summary>
    /// Compact snapshot of the device that cleared unread: device, visibility, the person's effective status
    /// and whether the connection was alive. Together they say whether a mark-read was a person looking at
    /// the conversation or a stale session eating the badge.
    /// </summary>
    public string DescribeCallerSession(Session? session, EffectiveStatus presence)
    {
        if (session == null) return "no session";
        var device = session.IsMobile ? "mobile" : "desktop";
        return $"{device}, {(session.Visible ? "visible" : "hidden")}, {presence}, {(session.Connected ? "connected" : "disconnected")}";
    }

    // Newest first — these feed admin tables directly.
    public IReadOnlyList<PushDecision> GetPushDecisions() => _decisions.Reverse().ToList();
    public IReadOnlyList<PushResult> GetPushResults() => _results.Reverse().ToList();
    public IReadOnlyList<UnreadChange> GetUnreadChanges() => _unread.Reverse().ToList();

    private static void Append<T>(ConcurrentQueue<T> queue, T entry)
    {
        queue.Enqueue(entry);
        while (queue.Count > Cap && queue.TryDequeue(out _)) { }
    }
}
