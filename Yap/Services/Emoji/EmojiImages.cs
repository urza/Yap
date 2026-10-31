namespace Yap.Services.Emoji;

/// <summary>
/// Where emoji pictures come from, as the bootstrap carries it (GEN-20, MR-15): the Twemoji CDN for every emoji
/// except the overrides in wwwroot/emoji-fallback, and the custom packs with their shortcodes. The client renders
/// every emoji as an image from this one set, so all readers see the same picture. It needs these rules before its
/// first paint, which is why they travel with the bootstrap that the device stores, and not in a document of their
/// own: that document raced the paint, and the rows it lost showed the device's own emoji font (2026-10-31).
///
/// The picker's list (the categories and the search keywords) is not here. It is fixed for a build and ships with
/// the shell as wwwroot/js/data/emoji-list.js, which the picker imports on its first open.
///
/// The image rule, applied on the client: an emoji whose override key is in <see cref="EmojiImagesDto.Overrides"/>
/// comes from this server's /emoji-fallback folder; every other emoji comes from the Twemoji CDN. The two naming
/// conventions differ, so both are stated here and the client computes each.
/// </summary>
public sealed class EmojiImages(CustomEmojiService custom, IWebHostEnvironment env)
{
    // A pinned release. With @latest, a device froze each emoji at the drawing it saw first (the worker keeps the
    // picture for ever), so two devices could show two drawings of one emoji after an upstream release.
    public const string TwemojiBase = "https://cdn.jsdelivr.net/gh/jdecked/twemoji@17.0.3/assets/svg";
    public const string OverridesBase = "/emoji-fallback";

    // Built once per process: the packs load at startup (CustomEmojiService) and the overrides change with a deploy.
    private readonly Lazy<EmojiImagesDto> _rules = new(() => Build(custom, env));

    public EmojiImagesDto Rules => _rules.Value;

    private static EmojiImagesDto Build(CustomEmojiService custom, IWebHostEnvironment env)
    {
        var packs = custom.Packs
            .Where(p => p.Emojis.Count > 0)   // an empty pack is not shown (PK-09)
            .Select(p => new EmojiPackDto(p.Key, p.DisplayName, p.Emojis.Select(e => new PackEmojiDto(e.Shortcode, e.Url, e.Keywords)).ToList()))
            .ToList();

        // Self-hosted overrides for emoji the CDN lacks or draws badly. Drop a PNG named by its codepoint into
        // wwwroot/emoji-fallback and it is picked up at the next start.
        var dir = Path.Combine(env.WebRootPath, "emoji-fallback");
        var overrides = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.png").Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant()).Where(n => !n.StartsWith('_')).Order().ToList()
            : [];

        return new EmojiImagesDto(TwemojiBase, OverridesBase, overrides, packs);
    }
}

/// <summary>
/// Twemoji names a file by its codepoints in lowercase hex without zero padding, joined by "-", with FE0F dropped
/// unless the sequence contains a ZWJ (U+200D). An override is named by the fully qualified codepoints, each padded
/// to four digits, FE0F kept.
/// </summary>
public sealed record EmojiImagesDto(string TwemojiBase, string OverridesBase, List<string> Overrides, List<EmojiPackDto> Packs);

public sealed record EmojiPackDto(string Key, string Name, List<PackEmojiDto> Emojis);

public sealed record PackEmojiDto(string Shortcode, string Url, string Keywords);
