using System.Collections.Concurrent;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Worker;

/// <summary>What every virtual user in this process shares: the API, the preset, the stats, the image pool and the link list.</summary>
public sealed class Context(int worker, YapApi api, Preset preset, Stats stats, IReadOnlyList<string> links, double? visibleShare)
{
    public int Worker => worker;
    public YapApi Api => api;
    public Preset Preset => preset;
    public Stats Stats => stats;
    public IReadOnlyList<string> Links => links;
    public double VisibleShare => visibleShare ?? preset.VisibleShare;

    /// <summary>Uploaded image URLs, filled by the first image sends; later sends reuse them (the server allows it).</summary>
    public ConcurrentBag<string> ImagePool { get; } = [];
    public const int ImagePoolSize = 20;

    /// <summary>Custom emoji shortcodes from /api/emoji, fetched once by the first user that connects.</summary>
    public volatile IReadOnlyList<string> Shortcodes = [];
    private int _catalogFetched;
    public bool ClaimCatalogFetch() => Interlocked.Exchange(ref _catalogFetched, 1) == 0;
}
