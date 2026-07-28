using Microsoft.AspNetCore.Http.Features;
using Yap.Services;
using Yap.Services.Gifs;

namespace Yap.Endpoints;

/// <summary>
/// GIF library export downloads. Plain GET links (cookie auth flows automatically from
/// same-origin &lt;a download&gt;), streaming the zip straight onto the response body —
/// no temp file, no buffering.
/// </summary>
public static class GifLibraryEndpoints
{
    public static void MapGifLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        var user = app.MapGroup("/api/gifs").RequireUser();
        user.MapGet("/export", (HttpContext ctx, GifService gifs, UserStateService state) =>
            StreamPackAsync(ctx, gifs, state.UserId, "yap-my-gifs.zip"));

        var admin = app.MapGroup("/api/admin/gifs").RequireAdmin();
        admin.MapGet("/export", (HttpContext ctx, GifService gifs) =>
            StreamPackAsync(ctx, gifs, userId: null, "yap-server-gifs.zip"));
    }

    private static async Task StreamPackAsync(HttpContext ctx, GifService gifs, Guid? userId, string fileName)
    {
        // Even through the async ZipArchive APIs, the zip writer performs a few synchronous
        // writes internally (entry headers, central directory) — Kestrel throws on those
        // mid-download unless sync IO is allowed for this response. The bulk copying stays
        // async; only those small bookkeeping writes ever block.
        var bodyControl = ctx.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl != null) bodyControl.AllowSynchronousIO = true;

        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await gifs.WritePackAsync(ctx.Response.Body, userId, ctx.RequestAborted);
    }
}
