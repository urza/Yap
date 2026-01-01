using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Yap.Services;

public partial class EmojiService
{
    // More precise emoji regex - common emojis only
    [GeneratedRegex(
        @"(?:[\u2700-\u27bf]|(?:\ud83c[\udde6-\uddff]){2}|[\ud800-\udbff][\udc00-\udfff]|[\u0023-\u0039]\ufe0f?\u20e3|\u3299|\u3297|\u303d|\u3030|\u24c2|\ud83c[\udd70-\udd71]|\ud83c[\udd7e-\udd7f]|\ud83c\udd8e|\ud83c[\udd91-\udd9a]|\ud83c[\udde6-\uddff]|\ud83c[\ude01-\ude02]|\ud83c\ude1a|\ud83c\ude2f|\ud83c[\ude32-\ude3a]|\ud83c[\ude50-\ude51]|\u203c|\u2049|[\u25aa-\u25ab]|\u25b6|\u25c0|[\u25fb-\u25fe]|\u00a9|\u00ae|\u2122|\u2139|\ud83c\udc04|[\u2600-\u26FF]|\u2b05|\u2b06|\u2b07|\u2b1b|\u2b1c|\u2b50|\u2b55|\u231a|\u231b|\u2328|\u23cf|[\u23e9-\u23f3]|[\u23f8-\u23fa]|\ud83c\udccf|\u2934|\u2935|[\u2190-\u21ff])")]
    private static partial Regex EmojiRegex();

    public MarkupString ConvertEmojisToTwemoji(string text, bool forceSmall = false)
    {
        if (string.IsNullOrEmpty(text))
            return new MarkupString(text);

        // Check if message contains only emojis (and whitespace)
        var isEmojiOnly = !forceSmall && IsEmojiOnlyMessage(text);
        var emojiSize = forceSmall ? "18px" : (isEmojiOnly ? "3em" : "1.2em");
        var verticalAlign = forceSmall ? "-3px" : (isEmojiOnly ? "-0.4em" : "-0.2em");

        var result = EmojiRegex().Replace(text, match =>
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

    private static bool IsEmojiOnlyMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Remove all whitespace
        var trimmed = text.Trim();

        // Replace all emojis with empty string
        var withoutEmojis = EmojiRegex().Replace(trimmed, "");

        // If nothing remains after removing emojis, it's emoji-only
        return string.IsNullOrWhiteSpace(withoutEmojis);
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
