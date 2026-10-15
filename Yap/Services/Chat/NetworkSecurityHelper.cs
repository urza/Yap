using System.Net;
using System.Net.Sockets;

namespace Yap.Services.Chat;

/// <summary>
/// The guard against requests into the server's own network (SSRF). Two layers. <see cref="IsPublicHostAsync"/>
/// answers before a request is made, for a quick refusal with a reason; <see cref="SafeRedirectHandler"/> asks it
/// again before every redirect hop (youtu.be needs redirects). <see cref="ConnectToPublicAddress"/> is the
/// ConnectCallback of every HttpClient that fetches a URL a person supplied (OutboundFetchExtensions): it refuses
/// at the moment of the connection, which covers what a check before the request cannot, a name that resolves to
/// a public address at the check and to a private one at the connection.
/// </summary>
public static class NetworkSecurityHelper
{
    public static async Task<bool> IsPublicHostAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            foreach (var addr in addresses)
            {
                if (IsPrivateAddress(addr))
                    return false;
            }
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Resolves the host itself and connects to a public address only. The handler never resolves on its
    /// own, so the address that was checked is the address that is used. Through an egress proxy (HTTPS_PROXY) the
    /// connection goes to the proxy, whose address is the operator's business; the request's own host is checked
    /// then, and the proxy is connected as it is. <see cref="SafeRedirectHandler"/> checks the later hops.</summary>
    public static async ValueTask<Stream> ConnectToPublicAddress(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var request = context.InitialRequestMessage;
        // A tunnel through the proxy begins with a CONNECT whose URI is the proxy itself; the target is its Host header.
        var targetHost = request.Method == HttpMethod.Connect
            ? (request.Headers.Host is { } authority ? HostOf(authority) : null)
            : request.RequestUri?.IdnHost;
        var viaProxy = targetHost is not null && !string.Equals(targetHost, endpoint.Host, StringComparison.OrdinalIgnoreCase);
        var host = viaProxy ? targetHost! : endpoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var allowed = addresses.Where(a => !IsPrivateAddress(a)).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException($"{host} is not a public address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (viaProxy) await socket.ConnectAsync(endpoint, ct);
            else await socket.ConnectAsync(allowed, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // "example.com:443" or "[2001:db8::1]:443" to the bare host name or address.
    private static string HostOf(string authority)
    {
        var host = new HostString(authority).Host;
        return host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                      // 10.0.0.0/8
                100 => bytes[1] >= 64 && bytes[1] <= 127,        // 100.64.0.0/10 (carrier NAT, Tailscale)
                172 => bytes[1] >= 16 && bytes[1] <= 31,         // 172.16.0.0/12
                192 => bytes[1] == 168,                          // 192.168.0.0/16
                169 => bytes[1] == 254,                          // 169.254.0.0/16 (link-local, cloud metadata)
                127 => true,                                     // 127.0.0.0/8
                0 => true,                                       // 0.0.0.0/8
                >= 224 => true,                                  // multicast, reserved, broadcast
                _ => false
            };
        }

        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;

        var ipv6Bytes = address.GetAddressBytes();
        if ((ipv6Bytes[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local

        return false;
    }
}
