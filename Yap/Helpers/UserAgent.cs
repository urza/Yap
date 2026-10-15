using System.Text.RegularExpressions;

namespace Yap.Helpers;

public static partial class UserAgent
{
    /// <summary>Whether the User-Agent names a phone or a tablet. Shown to the person and to admins only (PR-25).</summary>
    public static bool IsMobile(string? userAgent) => !string.IsNullOrEmpty(userAgent) && Mobile().IsMatch(userAgent);

    [GeneratedRegex(@"iPhone|iPad|iPod|Android|webOS|BlackBerry|Opera Mini|Opera Mobi|IEMobile|Windows Phone|Mobile Safari|Silk/|Kindle|PlayBook", RegexOptions.IgnoreCase)]
    private static partial Regex Mobile();
}
