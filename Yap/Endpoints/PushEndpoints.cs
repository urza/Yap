using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Yap.Models;
using Yap.Services;
using Yap.Services.Accounts;
using Yap.Services.Notifications;

namespace Yap.Endpoints;

public static class PushEndpoints
{
    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/push");

        group.MapGet("/vapid-public-key", (PushNotificationService pushService, ILogger<Program> logger) =>
        {
            var publicKey = pushService.GetPublicKey();
            logger.LogDebug("VAPID public key requested, configured={IsConfigured}", publicKey != null);
            return publicKey != null
                ? Results.Ok(new { publicKey })
                : Results.NotFound(new { error = "VAPID not configured" });
        });

        group.MapPost("/subscribe", async (HttpContext context, ClaimsPrincipal user, PushSubscriptionStore store, ILogger<Program> logger) =>
        {
            var body = await context.Request.ReadFromJsonAsync<PushSubscribeRequest>();
            // A push endpoint is an https URL of a push service; the server posts to it later, so nothing else passes.
            if (body == null || !Uri.TryCreate(body.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
                return Results.BadRequest(new { error = "Invalid subscription" });

            logger.LogDebug("Push subscribe for {Username}, endpoint={Endpoint}", user.Username(), body.Endpoint[..Math.Min(50, body.Endpoint.Length)]);

            var saved = await store.SaveSubscriptionAsync(user.Username(), new PushSubscriptionInfo
            {
                Endpoint = body.Endpoint,
                P256dh = body.P256dh ?? "",
                Auth = body.Auth ?? ""
            });
            if (!saved) return Results.Problem("That subscription belongs to another account.", statusCode: 409);

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        group.MapPost("/unsubscribe", async (HttpContext context, ClaimsPrincipal user, PushSubscriptionStore store, ILogger<Program> logger) =>
        {
            var body = await context.Request.ReadFromJsonAsync<PushUnsubscribeRequest>();
            if (body == null || string.IsNullOrEmpty(body.Endpoint))
                return Results.BadRequest(new { error = "Invalid request" });

            logger.LogDebug("Push unsubscribe for {Username}, endpoint={Endpoint}", user.Username(), body.Endpoint[..Math.Min(50, body.Endpoint.Length)]);

            if (!await store.RemoveOwnAsync(user.Username(), body.Endpoint)) return Results.NotFound();   // one's own only
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // The Settings card (ST-17, NT-30): the person's devices, Remove, and the test send that ignores every mute.
        group.MapGet("/devices", (ClaimsPrincipal user, PushSubscriptionStore store) =>
            Results.Ok(store.GetSubscriptionsDetailed(user.Username())
                .Select(s => new PushDeviceDto(s.Endpoint, Platform(s.Endpoint), s.CreatedAt, store.GetLastDelivered(s.Endpoint))).ToList()))
            .RequireAuthorization();

        group.MapDelete("/devices", async (ClaimsPrincipal user, [FromBody] PushDeviceRequest body, PushSubscriptionStore store) =>   // a DELETE body is not inferred
        {
            if (!await store.RemoveOwnAsync(user.Username(), body.Endpoint)) return Results.NotFound();
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/test", async (ClaimsPrincipal user, PushNotificationService pushService) =>
            Results.Ok(await pushService.SendTestAsync(user.Username()))).RequireAuthorization();

        // Delivery receipt from the service worker's push handler. Anonymous by design: it fires
        // while the app is closed, where the auth cookie may be absent or expired in SW context.
        // The subscription endpoint URL is itself a high-entropy secret. Presenting one proves
        // the caller received a push for it; unknown endpoints get 404.
        group.MapPost("/delivered", async (HttpContext context, PushSubscriptionStore store) =>
        {
            var body = await context.Request.ReadFromJsonAsync<PushDeliveredRequest>();
            if (body == null || string.IsNullOrEmpty(body.Endpoint))
                return Results.BadRequest(new { error = "Invalid request" });

            return store.MarkDelivered(body.Endpoint)
                ? Results.Ok(new { success = true })
                : Results.NotFound();
        });
    }

    // The push service behind an endpoint, for the device list's hint (NT wording: platform hints).
    private static string Platform(string endpoint) =>
        endpoint.Contains("push.apple.com") ? "Apple (Safari / iOS)"
        : endpoint.Contains("fcm.googleapis.com") || endpoint.Contains("google") ? "Google (Chrome / Edge / Android)"
        : endpoint.Contains("mozilla.com") ? "Mozilla (Firefox)"
        : endpoint.Contains("notify.windows.com") ? "Microsoft (WNS)"
        : "Notification device";

    private record PushSubscribeRequest(string Username, string Endpoint, string? P256dh, string? Auth);
    private record PushDeviceRequest(string Endpoint);
    public record PushDeviceDto(string Endpoint, string Platform, DateTime AddedAt, DateTime? DeliveredAt);
    private record PushUnsubscribeRequest(string Endpoint);
    private record PushDeliveredRequest(string Endpoint, string? Tag, bool? Shown);
}
