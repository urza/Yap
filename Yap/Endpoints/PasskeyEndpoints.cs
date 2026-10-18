using System.Security.Claims;
using System.Text.Json;
using Yap.Helpers;
using Yap.Models.Accounts;
using Yap.Services.Accounts;

namespace Yap.Endpoints;

/// <summary>
/// Passkeys (AU-27 to AU-30, ST-27). Two anonymous routes sign a device in with a passkey; the account routes are
/// the Settings card. Every ceremony is two requests: the options, then the browser's answer with the challenge id
/// the options came with. PasskeyService checks; SignInService signs in; the cookie is set the same way as for
/// every other sign-in (SignInEndpoints.Outcome).
/// </summary>
public static class PasskeyEndpoints
{
    public static void MapPasskeySignIn(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth/passkey");

        auth.MapPost("/options", (HttpContext http, PasskeyService passkeys) =>
            Attempt(() => Results.Ok(passkeys.BeginSignIn(http.Request.Host)), 429)).AllowAnonymous();

        auth.MapPost("", async (HttpContext http, PasskeyAnswer body, PasskeyService passkeys, SignInService signIn, CancellationToken ct) =>
        {
            var ip = IpHelper.GetClientIp(http);
            string? userAgent = http.Request.Headers.UserAgent;
            try
            {
                var passkey = await passkeys.CompleteSignInAsync(body.ChallengeId, body.Credential, http.Request.Host, ct);
                return SignInEndpoints.Outcome(http, signIn.SignInWithPasskey(passkey, ip, userAgent));
            }
            catch (PasskeyException ex)
            {
                return SignInEndpoints.Outcome(http, signIn.PasskeyRefused(ex.Message, ip, userAgent));
            }
        }).AllowAnonymous();
    }

    public static void MapPasskeys(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/account/passkeys");

        group.MapGet("", (HttpContext http, ClaimsPrincipal me, PasskeyService passkeys) =>
            Results.Ok(new PasskeysDto(passkeys.ForUser(me.Id()).Select(PasskeyDto.From).ToList(), http.Request.Host.Host)));

        group.MapPost("/options", (HttpContext http, ClaimsPrincipal me, PasskeyService passkeys, UserService users) =>
            users.GetById(me.Id()) is { } user ? Attempt(() => Results.Ok(passkeys.BeginRegistration(user, http.Request.Host)), 429) : Results.Unauthorized());

        group.MapPost("", async (HttpContext http, PasskeyAnswer body, ClaimsPrincipal me, PasskeyService passkeys, UserService users, CancellationToken ct) =>
        {
            if (users.GetById(me.Id()) is not { } user) return Results.Unauthorized();
            try
            {
                var passkey = await passkeys.CompleteRegistrationAsync(user, body.ChallengeId, body.Credential, http.Request.Host, http.Request.Headers.UserAgent, ct);
                return Results.Ok(PasskeyDto.From(passkey));
            }
            catch (PasskeyException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });

        group.MapPatch("/{id:guid}", (Guid id, PasskeyRename body, ClaimsPrincipal me, PasskeyService passkeys) =>
            passkeys.Rename(me.Id(), id, body.Name) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/{id:guid}", (Guid id, ClaimsPrincipal me, PasskeyService passkeys) =>
            passkeys.Remove(me.Id(), id) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult Attempt(Func<IResult> action, int failureStatus)
    {
        try { return action(); }
        catch (PasskeyException ex) { return Results.Problem(ex.Message, statusCode: failureStatus); }
    }
}

/// <summary>The second request of a ceremony: the id the options came with, and the browser's credential as it
/// serialized it (PublicKeyCredential.toJSON). Handed to Fido2NetLib as is.</summary>
public sealed record PasskeyAnswer(string? ChallengeId, JsonElement Credential);

public sealed record PasskeyRename(string? Name);

public sealed record PasskeyDto(Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt, bool BackedUp, string RpId)
{
    public static PasskeyDto From(Passkey p) => new(p.Id, p.Name, p.CreatedAt, p.LastUsedAt, p.BackedUp, p.RpId);
}

/// <summary>The list, and the domain of this request, so the card can mark a key that was made for another address.</summary>
public sealed record PasskeysDto(List<PasskeyDto> Passkeys, string RpId);
