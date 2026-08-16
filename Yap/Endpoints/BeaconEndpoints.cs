using System.Net;
using System.Net.Sockets;
using Yap.Helpers;
using Yap.Services;

namespace Yap.Endpoints;

/// <summary>
/// The IPv4 beacon: lets the admin panel show a dualstack client's IPv4 address.
///
/// Why: a dualstack browser prefers IPv6 (Happy Eyeballs), and one connection carries
/// exactly one address family — the server can never learn the client's IPv4 from the
/// circuit itself. So chat.js fires one background no-cors GET at a hostname that has
/// ONLY an A record (grey-cloud, like the tus upload subdomain). DNS forces that request
/// over IPv4, and whatever address it arrives from is the client's public IPv4.
///
/// Opt-in via ChatSettings:Ipv4BeaconUrl — when empty, the client never fires and this
/// endpoint just sits unused. The endpoint is anonymous by necessity: the beacon request
/// is cross-origin, so the SameSite auth cookie is not sent. The URL therefore carries a
/// single-use random nonce (never the sessionId), redeemed against ChatService's in-memory
/// map — and the recorded value is display-only regardless (see RecordBeaconIpv4).
/// </summary>
public static class BeaconEndpoints
{
    public static void MapBeaconEndpoints(this WebApplication app)
    {
        app.MapGet("/api/ipv4-beacon", (HttpContext context, ChatService chatService, string? n) =>
        {
            // Accept only a genuine IPv4 arrival. If the beacon hostname is misconfigured
            // with an AAAA record the request may come in over IPv6 — silently drop it
            // rather than record a second v6 address as "the v4".
            var ip = IpHelper.GetClientIp(context);
            if (!string.IsNullOrEmpty(n) && ip != null
                && IPAddress.TryParse(ip, out var addr)
                && (addr.AddressFamily == AddressFamily.InterNetwork || addr.IsIPv4MappedToIPv6))
            {
                chatService.RecordBeaconIpv4(n, addr.MapToIPv4().ToString());
            }

            // Always 204, valid nonce or not — the response is opaque to the caller
            // anyway (no-cors), and an error would only leak which nonces exist.
            return Results.NoContent();
        });
    }
}
