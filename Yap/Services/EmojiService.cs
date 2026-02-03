using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Yap.Services;

public partial class EmojiService
{
    private readonly CustomEmojiService _customEmojiService;

    public EmojiService(CustomEmojiService customEmojiService)
    {
        _customEmojiService = customEmojiService;
    }

    // More precise emoji regex - common emojis only
    [GeneratedRegex(
        @"(?:[\u2700-\u27bf]|(?:\ud83c[\udde6-\uddff]){2}|[\ud800-\udbff][\udc00-\udfff]|[\u0023-\u0039]\ufe0f?\u20e3|\u3299|\u3297|\u303d|\u3030|\u24c2|\ud83c[\udd70-\udd71]|\ud83c[\udd7e-\udd7f]|\ud83c\udd8e|\ud83c[\udd91-\udd9a]|\ud83c[\udde6-\uddff]|\ud83c[\ude01-\ude02]|\ud83c\ude1a|\ud83c\ude2f|\ud83c[\ude32-\ude3a]|\ud83c[\ude50-\ude51]|\u203c|\u2049|[\u25aa-\u25ab]|\u25b6|\u25c0|[\u25fb-\u25fe]|\u00a9|\u00ae|\u2122|\u2139|\ud83c\udc04|[\u2600-\u26FF]|\u2b05|\u2b06|\u2b07|\u2b1b|\u2b1c|\u2b50|\u2b55|\u231a|\u231b|\u2328|\u23cf|[\u23e9-\u23f3]|[\u23f8-\u23fa]|\ud83c\udccf|\u2934|\u2935|[\u2190-\u21ff])")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@":([a-zA-Z0-9_-]+):")]
    private static partial Regex CustomEmojiShortcodeRegex();

    /// <summary>
    /// Converts all Unicode emoji characters in the specified text to Twemoji SVG image tags, preserving the original
    /// text for non-emoji content.
    /// </summary>
    /// <remarks>The rendered emoji images use the Twemoji CDN and are styled according to the specified
    /// parameters. If the text consists only of emojis and whitespace, larger emoji images are used for emphasis. This
    /// method is intended for use in Blazor or other environments that support MarkupString rendering.</remarks>
    /// <param name="text">The input text that may contain Unicode emoji characters to be replaced with Twemoji images. Can be null or
    /// empty.</param>
    /// <param name="forceSmall">true to force emojis to render at a smaller size suitable for compact UI elements such as reaction pills;
    /// otherwise, false.</param>
    /// <param name="inline">true to render emojis with inline sizing and alignment, suitable for use within text flows such as display names
    /// or room names; otherwise, false.</param>
    /// <returns>A MarkupString containing the input text with all recognized emoji characters replaced by Twemoji SVG image
    /// tags. If no emojis are present or the input is null or empty, returns the original text as a MarkupString.</returns>
    public MarkupString ConvertEmojisToTwemoji(string text, bool forceSmall = false, bool inline = false)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(text);

        // Check if message contains only emojis (and whitespace)
        var isEmojiOnly = !forceSmall && !inline && IsEmojiOnlyMessage(text);

        var (emojiSize, verticalAlign)
            = (inline, forceSmall, isEmojiOnly) switch
        {
              (true, _, _) => ("1em", "-0.15em"),      // Inline text (display names, room names)
              (_, true, _) => ("18px", "-3px"),         // Reaction pills
              (_, _, true) => ("3em", "-0.4em"),        // Emoji-only messages
               _ => ("1.2em", "-0.2em")                  // Mixed content messages - nomral chat message containing text + emojis
        };

        // Replace custom emoji shortcodes FIRST (before Unicode emoji replacement)
        var result = CustomEmojiShortcodeRegex().Replace(text, match =>
        {
            var shortcode = match.Groups[1].Value;
            var emoji = _customEmojiService.GetByShortcode(shortcode);
            if (emoji == null)
                return match.Value; // Not a known custom emoji, leave as-is

            return $"<img src=\"{emoji.Url}\" alt=\":{emoji.Shortcode}:\" class=\"emoji custom-emoji\" " +
                   $"style=\"width: {emojiSize}; height: {emojiSize}; vertical-align: {verticalAlign}; display: inline-block; object-fit: contain;\" />";
        });

        result = EmojiRegex().Replace(result, match =>
        {
            var emoji = match.Value;
            var codePoint = GetCodePoint(emoji);

            // Skip if we can't get a valid code point
            if (string.IsNullOrEmpty(codePoint) || codePoint == "fffd")
                return emoji;

            return $"<img src=\"https://cdn.jsdelivr.net/gh/jdecked/twemoji@latest/assets/svg/{codePoint}.svg\" " +
                   $"alt=\"{emoji}\" class=\"emoji\" style=\"width: {emojiSize}; height: {emojiSize}; vertical-align: {verticalAlign}; display: inline-block;\" />";
        });

        return new MarkupString(result);
    }

    private bool IsEmojiOnlyMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var trimmed = text.Trim();

        // Strip known custom emoji shortcodes
        var withoutCustom = CustomEmojiShortcodeRegex().Replace(trimmed, match =>
            _customEmojiService.IsCustomEmoji(match.Groups[1].Value) ? "" : match.Value);

        // Replace all Unicode emojis with empty string
        var withoutEmojis = EmojiRegex().Replace(withoutCustom, "");

        // If nothing remains after removing emojis, it's emoji-only
        return string.IsNullOrWhiteSpace(withoutEmojis);
    }

    public MarkupString RenderCustomEmoji(CustomEmoji emoji, string size = "18px")
    {
        return new MarkupString(
            $"<img src=\"{emoji.Url}\" alt=\":{emoji.Shortcode}:\" class=\"emoji custom-emoji\" " +
            $"style=\"width: {size}; height: {size}; vertical-align: -3px; display: inline-block; object-fit: contain;\" />");
    }

    private static string GetCodePoint(string emoji)
    {
        try
        {
            var codePoints = new List<string>();

            for (int i = 0; i < emoji.Length; i++)
            {
                var c = emoji[i];

                // Handle surrogate pairs
                if (char.IsHighSurrogate(c) && i + 1 < emoji.Length)
                {
                    var low = emoji[i + 1];
                    if (char.IsLowSurrogate(low))
                    {
                        var codePoint = 0x10000 + (c - 0xD800) * 0x400 + (low - 0xDC00);
                        codePoints.Add(codePoint.ToString("x"));
                        i++; // Skip the low surrogate
                        continue;
                    }
                }

                // Regular character
                var charCode = (int)c;

                // Skip variation selectors and other modifiers we don't need
                if (charCode == 0xFE0F || charCode == 0x200D)
                    continue;

                codePoints.Add(charCode.ToString("x"));
            }

            return string.Join("-", codePoints);
        }
        catch
        {
            return "";
        }
    }
}
