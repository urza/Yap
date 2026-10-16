namespace Yap.LoadTest.Shared;

/// <summary>What a visible user sends, as weights. The sum is free; the picker normalises.</summary>
public sealed record Mix(int Short = 40, int Long = 10, int Emoji = 10, int CustomEmoji = 5, int Link = 8, int Image = 5, int Reply = 10, int Reaction = 10, int Dm = 2)
{
    public MessageKind Pick(Random rng)
    {
        var total = Short + Long + Emoji + CustomEmoji + Link + Image + Reply + Reaction + Dm;
        var roll = rng.Next(total);
        return roll < Short ? MessageKind.Short
            : (roll -= Short) < Long ? MessageKind.Long
            : (roll -= Long) < Emoji ? MessageKind.Emoji
            : (roll -= Emoji) < CustomEmoji ? MessageKind.CustomEmoji
            : (roll -= CustomEmoji) < Link ? MessageKind.Link
            : (roll -= Link) < Image ? MessageKind.Image
            : (roll -= Image) < Reply ? MessageKind.Reply
            : (roll -= Reply) < Reaction ? MessageKind.Reaction
            : MessageKind.Dm;
    }
}

public enum MessageKind { Short, Long, Emoji, CustomEmoji, Link, Image, Reply, Reaction, Dm }

/// <summary>
/// One shape of a community. Rooms are created by the orchestrator as lt-room-NN (1 means everyone in the lobby).
/// Think time is the mean pause between a visible user's actions. The hidden share only heartbeats.
/// </summary>
public sealed record Preset(
    string Name,
    string Description,
    int Rooms,
    double VisibleShare,
    double MeanThinkSeconds,
    double BurstChance,          // after a send: a run of 2 to 5 quick follow-ups
    double RoomHopChance,        // after a send: open another room for a while
    double FreshUploadShare,     // image sends that upload a new file instead of reusing the pool
    bool LinkPreviews,
    bool MediaCaching,
    Mix Mix)
{
    public static readonly Preset[] All =
    [
        new("big-room", "Everyone in the lobby. The fan-out of one message reaches every connection.",
            Rooms: 1, VisibleShare: 0.3, MeanThinkSeconds: 20, BurstChance: 0.15, RoomHopChance: 0, FreshUploadShare: 0.1, LinkPreviews: true, MediaCaching: true, new Mix()),
        new("small-rooms", "Twenty rooms, each user has a home room and hops now and then.",
            Rooms: 20, VisibleShare: 0.3, MeanThinkSeconds: 20, BurstChance: 0.15, RoomHopChance: 0.1, FreshUploadShare: 0.1, LinkPreviews: true, MediaCaching: true, new Mix()),
        new("dm-heavy", "Half of the traffic is direct messages between users.",
            Rooms: 5, VisibleShare: 0.4, MeanThinkSeconds: 15, BurstChance: 0.2, RoomHopChance: 0.05, FreshUploadShare: 0.1, LinkPreviews: true, MediaCaching: false,
            new Mix(Short: 30, Long: 5, Emoji: 10, CustomEmoji: 3, Link: 4, Image: 3, Reply: 5, Reaction: 5, Dm: 50)),
        new("media-heavy", "Images and video links dominate; every third image is a fresh upload.",
            Rooms: 5, VisibleShare: 0.3, MeanThinkSeconds: 25, BurstChance: 0.05, RoomHopChance: 0.05, FreshUploadShare: 0.33, LinkPreviews: true, MediaCaching: true,
            new Mix(Short: 20, Long: 5, Emoji: 5, CustomEmoji: 2, Link: 25, Image: 30, Reply: 5, Reaction: 6, Dm: 2)),
        new("burst", "Short think time, many bursts, mostly short texts and emoji. The send path under pressure.",
            Rooms: 3, VisibleShare: 0.5, MeanThinkSeconds: 6, BurstChance: 0.5, RoomHopChance: 0.05, FreshUploadShare: 0.05, LinkPreviews: false, MediaCaching: false,
            new Mix(Short: 55, Long: 5, Emoji: 20, CustomEmoji: 5, Link: 2, Image: 1, Reply: 5, Reaction: 5, Dm: 2)),
        new("idle-crowd", "Almost everyone hidden and idle. The cost of presence, heartbeats and connections alone.",
            Rooms: 1, VisibleShare: 0.05, MeanThinkSeconds: 60, BurstChance: 0.05, RoomHopChance: 0, FreshUploadShare: 0.05, LinkPreviews: false, MediaCaching: false, new Mix()),
    ];

    public static Preset? Find(string name) => All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
