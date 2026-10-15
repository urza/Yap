using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Yap.Services.Accounts;

/// <summary>
/// Turns the sign-in cookie into a principal with three claims: the account id, the username and,
/// for the admin, the "admin" role. Everything else about the account comes from the bootstrap.
/// The id claim is also what SignalR uses for Clients.User(...).
/// </summary>
public sealed class YapCookieAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, UserService users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "yap";
    public const string AdminPolicy = "admin";
    public const string AdminRole = "admin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AuthCookie.Name, out var token) || users.AuthenticateByToken(token) is not { } user)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        if (users.IsAdmin(user.Id))
            identity.AddClaim(new Claim(ClaimTypes.Role, AdminRole));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    // An anonymous call to an admin route gets 404 like a signed-in non-admin: the admin routes stay
    // invisible to anyone who is not the admin. Every other anonymous call gets 401.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = RequiresAdmin() ? StatusCodes.Status404NotFound : StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    private bool RequiresAdmin() =>
        Context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == AdminPolicy) == true;
}

public static class PrincipalExtensions
{
    /// <summary>The account id of a signed-in principal. Only valid after authorization passed.</summary>
    public static Guid Id(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static string Username(this ClaimsPrincipal user) => user.Identity!.Name!;

    public static bool IsSignedIn(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true;
}
