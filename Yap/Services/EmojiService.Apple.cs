using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Yap.Services;

/// <summary>
/// Apple-style emoji rendering (emoji-datasource-apple PNGs), added alongside the Twemoji
/// methods in <c>EmojiService.cs</c>. The Twemoji methods are kept intact as a backup; THESE
/// methods are the ones wired into the UI. To revert to Twemoji, point the call sites back at
/// <c>ConvertEmojisToTwemoji</c> / <c>ProcessMessageContent</c> / <c>GetPickerEmojiHtml</c>
/// (and this file + the embedded emoji.json can be deleted).
///
/// Resolution order for a rendered emoji:
///   1. self-hosted override  (wwwroot/emoji-fallback/{codepoint}.png) - for emoji missing or
///      outdated in emoji-datasource@16 (e.g. new Unicode releases). Drop a file named by its
///      codepoint and it is picked up automatically (folder scanned once).
///   2. emoji-datasource-apple CDN (the primary source for everything else).
///   3. Twemoji CDN (final <c>onerror</c> fallback for anything emoji-datasource is missing).
///
/// Filename convention (emoji-datasource, verified against the CDN): the fully-qualified
/// <c>unified</c> codepoint, lowercased - FE0F kept, each codepoint zero-padded to >=4 digits
/// (e.g. heart = 2764-fe0f, hash-keycap = 0023-fe0f-20e3, grinning = 1f600). Because EmojiData.cs
/// stores emoji bare (no FE0F), bare legacy BMP symbols + keycaps are resolved via
/// <see cref="AppleMap"/> (built from emoji.json); astral, skin-tone and already-qualified input
/// fall through to a direct computation.
/// </summary>
public partial class EmojiService
{
    private const char VariationSelector = (char)0xFE0F; // U+FE0F

    // 64px is the largest individual image size emoji-datasource ships.
    private const string AppleCdnBase = "https://cdn.jsdelivr.net/npm/emoji-datasource-apple@16.0.0/img/apple/64";
    private static string AppleImgUrl(string codePoint) => $"{AppleCdnBase}/{codePoint}.png";

    // Self-hosted overrides served from wwwroot/emoji-fallback/, and the Twemoji CDN used as the
    // final fallback for anything emoji-datasource is missing.
    private const string LocalFallbackUrlBase = "/emoji-fallback";
    private const string TwemojiCdnBase = "https://cdn.jsdelivr.net/gh/jdecked/twemoji@latest/assets/svg";

    // Memoized <img> markup for picker cells (lazy twin of the Twemoji _pickerEmojiCache).
    private readonly ConcurrentDictionary<string, MarkupString> _appleCache = new();

    // bare (FE0F-stripped) codepoint key -> fully-qualified apple filename. Lazily loaded.
    private Dictionary<string, string>? _appleMap;
    private readonly object _appleMapLock = new();

    // Codepoints that have a self-hosted PNG in wwwroot/emoji-fallback/. Scanned once, lazily.
    private HashSet<string>? _localOverrides;
    private readonly object _localOverridesLock = new();

    /// <summary>Twin of <see cref="GetPickerEmojiHtml"/> for the emoji picker grid/sidebar.</summary>
    /// <remarks>No Twemoji <c>onerror</c> fallback: every picker emoji comes from the curated
    /// EmojiData set and is present in emoji-datasource, so the fallback would only bloat ~1400
    /// cached cells.</remarks>
    public MarkupString GetAppleEmojiHtml(string emoji)
        => _appleCache.GetOrAdd(emoji, e => ConvertEmojisToApple(e, forceSmall: true, withFallback: false));

    /// <summary>
    /// Twin of <see cref="ConvertEmojisToTwemoji"/> that emits Apple-style emoji-datasource PNGs.
    /// Custom <c>:shortcode:</c> emoji and all sizing behaviour are identical to the Twemoji path.
    /// When <paramref name="withFallback"/> is true, each Unicode-emoji image gets an
    /// <c>onerror</c> handler that falls back to the Twemoji CDN.
    /// </summary>
    public MarkupString ConvertEmojisToApple(string text, bool forceSmall = false, bool inline = false, bool withFallback = true)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(text);

        var isEmojiOnly = !forceSmall && !inline && IsEmojiOnlyMessage(text);

        var (emojiSize, verticalAlign)
            = (inline, forceSmall, isEmojiOnly) switch
            {
                (true, _, _) => ("1em", "-0.15em"),     // Inline text (display names, room names)
                (_, true, _) => ("18px", "-3px"),        // Reaction pills
                (_, _, true) => ("3em", "-0.4em"),       // Emoji-only messages
                _ => ("1.2em", "-0.2em")                 // Mixed content messages
            };

        // Replace custom emoji shortcodes FIRST (local images - identical to the Twemoji path).
        var result = CustomEmojiShortcodeRegex().Replace(text, match =>
        {
            var shortcode = match.Groups[1].Value;
            var emoji = _customEmojiService.GetByShortcode(shortcode);
            if (emoji == null)
                return match.Value;

            return $"<img src=\"{emoji.Url}\" alt=\":{emoji.Shortcode}:\" class=\"emoji custom-emoji\" " +
                   $"style=\"width: {emojiSize}; height: {emojiSize}; vertical-align: {verticalAlign}; display: inline-block; object-fit: contain;\" />";
        });

        // Replace Unicode emojis, merging ZWJ sequences / skin tones into single images.
        var emojiMatches = EmojiRegex().Matches(result);
        if (emojiMatches.Count > 0)
        {
            var sb = new StringBuilder(result.Length + emojiMatches.Count * 100);
            var lastEnd = 0;
            var i = 0;

            while (i < emojiMatches.Count)
            {
                var seqStart = emojiMatches[i].Index;
                var seqEnd = seqStart + emojiMatches[i].Length;

                var next = i + 1;
                while (next < emojiMatches.Count)
                {
                    var gapStart = seqEnd;
                    var gapLength = emojiMatches[next].Index - gapStart;
                    if (ShouldMergeEmoji(result, gapStart, gapLength, emojiMatches[next].Index))
                    {
                        seqEnd = emojiMatches[next].Index + emojiMatches[next].Length;
                        next++;
                    }
                    else break;
                }

                // Consume trailing variation selectors (FE0F) that follow the sequence.
                while (seqEnd < result.Length && result[seqEnd] == VariationSelector)
                    seqEnd++;

                sb.Append(result, lastEnd, seqStart - lastEnd);

                var emoji = result.Substring(seqStart, seqEnd - seqStart);
                var codePoint = ResolveAppleCodePoint(emoji);

                if (string.IsNullOrEmpty(codePoint) || codePoint == "fffd")
                    sb.Append(emoji);
                else
                    sb.Append(BuildAppleImg(emoji, codePoint, emojiSize, verticalAlign, withFallback));

                lastEnd = seqEnd;
                i = next;
            }

            sb.Append(result, lastEnd, result.Length - lastEnd);
            result = sb.ToString();
        }

        return new MarkupString(result);
    }

    /// <summary>Twin of <see cref="ProcessMessageContent"/> using the Apple emoji path.</summary>
    public MarkupString ProcessMessageContentApple(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(text);

        var urls = LinkPreviewService.ExtractUrls(text);
        if (urls.Count == 0)
            return ConvertEmojisToApple(text);

        var sb = new StringBuilder();
        var remaining = text;

        foreach (var url in urls)
        {
            var searchUrl = url;
            var idx = remaining.IndexOf(searchUrl, StringComparison.Ordinal);

            if (idx < 0 && searchUrl.StartsWith("https://"))
            {
                searchUrl = searchUrl["https://".Length..];
                idx = remaining.IndexOf(searchUrl, StringComparison.Ordinal);
            }

            if (idx < 0) continue;

            if (idx > 0)
            {
                var before = remaining[..idx];
                sb.Append(ConvertEmojisToApple(before).Value);
            }

            var encodedUrl = WebUtility.HtmlEncode(url);
            var encodedDisplay = WebUtility.HtmlEncode(searchUrl);
            sb.Append($"<a href=\"{encodedUrl}\" target=\"_blank\" rel=\"noopener noreferrer\" class=\"message-link\">{encodedDisplay}</a>");

            remaining = remaining[(idx + searchUrl.Length)..];
        }

        if (remaining.Length > 0)
        {
            sb.Append(ConvertEmojisToApple(remaining).Value);
        }

        return new MarkupString(sb.ToString());
    }

    /// <summary>
    /// Builds an emoji <c>&lt;img&gt;</c>: self-hosted override if present, otherwise the
    /// emoji-datasource CDN, with an optional <c>onerror</c> fallback to the Twemoji CDN.
    /// </summary>
    private string BuildAppleImg(string emoji, string codePoint, string size, string verticalAlign, bool withFallback)
    {
        var src = LocalOverrides.Contains(codePoint)
            ? $"{LocalFallbackUrlBase}/{codePoint}.png"
            : AppleImgUrl(codePoint);

        var onError = "";
        if (withFallback)
        {
            // Twemoji uses its own naming (FE0F stripped, minimal width) - reuse the backup method.
            var twemojiCp = GetCodePoint(emoji);
            if (!string.IsNullOrEmpty(twemojiCp) && twemojiCp != "fffd")
                onError = $" onerror=\"this.onerror=null;this.src='{TwemojiCdnBase}/{twemojiCp}.svg'\"";
        }

        return $"<img src=\"{src}\"{onError} alt=\"{emoji}\" class=\"emoji\" " +
               $"style=\"width: {size}; height: {size}; vertical-align: {verticalAlign}; display: inline-block;\" />";
    }

    /// <summary>
    /// Resolves an emoji sequence to the emoji-datasource-apple filename (no extension).
    /// Bare/legacy/keycap emoji hit the qualified-name map; astral, skin-tone and already-qualified
    /// input fall through to a direct keep-FE0F + pad-to-4 computation.
    /// </summary>
    private string ResolveAppleCodePoint(string emoji)
    {
        try
        {
            var bareKey = NormalizeCodePoints(emoji, keepFe0f: false);
            if (AppleMap.TryGetValue(bareKey, out var qualified))
                return qualified;

            return NormalizeCodePoints(emoji, keepFe0f: true);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Hyphen-joined lowercase hex codepoints, each padded to a minimum of 4 digits
    /// (emoji-datasource convention). FE0F is optionally dropped.
    /// </summary>
    private static string NormalizeCodePoints(string emoji, bool keepFe0f)
    {
        var parts = new List<string>();
        for (int i = 0; i < emoji.Length; i++)
        {
            var c = emoji[i];

            if (char.IsHighSurrogate(c) && i + 1 < emoji.Length && char.IsLowSurrogate(emoji[i + 1]))
            {
                var cp = 0x10000 + (c - 0xD800) * 0x400 + (emoji[i + 1] - 0xDC00);
                parts.Add(cp.ToString("x4"));
                i++;
                continue;
            }

            if (!keepFe0f && c == VariationSelector)
                continue;

            parts.Add(((int)c).ToString("x4"));
        }

        return string.Join("-", parts);
    }

    private Dictionary<string, string> AppleMap
    {
        get
        {
            if (_appleMap != null)
                return _appleMap;

            lock (_appleMapLock)
            {
                _appleMap ??= LoadAppleMap();
            }

            return _appleMap;
        }
    }

    private HashSet<string> LocalOverrides
    {
        get
        {
            if (_localOverrides != null)
                return _localOverrides;

            lock (_localOverridesLock)
            {
                _localOverrides ??= ScanLocalOverrides();
            }

            return _localOverrides;
        }
    }

    /// <summary>
    /// Scans wwwroot/emoji-fallback/ for self-hosted override PNGs, keyed by the codepoint
    /// (the filename without extension, e.g. <c>1faea.png</c> -&gt; <c>1faea</c>).
    /// </summary>
    private static HashSet<string> ScanLocalOverrides()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "emoji-fallback");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.png"))
                    set.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
            }
        }
        catch
        {
            // Best-effort: without overrides, emoji fall back to emoji-datasource / Twemoji.
        }

        return set;
    }

    /// <summary>
    /// Builds the bare->qualified filename map from the embedded emoji.json. Only entries whose
    /// fully-qualified form contains FE0F need mapping (everything else matches the bare
    /// computation), so the map stays small.
    /// </summary>
    private static Dictionary<string, string> LoadAppleMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var asm = typeof(EmojiService).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("emoji.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
                return map;

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
                return map;

            using var doc = JsonDocument.Parse(stream);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("unified", out var unifiedEl))
                    continue;

                var unified = unifiedEl.GetString();
                if (string.IsNullOrEmpty(unified)
                    || unified.IndexOf("FE0F", StringComparison.OrdinalIgnoreCase) < 0)
                    continue; // bare form already resolves correctly

                // Skip emoji with no Apple artwork (would 404 either way).
                if (el.TryGetProperty("has_img_apple", out var hasApple) && hasApple.ValueKind == JsonValueKind.False)
                    continue;

                var filename = unified.ToLowerInvariant();                              // e.g. "2764-fe0f"
                var key = string.Join("-", filename.Split('-').Where(p => p != "fe0f")); // e.g. "2764"
                map[key] = filename;
            }
        }
        catch
        {
            // Best-effort: the fallback computation still handles astral/qualified input.
        }

        return map;
    }
}
