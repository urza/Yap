using System.Text.RegularExpressions;
using Yap.Services;

namespace Yap.Endpoints;

/// <summary>
/// Serves the chat client's HTML shell and the asset manifest its service worker installs from.
/// </summary>
public static class ChatShellEndpoints
{
    public static void MapChatShell(this WebApplication app)
    {
        // Computed once from the complete deployed package, never from personalized HTML.
        var manifest = app.Services.GetRequiredService<ChatShellManifest>();
        app.MapGet("/chat-client/manifest.json", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(manifest);
        });
        // Fail a broken release before accepting requests, rather than on every chat load.
        var shellHtml = File.ReadAllText(Path.Combine(app.Environment.WebRootPath, "chat-client", "index.html"));
        var appearanceToken = new Regex(@"\bdata-appearance(?=\s|>)");
        if (appearanceToken.Matches(shellHtml).Count != 1)
            throw new InvalidOperationException("Chat shell must contain exactly one data-appearance placeholder.");
        // These templates are what ChatRoutes.IsShell classifies; keep the two in step.
        foreach (var path in new[] { "/", "/chat", "/lobby", "/room/{id:guid}", "/dm/{username}" })
        {
            var endpoint = app.MapGet(path, (HttpContext http, UserStateService user) => ServeShell(http, user, shellHtml, appearanceToken));
            // The Razor Welcome page also owns GET /; ChatRootPolicy picks the shell for signed-in visitors.
            if (path == "/")
                endpoint.WithOrder(-1).WithMetadata(new ChatRootPolicy());
        }
    }

    // Only the neutral file is worker-cacheable. Online HTML carries current account
    // preferences before CSS; the blocking script mirrors them for cached navigation.
    private static async Task ServeShell(HttpContext http, UserStateService user, string html, Regex appearanceToken)
    {
        var theme = System.Net.WebUtility.HtmlEncode(user.Theme ?? "discord-dark");
        var size = user.FontSize is >= Models.User.MinFontSize and <= Models.User.MaxFontSize
            ? $"font-size: {user.FontSize}px" : "";
        html = appearanceToken.Replace(html, _ =>
            $"data-theme=\"{theme}\" style=\"{size}\" data-appearance-user=\"{user.UserId}\"");
        http.Response.ContentType = "text/html; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsync(html);
    }
}
