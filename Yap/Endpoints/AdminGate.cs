using Yap.Services;

namespace Yap.Endpoints;

/// <summary>
/// Admin gate for API route groups. AuthMiddleware has already resolved the token cookie into
/// the request-scoped UserStateService by the time an endpoint runs, so this only checks the
/// admin flag. Non-admins get 404 (not 403) so gated endpoints stay invisible.
/// </summary>
public static class AdminGate
{
    public static RouteGroupBuilder RequireAdmin(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
        {
            var userState = context.HttpContext.RequestServices.GetRequiredService<UserStateService>();
            var users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
            return userState.UserId is Guid id && users.IsAdmin(id)
                ? await next(context)
                : Results.NotFound();
        });

    /// <summary>
    /// Any-signed-in-user gate. Unlike RequireAdmin's stealth 404, this returns 401 — these
    /// endpoints are part of the signed-in surface, so the honest answer is "log in again".
    /// </summary>
    public static RouteGroupBuilder RequireUser(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<UserStateService>().UserId is not null
                ? await next(context)
                : Results.Unauthorized());
}
