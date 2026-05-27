using Yap.Models;
using Yap.Services;

namespace Yap.Middleware;

/// <summary>
/// Validates auth token from cookie and populates UserStateService.
/// This runs before Blazor starts, enabling deep linking to work correctly.
/// </summary>
public class AuthMiddleware
{
    private readonly RequestDelegate _next;
    public const string CookieName = "yap_auth";

    public AuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, UserService userService, UserStateService userState)
    {
        var token = context.Request.Cookies[CookieName];

        if (!string.IsNullOrEmpty(token))
        {
            var user = userService.AuthenticateByToken(token);
            if (user != null)
            {
                userState.UserId = user.Id;
                userState.Username = user.Username;
                userState.DisplayName = user.DisplayName;
                userState.ProfilePictureUrl = user.ProfilePictureUrl;
                userState.Theme = user.Theme;
                userState.Status = UserStatus.Online;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// Sets the auth cookie with secure options.
    /// </summary>
    public static void SetAuthCookie(HttpContext context, string token)
    {
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromDays(365), // Long-lived for "remember me" behavior
            Path = "/"
        });
    }

    /// <summary>
    /// Clears the auth cookie.
    /// </summary>
    public static void ClearAuthCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/"
        });
    }
}
