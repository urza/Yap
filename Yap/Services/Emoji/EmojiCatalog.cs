namespace Yap.Services.Emoji;

/// <summary>
/// Everything the client needs to show and search emoji, as one document (PK-02 to PK-10): the ten categories
/// with their emoji, the keywords, the custom packs, and the image rule. The client renders every emoji as an
/// image from one set (GEN-20, MR-15), so all readers see the same picture.
///
/// The image rule, applied on the client: an emoji whose override key is in <see cref="EmojiImagesDto.Overrides"/>
/// comes from this server's /emoji-fallback folder; every other emoji comes from the Twemoji CDN. The two
/// conventions differ, so both are stated here and the client computes each.
/// </summary>
public sealed class EmojiCatalog(CustomEmojiService custom, IWebHostEnvironment env)
{
    public const string TwemojiBase = "https://cdn.jsdelivr.net/gh/jdecked/twemoji@latest/assets/svg";
    public const string OverridesBase = "/emoji-fallback";

    private readonly Lazy<EmojiCatalogDto> _document = new(() => Build(custom, env));

    public EmojiCatalogDto Document => _document.Value;

    private static EmojiCatalogDto Build(CustomEmojiService custom, IWebHostEnvironment env)
    {
        var categories = EmojiData.Categories.Select(c => new EmojiCategoryDto(c.Key, c.Value.Icon, c.Value.Emojis)).ToList();
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

        return new EmojiCatalogDto(categories, EmojiData.EmojiKeywords, packs, new EmojiImagesDto(TwemojiBase, OverridesBase, overrides));
    }
}

public sealed record EmojiCatalogDto(List<EmojiCategoryDto> Categories, Dictionary<string, string> Keywords, List<EmojiPackDto> Packs, EmojiImagesDto Images);

public sealed record EmojiCategoryDto(string Id, string Icon, string[] Emojis);

public sealed record EmojiPackDto(string Key, string Name, List<PackEmojiDto> Emojis);

public sealed record PackEmojiDto(string Shortcode, string Url, string Keywords);

/// <summary>
/// Where emoji images come from. Twemoji names a file by its codepoints in lowercase hex without zero padding,
/// joined by "-", with FE0F dropped unless the sequence contains a ZWJ (U+200D). An override is named by the
/// fully qualified codepoints, each padded to four digits, FE0F kept.
/// </summary>
public sealed record EmojiImagesDto(string TwemojiBase, string OverridesBase, List<string> Overrides);
