using System.Globalization;
using System.Text.RegularExpressions;

namespace Yap.Services;

/// <summary>
/// Resolves user-friendly timezone strings and manages date format presets.
/// Accepts IANA IDs, abbreviations (EST, CET), and UTC/GMT offsets (UTC+1, GMT-5:30).
/// </summary>
public static class LocaleResolver
{
    private static readonly Dictionary<string, string> TimeZoneAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // North America
        ["EST"] = "America/New_York",
        ["EDT"] = "America/New_York",
        ["CST"] = "America/Chicago",
        ["CDT"] = "America/Chicago",
        ["MST"] = "America/Denver",
        ["MDT"] = "America/Denver",
        ["PST"] = "America/Los_Angeles",
        ["PDT"] = "America/Los_Angeles",
        ["HST"] = "Pacific/Honolulu",
        // Europe
        ["GMT"] = "Etc/GMT",
        ["UTC"] = "Etc/UTC",
        ["WET"] = "Europe/Lisbon",
        ["WEST"] = "Europe/Lisbon",
        ["CET"] = "Europe/Berlin",
        ["CEST"] = "Europe/Berlin",
        ["EET"] = "Europe/Bucharest",
        ["EEST"] = "Europe/Bucharest",
        ["BST"] = "Europe/London",
        ["IST"] = "Asia/Kolkata",
        ["MSK"] = "Europe/Moscow",
        // Asia / Pacific
        ["JST"] = "Asia/Tokyo",
        ["KST"] = "Asia/Seoul",
        ["HKT"] = "Asia/Hong_Kong",
        ["SGT"] = "Asia/Singapore",
        ["AEST"] = "Australia/Sydney",
        ["AEDT"] = "Australia/Sydney",
        ["NZST"] = "Pacific/Auckland",
        ["NZDT"] = "Pacific/Auckland",
    };

    private static readonly Regex OffsetRegex = new(
        @"^(?:UTC|GMT)\s*([+-])\s*(\d{1,2})(?::(\d{2}))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Resolves a timezone string to TimeZoneInfo.
    /// Tries: exact IANA ID → abbreviation → UTC/GMT offset.
    /// Returns null if unresolvable.
    /// </summary>
    public static TimeZoneInfo? ResolveTimeZone(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();

        // 1. Try exact IANA ID
        try { return TimeZoneInfo.FindSystemTimeZoneById(trimmed); }
        catch { }

        // 2. Try abbreviation
        if (TimeZoneAbbreviations.TryGetValue(trimmed, out var ianaId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
            catch { }
        }

        // 3. Try UTC/GMT offset (e.g. "UTC+1", "GMT-5:30")
        var match = OffsetRegex.Match(trimmed);
        if (match.Success)
        {
            var sign = match.Groups[1].Value == "+" ? 1 : -1;
            var hours = int.Parse(match.Groups[2].Value);
            var minutes = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            var offset = new TimeSpan(sign * hours, sign * minutes, 0);
            if (offset.TotalHours is >= -14 and <= 14)
                return TimeZoneInfo.CreateCustomTimeZone(trimmed, offset, trimmed, trimmed);
        }

        return null;
    }

    /// <summary>
    /// Formats a UTC offset as a user-friendly string like "UTC+1" or "UTC-5:30".
    /// </summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"UTC{sign}{abs.Hours}"
            : $"UTC{sign}{abs.Hours}:{abs.Minutes:D2}";
    }

    // --- Date format presets ---

    public static readonly DateFormatPreset[] Presets =
    [
        new("czech", "Czech", "d. M.", "d. M. yyyy", "H:mm"),
        new("us", "US English", "M/d", "M/d/yyyy", "h:mm tt"),
        new("european", "European", "dd/MM", "dd/MM/yyyy", "HH:mm"),
        new("iso", "ISO", "MM-dd", "yyyy-MM-dd", "HH:mm"),
    ];

    public static DateFormatPreset GetPreset(string? id) =>
        Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? Presets[2]; // default to European

    /// <summary>
    /// Guesses a date format preset from a browser locale string.
    /// </summary>
    public static string GuessPresetFromLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return "european";
        var lower = locale.ToLowerInvariant();
        if (lower.StartsWith("cs") || lower.StartsWith("sk")) return "czech";
        if (lower == "en-us") return "us";
        if (lower.StartsWith("ja") || lower.StartsWith("ko") || lower.StartsWith("zh")) return "iso";
        return "european";
    }
}

public record DateFormatPreset(string Id, string Label, string DateInYear, string FullDate, string Time)
{
    /// <summary>
    /// Formats a sample timestamp for the preset picker.
    /// </summary>
    public string FormatExample(DateTimeOffset time) =>
        $"{time.ToString(DateInYear, CultureInfo.InvariantCulture)} {time.ToString(Time, CultureInfo.InvariantCulture)}";
}
