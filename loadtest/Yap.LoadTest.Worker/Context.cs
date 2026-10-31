using System.Collections.Concurrent;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Worker;

/// <summary>What every virtual user in this process shares: the API, the live controls, the stats, the image pool and the link list.</summary>
public sealed class Context(int worker, YapApi api, Controls controls, Stats stats, int rooms, double visibleShare, string prefix, IReadOnlyList<string> links)
{
    public int Worker => worker;
    public YapApi Api => api;
    public Controls Controls => controls;
    public Stats Stats => stats;
    public int Rooms => rooms;                 // test rooms on the server; 1 means everyone in the lobby
    public double VisibleShare => visibleShare;
    public string Prefix => prefix;            // test account names start with it; the others are not DM peers
    public IReadOnlyList<string> Links => links;

    /// <summary>Uploaded image URLs, filled by the first image sends; later sends reuse them (the server allows it).</summary>
    public ConcurrentBag<string> ImagePool { get; } = [];
    public const int ImagePoolSize = 20;

    /// <summary>Custom emoji shortcodes from the bootstrap's emojiImages, kept by the first user that connects.</summary>
    public volatile IReadOnlyList<string> Shortcodes = [];
    private int _shortcodesClaimed;
    public bool ClaimShortcodes() => Interlocked.Exchange(ref _shortcodesClaimed, 1) == 0;
}
