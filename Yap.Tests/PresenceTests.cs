using Yap.Models.Accounts;
using Yap.Services.Accounts;
using Yap.Services.Presence;

namespace Yap.Tests;

/// <summary>The rules of docs/architecture/presence.md, run against PresenceService with a chosen clock.</summary>
public class PresenceTests : IClassFixture<YapFactory>
{
    private readonly PresenceService _presence;
    private readonly UserService _users;

    public PresenceTests(YapFactory app)
    {
        _presence = app.Service<PresenceService>();
        _users = app.Service<UserService>();
    }

    private (User User, List<(string Username, EffectiveStatus Status, PresenceChange Change)> Events) Person(string name)
    {
        var user = _users.CreateUser(name)!;
        var events = new List<(string, EffectiveStatus, PresenceChange)>();
        _presence.OnPresenceChanged += (u, s, c) => { if (u == name) events.Add((u, s, c)); };
        return (user, events);
    }

    private static Session SessionOf(User user, string connection, string device = "d") => new()
    {
        ConnectionId = connection, UserId = user.Id, Username = user.Username, DeviceId = device,
    };

    [Fact(DisplayName = "PR-06, PR-17: the first connection is a join, a second tab is silent, a reload within 30 s is silent")]
    public void Join_once_and_reload_silent()
    {
        var (user, events) = Person("pr_join");
        _presence.Connect(SessionOf(user, "c1"));
        _presence.Connect(SessionOf(user, "c2"));
        Assert.Equal([("pr_join", EffectiveStatus.Online, PresenceChange.Join)], events);

        _presence.Disconnect("c1", closedByClient: true);
        _presence.Sweep(DateTime.UtcNow.AddSeconds(10));
        Assert.Single(events);   // nothing yet: the grace period runs
        _presence.Connect(SessionOf(user, "c3"));
        _presence.Sweep(DateTime.UtcNow.AddSeconds(31));
        Assert.Single(events);   // c1 is gone, c2 and c3 remain, the person never left
    }

    [Fact(DisplayName = "PR-14: a closed last tab is offline after 30 s, with no away phase")]
    public void Closed_tab_leaves_after_30s()
    {
        var (user, events) = Person("pr_close");
        _presence.Connect(SessionOf(user, "c1"));
        _presence.Disconnect("c1", closedByClient: true);
        _presence.Sweep(DateTime.UtcNow.AddSeconds(29));
        Assert.Single(events);
        _presence.Sweep(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(("pr_close", EffectiveStatus.Offline, PresenceChange.Leave), events[^1]);
    }

    [Fact(DisplayName = "PR-15, PR-16: a dropped connection is away after 30 s and offline after 5 min")]
    public void Dropped_connection_away_then_offline()
    {
        var (user, events) = Person("pr_drop");
        _presence.Connect(SessionOf(user, "c1"));
        _presence.Disconnect("c1", closedByClient: false);
        _presence.Sweep(DateTime.UtcNow.AddSeconds(31));
        Assert.Equal(("pr_drop", EffectiveStatus.Away, PresenceChange.Change), events[^1]);
        _presence.Sweep(DateTime.UtcNow.AddMinutes(5).AddSeconds(1));
        Assert.Equal(("pr_drop", EffectiveStatus.Offline, PresenceChange.Leave), events[^1]);
    }

    [Fact(DisplayName = "PR-10, PR-13: idle for 5 min on every device is away; input ends it at once")]
    public void Idle_away_and_input_restores()
    {
        var (user, events) = Person("pr_idle");
        _presence.Connect(SessionOf(user, "c1"));
        _presence.Heartbeat("c1", visible: true, idleSeconds: 301, lastRttMs: 40, samples: null);
        _presence.Sweep(DateTime.UtcNow);
        Assert.Equal(("pr_idle", EffectiveStatus.Away, PresenceChange.Change), events[^1]);
        _presence.Heartbeat("c1", visible: true, idleSeconds: 2, lastRttMs: 40, samples: null);
        Assert.Equal(("pr_idle", EffectiveStatus.Online, PresenceChange.Change), events[^1]);
    }

    [Fact(DisplayName = "PR-05, PR-12: invisible appears offline and never changes by itself; a chosen away stays")]
    public void Invisible_and_chosen_away()
    {
        var (user, events) = Person("pr_invisible");
        _presence.Connect(SessionOf(user, "c1"));
        _presence.SetChosenStatus(user.Id, UserStatus.Invisible);
        Assert.Equal(("pr_invisible", EffectiveStatus.Offline, PresenceChange.Change), events[^1]);   // a pick is a change: no lobby line (PR-05)
        Assert.DoesNotContain("pr_invisible", _presence.Effective().Keys);

        _presence.Heartbeat("c1", visible: true, idleSeconds: 999, lastRttMs: null, samples: null);
        _presence.Sweep(DateTime.UtcNow.AddMinutes(10));
        Assert.Equal(EffectiveStatus.Offline, _presence.EffectiveOf(user.Id));

        _presence.SetChosenStatus(user.Id, UserStatus.Away);
        Assert.Equal(("pr_invisible", EffectiveStatus.Away, PresenceChange.Change), events[^1]);
        _presence.Heartbeat("c1", visible: true, idleSeconds: 1, lastRttMs: null, samples: null);
        Assert.Equal(EffectiveStatus.Away, _presence.EffectiveOf(user.Id));   // a chosen away never self-reverts
    }

    [Fact(DisplayName = "PR-20, PR-21: typing is per person, repeated while it lasts, and ends with the last connection")]
    public void Typing()
    {
        var (user, _) = Person("pr_typing");
        var channel = Guid.NewGuid();
        _presence.Connect(SessionOf(user, "c1"));
        _presence.Connect(SessionOf(user, "c2"));
        _presence.SetViewing("c1", channel); _presence.SetViewing("c2", channel);   // a typist is a viewer of the conversation (PR-19)
        var typing = new List<(string, bool)>();
        _presence.OnTypingChanged += (c, u, on) => { if (c == channel) typing.Add((u, on)); };

        _presence.SetTyping("c1", channel, true);
        _presence.SetTyping("c2", channel, true);   // the same person twice is one typist, told twice: readers drop a typist 3.5 s after the last 'on' (PR-20)
        Assert.Equal([("pr_typing", true), ("pr_typing", true)], typing);
        Assert.Equal(["pr_typing"], _presence.Typists(channel));
        Assert.Empty(_presence.Typists(channel, except: "pr_typing"));

        _presence.SetTyping("c1", channel, false);
        Assert.Equal(2, typing.Count);                // c2 still types: no 'off'
        _presence.StopTyping(channel, "pr_typing");   // a send ends it for every connection (IB-59)
        Assert.Equal(("pr_typing", false), typing[^1]);
    }
}
