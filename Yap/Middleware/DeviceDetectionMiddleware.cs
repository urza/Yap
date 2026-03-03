using System.Text.RegularExpressions;

namespace Yap.Middleware;

/// <summary>
/// Detects if the client is using a mobile device based on User-Agent.
/// Sets HttpContext.Items["IsMobile"] for downstream components to read.
/// </summary>
public class DeviceDetectionMiddleware
{
    private readonly RequestDelegate _next;

    // Regex pattern matching common mobile device indicators
    private static readonly Regex MobileRegex = new(
        @"iPhone|iPad|iPod|Android|webOS|BlackBerry|Opera Mini|Opera Mobi|IEMobile|Windows Phone|Mobile Safari|Silk/|Kindle|PlayBook",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public DeviceDetectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();

        var isMobile = !string.IsNullOrEmpty(userAgent) && MobileRegex.IsMatch(userAgent);

        context.Items["IsMobile"] = isMobile;

        // Capture client IP for downstream components (e.g. GeoLocation)
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
            context.Items["ClientIp"] = forwardedFor.Split(',')[0].Trim();
        else
            context.Items["ClientIp"] = context.Connection.RemoteIpAddress?.ToString();

        await _next(context);
    }
}
