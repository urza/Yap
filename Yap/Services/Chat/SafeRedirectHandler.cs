using System.Net;

namespace Yap.Services.Chat;

/// <summary>
/// Follows redirects itself, so that every hop's host passes the public-host check before it is requested. The
/// handler's own following checks nothing, and through an egress proxy the connect callback sees the proxy, not
/// the target, so the hop would reach a private address unseen. GET requests only: the clients that carry this
/// handler fetch pages and files. The primary handler runs with AllowAutoRedirect off.
/// </summary>
public sealed class SafeRedirectHandler : DelegatingHandler
{
    private const int MaxHops = 10;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        for (var hop = 0; hop < MaxHops && IsRedirect(response.StatusCode) && response.Headers.Location is { } location; hop++)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            if (target.Scheme is not ("http" or "https") || !await NetworkSecurityHelper.IsPublicHostAsync(target.IdnHost))
                throw new HttpRequestException($"{request.RequestUri?.IdnHost} redirects to {target.IdnHost}, which is not a public address.");
            response.Dispose();
            var next = new HttpRequestMessage(HttpMethod.Get, target);
            foreach (var header in request.Headers) next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request = next;
            response = await base.SendAsync(request, ct);
        }
        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
               or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
