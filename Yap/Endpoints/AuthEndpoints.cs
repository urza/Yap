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
        if (string.IsNullOrEmpty(username))
            return Results.Redirect("/");

        // Block login as bot user
        if (botService.IsBotUser(username))
            return Results.Redirect("/login");

        var registrationGate = context.RequestServices.GetRequiredService<RegistrationGateService>();

        User? user;
        string? newDeviceMethod = null;

        if (!string.IsNullOrEmpty(password))
        {
            user = userService.VerifyPassword(username, password);
            if (user == null)
                return Results.Redirect("/");
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
                    var requestIp = IpHelper.GetClientIp(context);
                    if (chatService.HasActiveSessionFromIp(username, requestIp))
                    {
                        user = existingUser;
                        newDeviceMethod = "smart";
                        actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.SMART_LOGIN,
                            info: username, ip: requestIp ?? "unknown");
                    }
                    else
                    {
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
                if (registrationGate.RegistrationClosed)
                    return Results.Redirect("/login");

                if (registrationGate.RequireApproval && !registrationGate.ConsumeApproval(username))
                    return Results.Redirect("/login");

                user = await userService.CreateUserAsync(username);
                if (user == null)
                    return Results.Redirect("/");
            }
        }

        AuthMiddleware.SetAuthCookie(context, user.Token);

        var ip = IpHelper.GetClientIp(context) ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        actionLog.Log(user.Id.ToString(), UserActionLog.KnownActions.LOGIN, info: username, ip: ip, userAgent: ua);

        if (newDeviceMethod != null)
            _ = botService.NotifyNewDeviceLoginAsync(username, newDeviceMethod, ip);

        var destination = "/lobby";
        if (!string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith("/") && !returnUrl.StartsWith("//"))
        {
            destination = returnUrl;
        }

        return Results.Redirect(destination);
    }
}
