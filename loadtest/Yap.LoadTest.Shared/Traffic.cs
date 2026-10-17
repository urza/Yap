namespace Yap.LoadTest.Shared;

/// <summary>The kinds of traffic the virtual users produce. Each kind is one switch on the dashboard.</summary>
[Flags]
public enum Kinds
{
    None = 0,
    Text = 1,         // short and long texts, emoji, custom emoji, replies, and the bursts of quick follow-ups
    Reactions = 2,
    Links = 4,        // page and video links; the server fetches one preview per URL
    Images = 8,       // image messages; one in ten uploads a fresh file, the rest reuse the pool
    Dms = 16,
    All = Text | Reactions | Links | Images | Dms,
}

/// <summary>How often a visible user acts: the mean pause between actions, and the chance of a burst after a send.</summary>
public enum Pace { Calm, Normal, Busy, Frantic }

/// <summary>What one action of a visible user is.</summary>
public enum MessageKind { Short, Long, Emoji, CustomEmoji, Link, Image, Reply, Reaction, Dm }

public static class Traffic
{
    public static double ThinkSeconds(this Pace p) => p switch { Pace.Calm => 60, Pace.Normal => 20, Pace.Busy => 6, _ => 2 };
    public static double BurstChance(this Pace p) => p switch { Pace.Calm => 0.05, Pace.Normal => 0.15, _ => 0.5 };

    public static readonly Kinds[] Each = [Kinds.Text, Kinds.Reactions, Kinds.Links, Kinds.Images, Kinds.Dms];

    /// <summary>"text,links", "all" or "none"; what the orchestrator writes on a worker's stdin and in the results.</summary>
    public static Kinds ParseKinds(string s) => Enum.Parse<Kinds>(s, ignoreCase: true);
    public static string Format(Kinds k) => k == Kinds.All ? "all" : k == Kinds.None ? "none"
        : string.Join(",", Each.Where(e => k.HasFlag(e)).Select(e => e.ToString().ToLowerInvariant()));

    // The weights of a lively community: mostly text, then reactions, links, DMs, images. A kind that is off drops
    // out and the kinds that are on share its actions. The number of actions per second does not change with the
    // kinds; it comes from the pace and the number of visible users.
    private static readonly (MessageKind Kind, Kinds Switch, int Weight)[] Mix =
    [
        (MessageKind.Short, Kinds.Text, 38), (MessageKind.Long, Kinds.Text, 8), (MessageKind.Emoji, Kinds.Text, 10),
        (MessageKind.CustomEmoji, Kinds.Text, 4), (MessageKind.Reply, Kinds.Text, 10),
        (MessageKind.Reaction, Kinds.Reactions, 12), (MessageKind.Link, Kinds.Links, 7), (MessageKind.Dm, Kinds.Dms, 6), (MessageKind.Image, Kinds.Images, 5),
    ];

    /// <summary>One kind among those that are on, by weight. Null when nothing is on: the user only reads.</summary>
    public static MessageKind? Pick(Random rng, Kinds on)
    {
        var total = 0;
        foreach (var m in Mix) if (on.HasFlag(m.Switch)) total += m.Weight;
        if (total == 0) return null;
        var roll = rng.Next(total);
        foreach (var m in Mix)
        {
            if (!on.HasFlag(m.Switch)) continue;
            if (roll < m.Weight) return m.Kind;
            roll -= m.Weight;
        }
        return null;
    }
}

/// <summary>The settings a worker takes from the dashboard while it runs. The orchestrator writes one stdin line per change.</summary>
public sealed class Controls
{
    public volatile Kinds Kinds = Kinds.All;
    public volatile Pace Pace = Pace.Normal;
}
