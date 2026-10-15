using System.Net;
using System.Security.Claims;
using Yap.Configuration;
using Yap.Services.Accounts;

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
        "/", "/lobby", "/room/{id:guid}", "/dm/{user}", "/settings", "/admin",
        "/channel/new", "/channel/{id:guid}/settings", "/login", "/verify/{user}", "/invite/{code}",
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
            app.MapGet(route, (HttpContext http, ClaimsPrincipal principal, ServerConfig config) =>
            {
                // A signed-out visitor at the root gets the welcome page when the deployment has one (AD-36).
                if (route == "/" && !principal.IsSignedIn() && config.WelcomePageEnabled
                    && File.Exists(Path.Combine(env.ContentRootPath, "Data", "welcome", "welcome.html")))
                    return Results.Content(Welcome(env, config.ProjectName), "text/html; charset=utf-8");

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
