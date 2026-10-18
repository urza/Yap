using System.Net;
using System.Security.Claims;
using Yap.Configuration;
using Yap.Helpers;
using Yap.Models.Admin;
using Yap.Services.Accounts;
using Yap.Services.Admin;

namespace Yap.Endpoints;

/// <summary>
/// The one HTML page. Every client route answers wwwroot/index.html, so a deep link, a reload and the
/// installed app's start URL all land in the same shell and the router in the browser takes over (SH-42).
/// The build stamp is written into the page so the client and its service worker know which build they
/// run (the new-version strip and the shell cache).
/// </summary>
public static class ShellEndpoints
{
    private static readonly string[] Routes =
    [
        "/", "/dm/{user}", "/settings", "/admin", "/login", "/verify/{user}", "/invite/{code}",
    ];

    public static void MapShell(this WebApplication app)
    {
        var env = app.Environment;
        string? cached = null;
        // Development reads the file on every request, so an edit shows on reload. Production reads it once.
        string Shell() => env.IsDevelopment() ? Read() : cached ??= Read();
        string Read()
        {
            using var reader = new StreamReader(env.WebRootFileProvider.GetFileInfo("index.html").CreateReadStream());
            return reader.ReadToEnd().Replace("{{build}}", BuildInfo.Time);
        }

        foreach (var route in Routes)
        {
            app.MapGet(route, (HttpContext http, ClaimsPrincipal principal, ServerConfig config, UserActionLogService log, RegistrationGateService gate) =>
            {
                // A signed-out visitor at the root gets the welcome page when the deployment has one (AD-36).
                if (route == "/" && !principal.IsSignedIn() && config.WelcomePageEnabled
                    && File.Exists(Path.Combine(env.ContentRootPath, "Data", "welcome", "welcome.html")))
                    return Results.Content(Welcome(env, config.ProjectName), "text/html; charset=utf-8");

                // A signed-out load of the login page or of an invite is a visit for the Visitors tab (AD-20). The
                // invite's code stays out of the log: the admin may not see a claimed code (AD-17). Only a navigation
                // counts: the service worker also fetches /login to store the shell, with Sec-Fetch-Mode "same-origin".
                var mode = http.Request.Headers["Sec-Fetch-Mode"].ToString();
                if (!principal.IsSignedIn() && route is "/login" or "/invite/{code}" && (mode.Length == 0 || mode == "navigate"))
                    log.Log(null, UserActionLog.KnownActions.VISIT, url: route == "/login" ? "/login" : "/invite",
                        info: route == "/login" && gate.RegistrationClosed ? "registration_closed" : null,
                        ip: IpHelper.GetClientIp(http), userAgent: http.Request.Headers.UserAgent.ToString());

                http.Response.Headers.CacheControl = "no-cache";   // always revalidate: the service worker, not the HTTP cache, serves the shell offline
                return Results.Content(Shell(), "text/html; charset=utf-8");
            });
        }
    }

    // Data/welcome/welcome.html is a fragment (styles and markup) that the admin edits. It gets a document
    // around it and the one behaviour it had: the #yap-enter element leads to the login page, after the
    // .welcome-bg "awaken" animation when the fragment has one.
    private static string Welcome(IWebHostEnvironment env, string projectName)
    {
        var fragment = File.ReadAllText(Path.Combine(env.ContentRootPath, "Data", "welcome", "welcome.html"));
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(projectName)}}</title>
            <link rel="icon" type="image/svg+xml" href="/icon.svg">
            </head>
            <body>
            {{fragment}}
            <script>
            (() => {
              const enter = document.getElementById('yap-enter');
              if (!enter) return;
              enter.style.cursor = 'pointer';
              enter.addEventListener('click', () => {
                const go = () => location.assign('/login');
                const bg = document.querySelector('.welcome-bg');
                if (bg) { enter.classList.add('lit'); bg.classList.add('awaken'); setTimeout(go, 2200); } else go();
              }, { once: true });
            })();
            </script>
            </body>
            </html>
            """;
    }
}
