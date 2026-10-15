using System.Net;

namespace Yap.Helpers;

/// <summary>
/// The client's address as the server believes it. A forwarded header counts only when the connection comes from
/// a proxy in ChatSettings:TrustedProxies: the forwarded-headers middleware (Program.cs, <see cref="TrustedProxies"/>)
/// then rewrites RemoteIpAddress and removes the entries it consumed. An X-Forwarded-For that is still on the
/// request was sent by a peer the server does not trust, and the address is unknown then. Smart login, the memory
/// of known addresses and the IPv4 beacon all prefer "unknown" to a value the caller chose: with the header taken
/// at face value, a username and a guessed address were enough to sign in as someone else (AU-09).
/// </summary>
public static class IpHelper
{
    public static string? GetClientIp(HttpContext context)
    {
        if (UntrustedForwardedFor(context.Request) is not null) return null;
        if (context.Connection.RemoteIpAddress is not { } ip) return null;
        ip = Unmapped(ip);
        return IPAddress.IsLoopback(ip) ? "127.0.0.1" : ip.ToString();   // ::1 and 127.0.0.1 are one machine and one string
    }

    /// <summary>The X-Forwarded-For still on the request, or null. A value means no trusted proxy consumed it.
    /// CF-Connecting-IP and X-Real-IP are never read; a trusted proxy that sets them sets X-Forwarded-For too.</summary>
    public static string? UntrustedForwardedFor(HttpRequest request)
    {
        var value = request.Headers["X-Forwarded-For"].ToString();
        return value.Length == 0 ? null : value;
    }

    /// <summary>An IPv4 address that Kestrel reports in its IPv6-mapped form (::ffff:1.2.3.4), as plain IPv4.</summary>
    public static IPAddress Unmapped(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
