using System.Collections.Concurrent;
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

    // --- Culture sanitization ---

    private static readonly ConcurrentDictionary<string, CultureInfo> SanitizedCultureCache = new();
    private static readonly string[] WesternDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>
    /// Creates a formatting-safe copy of a culture.
    /// Keeps separators but forces Gregorian calendar and Western digits.
    /// This prevents Hijri dates, Buddhist years, and Eastern Arabic numerals.
    /// </summary>
    public static CultureInfo SanitizeCulture(CultureInfo culture)
    {
        if (culture == CultureInfo.InvariantCulture)
            return culture;

        return SanitizedCultureCache.GetOrAdd(culture.Name, _ =>
        {
            var safe = (CultureInfo)culture.Clone();
            safe.DateTimeFormat.Calendar = new GregorianCalendar();
            safe.NumberFormat.NativeDigits = WesternDigits;
            safe.NumberFormat.DigitSubstitution = DigitShapes.None;
            return CultureInfo.ReadOnly(safe);
        });
    }

    // --- Timezone → local culture mapping ---

    private static readonly Dictionary<string, string> TimezoneToCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        // Europe
        ["Europe/Prague"] = "CZ", ["Europe/Berlin"] = "DE", ["Europe/Paris"] = "FR",
        ["Europe/London"] = "GB", ["Europe/Madrid"] = "ES", ["Europe/Rome"] = "IT",
        ["Europe/Amsterdam"] = "NL", ["Europe/Brussels"] = "BE", ["Europe/Vienna"] = "AT",
        ["Europe/Warsaw"] = "PL", ["Europe/Bucharest"] = "RO", ["Europe/Budapest"] = "HU",
        ["Europe/Moscow"] = "RU", ["Europe/Istanbul"] = "TR", ["Europe/Athens"] = "GR",
        ["Europe/Helsinki"] = "FI", ["Europe/Stockholm"] = "SE", ["Europe/Oslo"] = "NO",
        ["Europe/Copenhagen"] = "DK", ["Europe/Dublin"] = "IE", ["Europe/Lisbon"] = "PT",
        ["Europe/Zurich"] = "CH", ["Europe/Bratislava"] = "SK", ["Europe/Ljubljana"] = "SI",
        ["Europe/Zagreb"] = "HR", ["Europe/Belgrade"] = "RS", ["Europe/Sofia"] = "BG",
        ["Europe/Tallinn"] = "EE", ["Europe/Riga"] = "LV", ["Europe/Vilnius"] = "LT",
        ["Europe/Kiev"] = "UA", ["Europe/Kyiv"] = "UA", ["Europe/Minsk"] = "BY",
        // Asia
        ["Asia/Tokyo"] = "JP", ["Asia/Seoul"] = "KR", ["Asia/Shanghai"] = "CN",
        ["Asia/Hong_Kong"] = "HK", ["Asia/Singapore"] = "SG", ["Asia/Kolkata"] = "IN",
        ["Asia/Calcutta"] = "IN", ["Asia/Bangkok"] = "TH", ["Asia/Dubai"] = "AE",
        ["Asia/Riyadh"] = "SA", ["Asia/Baghdad"] = "IQ", ["Asia/Tehran"] = "IR",
        ["Asia/Jakarta"] = "ID", ["Asia/Manila"] = "PH", ["Asia/Kuala_Lumpur"] = "MY",
        ["Asia/Taipei"] = "TW", ["Asia/Ho_Chi_Minh"] = "VN", ["Asia/Karachi"] = "PK",
        // Americas
        ["America/New_York"] = "US", ["America/Chicago"] = "US", ["America/Denver"] = "US",
        ["America/Los_Angeles"] = "US", ["America/Phoenix"] = "US", ["America/Anchorage"] = "US",
        ["America/Toronto"] = "CA", ["America/Vancouver"] = "CA", ["America/Edmonton"] = "CA",
        ["America/Mexico_City"] = "MX", ["America/Cancun"] = "MX", ["America/Tijuana"] = "MX",
        ["America/Sao_Paulo"] = "BR", ["America/Manaus"] = "BR",
        ["America/Buenos_Aires"] = "AR", ["America/Argentina/Buenos_Aires"] = "AR",
        ["America/Lima"] = "PE", ["America/Bogota"] = "CO", ["America/Santiago"] = "CL",
        ["America/Caracas"] = "VE", ["America/Guatemala"] = "GT", ["America/Costa_Rica"] = "CR",
        ["America/Panama"] = "PA", ["America/Havana"] = "CU", ["America/Santo_Domingo"] = "DO",
        ["America/Guayaquil"] = "EC", ["America/La_Paz"] = "BO", ["America/Asuncion"] = "PY",
        ["America/Montevideo"] = "UY", ["America/El_Salvador"] = "SV",
        ["America/Tegucigalpa"] = "HN", ["America/Managua"] = "NI",
        // Oceania / Africa
        ["Australia/Sydney"] = "AU", ["Australia/Melbourne"] = "AU", ["Australia/Perth"] = "AU",
        ["Australia/Brisbane"] = "AU", ["Pacific/Auckland"] = "NZ",
        ["Africa/Cairo"] = "EG", ["Africa/Johannesburg"] = "ZA", ["Africa/Lagos"] = "NG",
        ["Africa/Nairobi"] = "KE", ["Africa/Casablanca"] = "MA", ["Africa/Tunis"] = "TN",
        // Middle East
        ["Asia/Jerusalem"] = "IL", ["Asia/Beirut"] = "LB", ["Asia/Amman"] = "JO",
    };

    // Cache: country code → primary CultureInfo
    private static readonly ConcurrentDictionary<string, CultureInfo?> CountryCultureCache = new();

    /// <summary>
    /// Gets the primary culture for a country code using .NET's culture database.
    /// </summary>
    private static CultureInfo? GetCultureForCountry(string countryCode)
    {
        return CountryCultureCache.GetOrAdd(countryCode, code =>
        {
            try
            {
                return CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                    .FirstOrDefault(c =>
                    {
                        try { return new RegionInfo(c.Name).TwoLetterISORegionName == code; }
                        catch { return false; }
                    });
            }
            catch { return null; }
        });
    }

    /// <summary>
    /// Guesses the local culture from a timezone ID.
    /// E.g. "Europe/Prague" → cs-CZ, "Asia/Tokyo" → ja-JP.
    /// Returns null if timezone is unknown or not in the map.
    /// </summary>
    public static CultureInfo? GuessCultureFromTimezone(string? timezone)
    {
        if (timezone == null || !TimezoneToCountry.TryGetValue(timezone, out var countryCode))
            return null;
        return GetCultureForCountry(countryCode);
    }

    /// <summary>
    /// Returns the available formatting cultures for a user (sanitized: Gregorian + Western digits).
    /// Always includes the browser locale; adds the timezone's local culture if different.
    /// </summary>
    public static List<CultureInfo> GetAvailableCultures(string? browserLocale, string? timezone)
    {
        var result = new List<CultureInfo>();

        // Browser locale (always first)
        CultureInfo? browserCulture = null;
        if (!string.IsNullOrEmpty(browserLocale))
        {
            try { browserCulture = SanitizeCulture(new CultureInfo(browserLocale)); }
            catch { }
        }
        browserCulture ??= CultureInfo.InvariantCulture;
        result.Add(browserCulture);

        // Timezone-derived culture (if different)
        var tzCulture = GuessCultureFromTimezone(timezone);
        if (tzCulture != null && !tzCulture.Name.Equals(browserCulture.Name, StringComparison.OrdinalIgnoreCase))
        {
            result.Add(SanitizeCulture(tzCulture));
        }

        return result;
    }

    // --- Date format: two independent choices (date order + clock style) + optional culture override ---

    public record DateOrderOption(string Id, string DateInYear, string FullDate);
    public record ClockOption(string Id, string TimeFormat);

    public static readonly DateOrderOption[] DateOrders =
    [
        new("dmy", "dd/MM", "dd/MM/yyyy"),
        new("mdy", "M/d", "M/d/yyyy"),
        new("ymd", "MM-dd", "yyyy-MM-dd"),
    ];

    public static readonly ClockOption[] ClockStyles =
    [
        new("24h", "HH:mm"),
        new("12h", "h:mm tt"),
    ];

    /// <summary>
    /// Builds a DateTimeFormat from the two independent choices.
    /// </summary>
    public static DateTimeFormat BuildFormat(string? dateOrder, string? clock)
    {
        var order = DateOrders.FirstOrDefault(o => o.Id == dateOrder) ?? DateOrders[0];
        var clk = ClockStyles.FirstOrDefault(c => c.Id == clock) ?? ClockStyles[0];
        return new DateTimeFormat(order.DateInYear, order.FullDate, clk.TimeFormat);
    }

    /// <summary>
    /// Parses stored format string into components.
    /// Supports "dmy-24h" (no culture override) and "dmy-24h-cs-CZ" (with override).
    /// </summary>
    public static (string DateOrder, string Clock, string? CultureOverride) ParseDateFormat(string? stored)
    {
        if (stored != null)
        {
            var parts = stored.Split('-', 3);
            if (parts.Length >= 2
                && DateOrders.Any(o => o.Id == parts[0])
                && ClockStyles.Any(c => c.Id == parts[1]))
            {
                var culture = parts.Length >= 3 ? parts[2] : null;
                return (parts[0], parts[1], culture);
            }
        }
        return ("dmy", "24h", null);
    }

    /// <summary>
    /// Gets a DateTimeFormat from a stored format string.
    /// </summary>
    public static DateTimeFormat GetFormat(string? stored)
    {
        var (order, clock, _) = ParseDateFormat(stored);
        return BuildFormat(order, clock);
    }

    /// <summary>
    /// Gets the culture override from a stored format string, if any.
    /// </summary>
    public static string? GetCultureOverride(string? stored)
    {
        var (_, _, culture) = ParseDateFormat(stored);
        return culture;
    }

    /// <summary>
    /// Guesses date order from browser locale using .NET's built-in culture data.
    /// Reads ShortDatePattern to determine if culture uses DMY, MDY, or YMD.
    /// </summary>
    public static string GuessDateOrderFromLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return "dmy";
        try
        {
            var culture = new CultureInfo(locale);
            var pattern = culture.DateTimeFormat.ShortDatePattern;
            if (pattern.StartsWith('y') || pattern.StartsWith('Y')) return "ymd";
            if (pattern.StartsWith('M')) return "mdy";
            return "dmy";
        }
        catch { return "dmy"; }
    }

    /// <summary>
    /// Guesses clock style from browser locale using .NET's built-in culture data.
    /// Reads ShortTimePattern to determine if culture uses 12h (AM/PM) or 24h.
    /// </summary>
    public static string GuessClockFromLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale)) return "24h";
        try
        {
            var culture = new CultureInfo(locale);
            return culture.DateTimeFormat.ShortTimePattern.Contains("tt") ? "12h" : "24h";
        }
        catch { return "24h"; }
    }

    /// <summary>
    /// Guesses combined format string from browser locale.
    /// </summary>
    public static string GuessDateFormatFromLocale(string? locale) =>
        $"{GuessDateOrderFromLocale(locale)}-{GuessClockFromLocale(locale)}";

    /// <summary>
    /// Returns date orders with best guess first.
    /// </summary>
    public static DateOrderOption[] GetOrderedDateOrders(string? locale)
    {
        var guessed = GuessDateOrderFromLocale(locale);
        return DateOrders.OrderByDescending(o => o.Id == guessed).ToArray();
    }

    /// <summary>
    /// Returns clock styles with best guess first.
    /// </summary>
    public static ClockOption[] GetOrderedClockStyles(string? locale)
    {
        var guessed = GuessClockFromLocale(locale);
        return ClockStyles.OrderByDescending(c => c.Id == guessed).ToArray();
    }
}

/// <summary>
/// Combined date+time format built from independent date order and clock choices.
/// Format strings use / and : as culture-aware placeholders.
/// </summary>
public record DateTimeFormat(string DateInYear, string FullDate, string Time)
{
    public string FormatExample(DateTimeOffset time, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.InvariantCulture;
        return $"{time.ToString(DateInYear, c)} {time.ToString(Time, c)}";
    }
}
