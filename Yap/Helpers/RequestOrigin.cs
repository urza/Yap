namespace Yap.Helpers;

/// <summary>The origin the browser used, as "https://host". Behind a TLS-terminating proxy the scheme comes from
/// X-Forwarded-Proto, which the forwarded-headers middleware applies to Request.Scheme for a trusted proxy only
/// (<see cref="TrustedProxies"/>). The header itself is not read here: anyone can send one.</summary>
public static class RequestOrigin
{
    public static string? Origin(this HttpRequest request) =>
        request.Host.HasValue ? $"{request.Scheme}://{request.Host.Value}" : null;
}
