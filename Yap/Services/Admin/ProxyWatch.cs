using Yap.Helpers;

namespace Yap.Services.Admin;

/// <summary>
/// Remembers that requests arrive with an X-Forwarded-For from a peer that is not in ChatSettings:TrustedProxies.
/// Such a server runs, but every client address counts as unknown and smart login is off (IpHelper), and the
/// person who installed it must learn why in a place they look at: the log says it at start and at the first such
/// request, the bot tells the admin in chat, and the admin page shows a notice until the proxy is listed and the
/// server restarted (the list is read at start). A request through Cloudflare is not such a request: Cloudflare is
/// trusted by its own address ranges (IpHelper, CloudflareIps).
/// </summary>
public sealed class ProxyWatch(IConfiguration configuration)
{
    private readonly object _gate = new();

    public IReadOnlyList<string> Trusted { get; } = TrustedProxies.Entries(configuration);

    public string? UntrustedPeer { get; private set; }
    public string? ForwardedFor { get; private set; }   // the entries no trusted proxy consumed
    public DateTime? FirstSeenAt { get; private set; }
    public long Count { get; private set; }

    /// <summary>Records a request whose forwarded header no trusted proxy consumed; false for a request without one.
    /// True the first time, for the log and the bot.</summary>
    public bool Note(HttpContext context)
    {
        if (IpHelper.UntrustedForwardedFor(context) is not { } forwardedFor) return false;
        lock (_gate)
        {
            Count++;
            if (FirstSeenAt is not null) return false;
            FirstSeenAt = DateTime.UtcNow;
            UntrustedPeer = context.Connection.RemoteIpAddress is { } peer ? IpHelper.Unmapped(peer).ToString() : null;
            ForwardedFor = forwardedFor;
            return true;
        }
    }

    public ProxyStatusDto Status()
    {
        lock (_gate) return new(Trusted, UntrustedPeer, ForwardedFor, FirstSeenAt, Count);
    }
}

public sealed record ProxyStatusDto(IReadOnlyList<string> Trusted, string? UntrustedPeer, string? ForwardedFor, DateTime? FirstSeenAt, long Count);
