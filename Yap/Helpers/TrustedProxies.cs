using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Yap.Helpers;

/// <summary>
/// ChatSettings:TrustedProxies as the forwarded-headers middleware wants it: the addresses and the networks whose
/// X-Forwarded-For and X-Forwarded-Proto the server believes. The list is empty by default, and the framework
/// adds the loopback addresses itself: a proxy on the same machine works out of the box, a proxy in another
/// container or on another host must be listed. <see cref="IpHelper"/> says what a forwarded header from anyone
/// else does to the request.
/// </summary>
public static class TrustedProxies
{
    /// <summary>The configured entries, trimmed and without blanks: the list as the log and the admin page show it.</summary>
    public static IReadOnlyList<string> Entries(IConfiguration configuration) =>
        (configuration.GetSection("ChatSettings:TrustedProxies").Get<string[]>() ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

    public static ForwardedHeadersOptions Options(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,   // every hop of a chain, as long as each hop is a trusted one
        };
        foreach (var text in Entries(configuration))
        {
            if (System.Net.IPNetwork.TryParse(text, out var network)) options.KnownIPNetworks.Add(network);
            else if (IPAddress.TryParse(text, out var address)) options.KnownProxies.Add(address);
            else throw new InvalidOperationException($"ChatSettings:TrustedProxies: '{text}' is neither an address nor a network in CIDR form.");
        }
        return options;
    }
}
