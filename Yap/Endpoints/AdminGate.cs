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
}
