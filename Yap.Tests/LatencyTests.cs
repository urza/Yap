using Yap.Services.Accounts;
using Yap.Services.Presence;

namespace Yap.Tests;

/// <summary>The latency samples of AD-26: what lands on the connection, what lands on the person, what is dropped.</summary>
public class LatencyTests(YapFactory app) : IClassFixture<YapFactory>
{
    private readonly PresenceService _presence = app.Service<PresenceService>();
    private readonly UserService _users = app.Service<UserService>();

    private Session Connect(string name, string connection)
    {
        var user = _users.CreateUser(name)!;
        var session = new Session { ConnectionId = connection, UserId = user.Id, Username = user.Username, DeviceId = "d" };
        _presence.Connect(session);
        return session;
    }

    private LatencyStats? OfUser(Guid userId) => _presence.LatencyByUser().SingleOrDefault(u => u.UserId == userId).Latency;

    [Fact(DisplayName = "AD-26: a heartbeat sample lands on the connection and on the person, the round trip too")]
    public void Samples_land_on_connection_and_person()
    {
        var s = Connect("lat_both", "l1");
        _presence.Heartbeat("l1", visible: true, idleSeconds: 1, lastRttMs: 80, samples: [new("open", 300), new("open", 500)]);

        Assert.Equal(80, s.Latency.Of(Wait.Rtt)!.Last);
        var open = s.Latency.Of(Wait.Open)!;
        Assert.Equal((500d, 500d, 2), (open.Last, open.Max, open.Count));
        Assert.Equal(350, open.Avg);                                   // 0.75 * 300 + 0.25 * 500
        var mine = OfUser(s.UserId)!.Of(Wait.Open)!;
        Assert.Equal((open.Last, open.Avg, open.Max, open.Count), (mine.Last, mine.Avg, mine.Max, mine.Count));   // the same numbers on the person
        Assert.Equal(["open", "rtt"], s.Latency.Stats.Keys.Order());  // camelCase names for the JSON
    }

    [Fact(DisplayName = "AD-26: unknown names, server-only names and out-of-range numbers are dropped")]
    public void Untrusted_samples_are_dropped()
    {
        var s = Connect("lat_drop", "l2");
        _presence.Heartbeat("l2", visible: true, idleSeconds: 1, lastRttMs: -5,
            samples: [new("bogus", 10), new("post", 10), new("rtt", 10), new("send", -1), new("send", 600_001)]);

        Assert.Empty(s.Latency.Stats);
        Assert.Null(OfUser(s.UserId));
    }

    [Fact(DisplayName = "AD-26: a hidden beat carries no round trip and leaves the last one in place")]
    public void Hidden_beat_keeps_last_rtt()
    {
        var s = Connect("lat_hidden", "l3");
        _presence.Heartbeat("l3", visible: true, idleSeconds: 1, lastRttMs: 60, samples: null);
        _presence.Heartbeat("l3", visible: false, idleSeconds: 1, lastRttMs: null, samples: null);

        Assert.Equal((60d, 1), (s.Latency.Of(Wait.Rtt)!.Last, s.Latency.Of(Wait.Rtt)!.Count));
    }

    [Fact(DisplayName = "AD-26: a timed send lands on the person only; from 100 ms it is also a slow event")]
    public void Send_timing_is_per_person()
    {
        var s = Connect("lat_post", "l4");
        _presence.ReportRequest(s.UserId, s.Username, Wait.Post, 40);
        _presence.ReportRequest(s.UserId, s.Username, Wait.Post, 100);

        var person = OfUser(s.UserId)!;
        Assert.Equal((2, 1), (person.Of(Wait.Post)!.Count, person.SlowEventCount));
        Assert.Null(s.Latency.Of(Wait.Post));
    }
}
