namespace Yap.Helpers;

public static class IpHelper
{
    /// <summary>
    /// Gets the client IP address from the HTTP context.
    /// Priority: CF-Connecting-IP (Cloudflare) → X-Forwarded-For → RemoteIpAddress.
    /// Returns null when IP cannot be determined (prevents false matches in smart login).
    /// </summary>
    public static string? GetClientIp(HttpContext context)
    {
        // Cloudflare sets this header and overwrites any client-sent value
        var cfIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(cfIp))
            return cfIp.Trim();

        // Fallback: X-Forwarded-For (first entry — can be spoofed without trusted proxy)
        var xff = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff))
        {
            var first = xff.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString();

        // Normalize IPv6 loopback to IPv4 (localhost: some browsers use ::1, others 127.0.0.1)
        if (ip == "::1") ip = "127.0.0.1";

        return ip;
    }
}
