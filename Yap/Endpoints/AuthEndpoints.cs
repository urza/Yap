using Yap.Helpers;
using Yap.Middleware;
using Yap.Models;
using Yap.Services;

namespace Yap.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // GET: new user signup (from Login.razor)
        app.MapGet("/auth/signin", async (HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService, string username, string? password, string? returnUrl) =>
            await HandleSignIn(context, userService, actionLog, botService, username, password, returnUrl));

        // POST: existing user with passphrase (from VerifyDevice.razor — avoids password in URL)
        app.MapPost("/auth/signin", async (HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService) =>
        {
            var form = await context.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();
            return await HandleSignIn(context, userService, actionLog, botService, username, password, returnUrl);
        }).DisableAntiforgery();

        app.MapGet("/auth/signout", (HttpContext context, UserService userService, UserActionLogService actionLog) =>
        {
            var token = context.Request.Cookies[AuthMiddleware.CookieName];
            if (!string.IsNullOrEmpty(token))
            {
                var user = userService.AuthenticateByToken(token);
                if (user != null)
                {
                    var ip = IpHelper.GetClientIp(context) ?? "unknown";
                    actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGOUT, info: user.Username, ip: ip);
                }
            }

            AuthMiddleware.ClearAuthCookie(context);
            return Results.Redirect("/");
        });

        app.MapGet("/auth/refresh-token", (HttpContext context, UserService userService, string token, string? returnUrl) =>
        {
            var user = userService.AuthenticateByToken(token);
            if (user == null)
                return Results.Redirect("/");

            AuthMiddleware.SetAuthCookie(context, token);

            var destination = "/settings";
            if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
                destination = returnUrl;

            return Results.Redirect(destination);
        });
    }

    private static async Task<IResult> HandleSignIn(HttpContext context, UserService userService, UserActionLogService actionLog, SystemBotService botService, string username, string? password, string? returnUrl)
    {
        var ip = IpHelper.GetClientIp(context);
        var ua = context.Request.Headers.UserAgent.ToString();

        if (string.IsNullOrEmpty(username))
            return Results.Redirect("/");

        // Block login as bot user
        if (botService.IsBotUser(username))
        {
            actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"bot_username:{username}", ip: ip ?? "unknown", userAgent: ua);
            return Results.Redirect("/login");
        }

        var registrationGate = context.RequestServices.GetRequiredService<RegistrationGateService>();

        User? user;
        string? newDeviceMethod = null;

        if (!string.IsNullOrEmpty(password))
        {
            user = userService.VerifyPassword(username, password);
            if (user == null)
            {
                actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"wrong_passphrase:{username}", ip: ip ?? "unknown", userAgent: ua);
                return Results.Redirect("/");
            }
            newDeviceMethod = "passphrase";
        }
        else
        {
            var existingUser = userService.GetByUsername(username);
            if (existingUser != null)
            {
                if (registrationGate.SmartMode && !existingUser.SmartLoginOptOut)
                {
                    var chatService = context.RequestServices.GetRequiredService<ChatService>();
                    // Live session OR an IP this user was recently seen from (persisted,
                    // 14-day TTL) — the latter keeps smart login working across server
                    // restarts and closed browsers. Logged distinctly for auditability.
                    var liveMatch = chatService.HasActiveSessionFromIp(username, ip);
                    if (liveMatch || userService.HasRecentKnownIp(username, ip))
                    {
                        user = existingUser;
                        newDeviceMethod = "smart";
                        actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.SMART_LOGIN,
                            info: liveMatch ? username : $"known_ip:{username}", ip: ip ?? "unknown");
                    }
                    else
                    {
                        actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"ip_mismatch:{username}", ip: ip ?? "unknown", userAgent: ua);
                        return Results.Redirect("/login");
                    }
                }
                else
                {
                    return Results.Redirect("/login");
                }
            }
            else
            {
                // Unknown username about to become a brand-new account. If it matches an
                // existing member's display name, this is almost certainly that member
                // typing the name they now go by — creating the account would silently
                // fork their identity (doppelgänger user + duplicate DM channels).
                if (userService.FindByDisplayName(username) is not null)
                {
                    actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"displayname_collision:{username}", ip: ip ?? "unknown", userAgent: ua);
                    return Results.Redirect($"/login?reason=displayname&u={Uri.EscapeDataString(username)}");
                }

                if (registrationGate.RegistrationClosed)
                {
                    actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"registration_closed:{username}", ip: ip ?? "unknown", userAgent: ua);
                    return Results.Redirect("/login");
                }

                if (registrationGate.RequireApproval && !registrationGate.ConsumeApproval(username))
                {
                    actionLog.Log(null, UserActionLog.KnownActions.LOGIN_FAIL, info: $"approval_not_consumed:{username}", ip: ip ?? "unknown", userAgent: ua);
                    return Results.Redirect("/login");
                }

                user = await userService.CreateUserAsync(username);
                if (user == null)
                    return Results.Redirect("/");
            }
        }

        AuthMiddleware.SetAuthCookie(context, user.Token);

        // Remember this IP so smart login recognizes the user's networks (14-day window)
        userService.RecordKnownIp(user.Id, ip);

        actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: username, ip: ip ?? "unknown", userAgent: ua);

        if (newDeviceMethod != null)
            _ = botService.NotifyNewDeviceLoginAsync(username, newDeviceMethod, ip ?? "unknown");

        var destination = "/lobby";
        if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
        {
            destination = returnUrl;
        }

        return Results.Redirect(destination);
    }
}
