using System.Text.RegularExpressions;

namespace Yap.Helpers;

public static partial class UserAgent
{
    /// <summary>Whether the User-Agent names a phone or a tablet. Shown to the person and to admins only (PR-25).</summary>
    public static bool IsMobile(string? userAgent) => !string.IsNullOrEmpty(userAgent) && Mobile().IsMatch(userAgent);

    [GeneratedRegex(@"iPhone|iPad|iPod|Android|webOS|BlackBerry|Opera Mini|Opera Mobi|IEMobile|Windows Phone|Mobile Safari|Silk/|Kindle|PlayBook", RegexOptions.IgnoreCase)]
    private static partial Regex Mobile();

    /// <summary>The browser family, for the load-time table (AD-41). The order matters: every Chromium browser also
    /// says "Chrome" and every WebKit browser says "Safari", so the specific names come first. The iOS spellings are
    /// CriOS, FxiOS and EdgiOS. Unknown is "".</summary>
    public static string Browser(string? ua) => ua switch
    {
        null or "" => "",
        _ when ua.Contains("Edg") => "Edge",
        _ when ua.Contains("OPR/") || ua.Contains("Opera") => "Opera",
        _ when ua.Contains("SamsungBrowser") => "Samsung",
        _ when ua.Contains("Firefox") || ua.Contains("FxiOS") => "Firefox",
        _ when ua.Contains("Chrome") || ua.Contains("CriOS") => "Chrome",
        _ when ua.Contains("Safari") => "Safari",
        _ => "",
    };

    /// <summary>The operating system family (AD-41). An iPad in Safari's desktop mode says "Macintosh" and lands on
    /// macOS, as it does for IsMobile. Unknown is "".</summary>
    public static string Os(string? ua) => ua switch
    {
        null or "" => "",
        _ when ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iPod") => "iOS",
        _ when ua.Contains("Android") => "Android",
        _ when ua.Contains("Windows") => "Windows",
        _ when ua.Contains("CrOS") => "ChromeOS",
        _ when ua.Contains("Macintosh") => "macOS",
        _ when ua.Contains("Linux") => "Linux",
        _ => "",
    };
}
