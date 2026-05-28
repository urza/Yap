using System.Net;
using System.Net.Sockets;

namespace Yap.Services;

/// <summary>
/// Shared SSRF-prevention check: resolve the hostname and reject any address
/// that falls into a private / loopback / link-local range.
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
                172 => bytes[1] >= 16 && bytes[1] <= 31,        // 172.16.0.0/12
                192 => bytes[1] == 168,                          // 192.168.0.0/16
                169 => bytes[1] == 254,                          // 169.254.0.0/16 (link-local)
                127 => true,                                     // 127.0.0.0/8
                0 => true,                                       // 0.0.0.0/8
                _ => false
            };
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

        var ipv6Bytes = address.GetAddressBytes();
        if ((ipv6Bytes[0] & 0xFE) == 0xFC) return true; // fc00::/7 unique local

        return false;
    }
}
