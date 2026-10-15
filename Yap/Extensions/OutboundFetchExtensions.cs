using Yap.Services.Chat;

namespace Yap.Extensions;

public static class OutboundFetchExtensions
{
    /// <summary>
    /// For every HttpClient that fetches a URL a person supplied (link previews, media, the GIF provider's items):
    /// the connection is refused at a private address, and redirects are followed by SafeRedirectHandler, which
    /// checks each hop's host before the hop (SSRF). The two settings only work together: with the handler's own
    /// redirect following on, SafeRedirectHandler would never see a hop. <see cref="NetworkSecurityHelper"/> has
    /// the rules.
    /// </summary>
    public static IHttpClientBuilder FetchesPublicAddressesOnly(this IHttpClientBuilder builder, bool useCookies = true) =>
        builder
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = useCookies, AllowAutoRedirect = false, ConnectCallback = NetworkSecurityHelper.ConnectToPublicAddress })
            .AddHttpMessageHandler(() => new SafeRedirectHandler());
}
