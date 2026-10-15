using Yap.Services.Emoji;

namespace Yap.Endpoints;

/// <summary>GET /api/emoji: the emoji catalogue. The same for everyone and for the life of a build, so the client
/// requests it with the build stamp in the URL and the browser keeps it.</summary>
public static class EmojiEndpoints
{
    public static void MapEmoji(this RouteGroupBuilder api)
    {
        api.MapGet("/emoji", (HttpResponse response, EmojiCatalog catalog) =>
        {
            response.Headers.CacheControl = "private, max-age=31536000, immutable";   // signed-in response: the browser keeps it, a shared cache must not
            return Results.Ok(catalog.Document);
        });
    }
}
