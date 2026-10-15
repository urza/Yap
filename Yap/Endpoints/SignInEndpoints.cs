using System.Security.Claims;
using Yap.Helpers;
using Yap.Models.Admin;
using Yap.Services.Accounts;
using Yap.Services.Admin;
using Yap.Services.Notifications;
using Yap.Services.Presence;

namespace Yap.Endpoints;

/// <summary>
/// The JSON routes behind the login, verify and invite pages, and sign-out (docs/architecture/sign-in.md).
/// The sign-in routes set the cookie in their response; the client then navigates. SignInService decides.
/// </summary>
public static class SignInEndpoints
{
    public static void MapSignIn(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/check", (HttpContext http, string? username, SignInService signIn) =>
            Results.Ok(signIn.Check(username, IpHelper.GetClientIp(http)))).AllowAnonymous();

        auth.MapPost("/signin", (HttpContext http, SignInRequest body, SignInService signIn) =>
            Outcome(http, signIn.SignIn(body.Username, body.Code, IpHelper.GetClientIp(http), http.Request.Headers.UserAgent, AuthCookie.Pending(http.Request)))).AllowAnonymous();

        auth.MapGet("/pending/{username}", (HttpContext http, string username, SignInService signIn) =>
            Results.Ok(new { state = signIn.PendingState(username.Trim().ToLowerInvariant(), AuthCookie.Pending(http.Request)) })).AllowAnonymous();

        auth.MapPost("/signout", async (HttpContext http, ClaimsPrincipal me, SignOutRequest? body, PushSubscriptionStore push, PresenceService presence, UserActionLogService log) =>
        {
            log.Log(me.Id().ToString(), UserActionLog.KnownActions.LOGOUT, info: me.Username(), ip: IpHelper.GetClientIp(http) ?? "unknown");
            if (!string.IsNullOrEmpty(body?.PushEndpoint)) await push.RemoveOwnAsync(me.Username(), body.PushEndpoint);   // NT-24: this account's subscription, not anyone's
            AuthCookie.Clear(http.Response);   // this device only; the token stays valid for the others (AU-22)
            // The device's other tabs hold hub connections that were authenticated when they opened. The cookie
            // is gone now, so those connections end here; a tab that tries again gets 401 and shows the login
            // page (GEN-05, AU-21). The tabs also hear of the sign-out from the signing-out tab (session.js).
            if (!string.IsNullOrEmpty(body?.DeviceId))
                foreach (var session in presence.SessionsOf(me.Id()).Where(s => s.DeviceId == body.DeviceId)) presence.End(session);
            return Results.NoContent();
        }).RequireAuthorization();

        var invite = app.MapGroup("/api/invite");

        invite.MapGet("/{code}", (string code, ClaimsPrincipal principal, SignInService signIn) =>
            Results.Ok(signIn.LookupInvite(code, principal.IsSignedIn() ? principal.Id() : null))).AllowAnonymous();

        invite.MapPost("/{code}", (HttpContext http, string code, RedeemRequest? body, SignInService signIn) =>
            Outcome(http, signIn.Redeem(code, body?.Username, IpHelper.GetClientIp(http), http.Request.Headers.UserAgent))).AllowAnonymous();
    }

    private static IResult Outcome(HttpContext http, SignInOutcome outcome)
    {
        if (outcome.User is { } user)
        {
            AuthCookie.Set(http.Response, user.Token);
            http.RequestServices.GetRequiredService<AccessLinkService>().ObserveOrigin(user.Id, http.Request.Origin());   // this person's welcome link needs the address this person used (AU-15)
            AuthCookie.ClearPending(http.Response);
            return Results.Ok(new { username = user.Username });
        }
        if (outcome.Status != 202) return Results.Problem(outcome.Reason, statusCode: outcome.Status);
        if (outcome.PendingToken is { } token) AuthCookie.SetPending(http.Response, token);
        return Results.Accepted(value: new { state = "pending" });
    }
}

public sealed record SignInRequest(string Username, string? Code);
public sealed record SignOutRequest(string? PushEndpoint, string? DeviceId);
public sealed record RedeemRequest(string? Username);
