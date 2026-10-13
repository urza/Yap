using Yap.Middleware;
using Yap.Services;

namespace Yap.Endpoints;

/// <summary>
/// Names the chat client's paths so middleware, the endpoint map and the hub map agree on which
/// requests are the shell, the API and the hub.
/// </summary>
public static class ChatRoutes
{
    public const string Api = "/api/chat";
    public const string Hub = "/hubs/chat";

    // Not a pure path test: GET / belongs to the chat shell only for a signed-in visitor, so
    // this resolves the cookie. ChatRootPolicy and AuthMiddleware both rely on that.
    public static bool IsAuthenticatedRoot(HttpContext http) => HttpMethods.IsGet(http.Request.Method)
        && http.Request.Path == "/" && !http.Request.Query.ContainsKey("returnUrl")
        && http.RequestServices.GetRequiredService<UserService>()
            .AuthenticateByToken(http.Request.Cookies[AuthMiddleware.CookieName] ?? "") != null;
    public static bool IsShell(HttpContext http) => IsShell(http.Request.Path) || IsAuthenticatedRoot(http);
    public static bool IsShell(PathString path) => path.Equals("/chat", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/lobby", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/room") || path.StartsWithSegments("/dm");
    public static bool IsApi(PathString path) => path.StartsWithSegments(Api);
    public static bool IsHub(PathString path) => path.StartsWithSegments(Hub);
}
