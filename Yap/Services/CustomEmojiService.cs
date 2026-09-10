using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yap.Services;

/// <summary>
/// Image emoji — pictures with no Unicode codepoint, addressed in message text by
/// <c>:shortcode:</c>. Two sources feed one flat shortcode namespace:
/// <list type="bullet">
///   <item><b>Server customs</b> — <c>Data/custom-emojis/</c>, dropped in per deployment (gitignored).</item>
///   <item><b>Built-in packs</b> — <c>wwwroot/emoji-packs/&lt;pack&gt;/</c>, committed to the repo, so
///     they ship in the build output of every instance.</item>
/// </list>
/// The server folder is scanned <i>first</i> and claims its shortcodes — that is what makes it the
/// override: a deployment replaces a shipped emoji simply by naming its file the same, and the
/// shadowed built-in drops out of its pack. Both trees are scanned once at startup.
/// <para>
/// Either folder may hold a <c>keywords.json</c> (see <see cref="LoadKeywords"/>) that adds search
/// terms to its own emoji — the filename alone is the shortcode, so it is the only thing the picker
/// could otherwise match on.
/// </para>
/// </summary>
public partial class CustomEmojiService
{
    // Flat shortcode namespace across both sources — first writer wins (see scan order above).
    private readonly Dictionary<string, CustomEmoji> _emojis = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EmojiPack> _packs = new();
    private readonly ILogger<CustomEmojiService> _logger;

    private const string ServerPackKey = "custom";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".svg", ".gif", ".webp", ".jpg", ".jpeg"
    };

    // Guards both emoji filenames (-> shortcode) and pack folder names (-> picker key).
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidNameRegex();

    public bool HasCustomEmojis => _emojis.Count > 0;

    /// <summary>Picker groups in display order: server customs first, then built-in packs A–Z.</summary>
    public IReadOnlyList<EmojiPack> Packs => _packs;

    public CustomEmojiService(IWebHostEnvironment env, ILogger<CustomEmojiService> logger)
    {
        _logger = logger;

        var serverFolder = Path.Combine(env.ContentRootPath, "Data", "custom-emojis");
        Directory.CreateDirectory(serverFolder);
        AddPack(ServerPackKey, "Custom",
            ScanFolder(serverFolder, "/custom-emojis", ServerPackKey, isBuiltIn: false));

        AddBuiltInPacks(Path.Combine(env.WebRootPath, "emoji-packs"));

        _logger.LogInformation("Loaded {Count} image emoji across {Packs} pack(s)", _emojis.Count, _packs.Count);
    }

    /// <summary>One pack per subfolder of <c>wwwroot/emoji-packs/</c>; the folder name is the pack name.</summary>
    private void AddBuiltInPacks(string packsRoot)
    {
        if (!Directory.Exists(packsRoot))
            return;

        foreach (var dir in Directory.GetDirectories(packsRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (!ValidNameRegex().IsMatch(name))
            {
                _logger.LogWarning("Skipping emoji pack with invalid folder name: {Pack}", name);
                continue;
            }

            // Prefixed so a pack may be named after a standard Unicode category ("food") without
            // colliding with it in the picker's data-section / data-category keys.
            var key = $"pack-{name.ToLowerInvariant()}";
            AddPack(key, CapitalizeFirst(name), ScanFolder(dir, $"/emoji-packs/{name}", key, isBuiltIn: true));
        }
    }

    /// <summary>
    /// Registers every valid image in <paramref name="folder"/> under its filename-derived shortcode
    /// and returns the ones it actually claimed (i.e. minus anything a higher-priority source owns).
    /// </summary>
    private List<CustomEmoji> ScanFolder(string folder, string urlBase, string pack, bool isBuiltIn)
    {
        var claimed = new List<CustomEmoji>();
        var keywords = LoadKeywords(folder);

        // Explicit sort: Directory.GetFiles order is filesystem-dependent (ext4 returns hash order),
        // and the picker's layout shouldn't differ between a dev box and the server.
        foreach (var file in Directory.GetFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(file)))
                continue;

            var filename = Path.GetFileName(file);
            var name = Path.GetFileNameWithoutExtension(file);
            if (!ValidNameRegex().IsMatch(name))
            {
                _logger.LogWarning("Skipping emoji with invalid filename: {File}", filename);
                continue;
            }

            var shortcode = name.ToLowerInvariant();
            // Consume the keywords entry *before* the shadow check: the key is valid whenever this
            // folder holds the image, even if a higher-priority pack owns the shortcode. Whatever
            // is left in the map after the loop names an emoji with no file, i.e. a typo.
            keywords.Remove(shortcode, out var kw);
            if (_emojis.TryGetValue(shortcode, out var owner))
            {
                if (owner.Pack == pack)
                    _logger.LogWarning("Duplicate emoji shortcode ':{Shortcode}:' in '{Pack}', skipping {File}",
                        shortcode, pack, filename);
                else
                    _logger.LogInformation("Emoji ':{Shortcode}:' from pack '{Pack}' is overridden by '{Owner}'",
                        shortcode, pack, owner.Pack);
                continue;
            }

            var emoji = new CustomEmoji
            {
                Shortcode = shortcode,
                Filename = filename,
                Url = $"{urlBase}/{filename}",
                Pack = pack,
                IsBuiltIn = isBuiltIn,
                Keywords = string.IsNullOrEmpty(kw) ? shortcode : $"{shortcode} {kw}"
            };

            _emojis[shortcode] = emoji;
            claimed.Add(emoji);
        }

        foreach (var key in keywords.Keys)
            _logger.LogWarning("keywords.json in '{Pack}' names ':{Shortcode}:', which has no image file",
                pack, key);

        return claimed;
    }

    private static readonly JsonSerializerOptions KeywordJsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Reads the optional <c>keywords.json</c> of one emoji folder: a flat map of shortcode to one
    /// string of search terms.
    /// <code>
    /// { "blobwave": "hello hi greeting", "blobthink": "hmm ponder" }
    /// </code>
    /// Values come back lowercased with normalized whitespace, ready to append to the picker's
    /// <c>data-kw</c> corpus. A missing file is the normal case. A broken one (bad JSON, a value
    /// that is not a string) is logged and the whole file is ignored, because losing search terms
    /// must never stop the app from starting.
    /// </summary>
    private Dictionary<string, string> LoadKeywords(string folder)
    {
        var path = Path.Combine(folder, "keywords.json");
        if (!File.Exists(path))
            return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), KeywordJsonOptions)
                      ?? new();
            // Re-split on whitespace: hand-written values carry stray double spaces, and the search
            // compares a raw substring, so "hi greeting" must not miss "hi  greeting".
            return raw.ToDictionary(
                kv => kv.Key,
                kv => string.Join(' ', kv.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Path} — those emoji stay searchable by shortcode only", path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // An empty pack has no tab icon and nothing to scroll to, so it never reaches the picker.
    private void AddPack(string key, string displayName, List<CustomEmoji> emojis)
    {
        if (emojis.Count > 0)
            _packs.Add(new EmojiPack(key, displayName, emojis));
    }

    public IReadOnlyCollection<CustomEmoji> GetAll() => _emojis.Values;

    public CustomEmoji? GetByShortcode(string shortcode) =>
        _emojis.GetValueOrDefault(shortcode);

    public bool IsCustomEmoji(string shortcode) =>
        _emojis.ContainsKey(shortcode);

    private static string CapitalizeFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

/// <summary>
/// One picker group — the server's customs or a single built-in pack.
/// </summary>
/// <param name="Key">Stable id for the picker's <c>data-section</c> / <c>data-category</c> attributes.</param>
/// <param name="DisplayName">Section header + tab tooltip.</param>
/// <param name="Emojis">Never empty; the first entry doubles as the sidebar tab icon.</param>
public record EmojiPack(string Key, string DisplayName, IReadOnlyList<CustomEmoji> Emojis);

public class CustomEmoji
{
    public required string Shortcode { get; init; }
    public required string Filename { get; init; }
    public required string Url { get; init; }
    /// <summary>Owning pack's <see cref="EmojiPack.Key"/>.</summary>
    public required string Pack { get; init; }
    public required bool IsBuiltIn { get; init; }
    /// <summary>
    /// The picker's full search corpus for this emoji: the shortcode, followed by any terms the
    /// folder's keywords.json adds. Lowercase, space-separated, built once at scan time so the
    /// picker renders it straight into <c>data-kw</c>.
    /// </summary>
    public required string Keywords { get; init; }
}
