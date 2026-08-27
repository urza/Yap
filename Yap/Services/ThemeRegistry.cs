namespace Yap.Services;

/// <summary>
/// Metadata for a color theme. The actual CSS variables live in wwwroot/themes.css
/// under a matching <c>[data-theme="{Id}"]</c> selector block.
/// </summary>
/// <param name="Id">Theme identifier — written to the <c>data-theme</c> attribute on &lt;html&gt;.</param>
/// <param name="Name">Display name shown in the picker.</param>
/// <param name="PreviewBg">CSS <c>background</c> value (solid color or gradient) used in the picker card.</param>
/// <param name="PreviewAccent">Hex color shown as the accent dot in the picker card.</param>
/// <param name="HasGradient">Whether this theme uses a gradient on its main background.</param>
public record ThemeDefinition(
    string Id,
    string Name,
    string PreviewBg,
    string PreviewAccent,
    bool HasGradient);

/// <summary>
/// Curated list of color themes. Order here = display order in Settings.
/// The first entry (discord-dark) is the default — it has no override block in
/// themes.css and falls through to the <c>:root</c> values in app.css.
/// </summary>
public static class ThemeRegistry
{
    public const string DefaultId = "discord-dark";

    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        new ThemeDefinition(
            "discord-dark", "Discord Dark",
            PreviewBg: "#36393f",
            PreviewAccent: "#5865f2",
            HasGradient: false),

        new ThemeDefinition(
            "midnight", "Midnight",
            PreviewBg: "#0a0a14",
            PreviewAccent: "#7c4dff",
            HasGradient: false),

        new ThemeDefinition(
            "nord", "Nord",
            PreviewBg: "#2e3440",
            PreviewAccent: "#88c0d0",
            HasGradient: false),

        new ThemeDefinition(
            "ocean", "Ocean",
            PreviewBg: "linear-gradient(135deg, #0f2027 0%, #203a43 50%, #2c5364 100%)",
            PreviewAccent: "#26d0ce",
            HasGradient: true),

        new ThemeDefinition(
            "sunset", "Sunset",
            PreviewBg: "linear-gradient(135deg, #2d1b3d 0%, #6b2d5c 50%, #c44569 100%)",
            PreviewAccent: "#ff6b9d",
            HasGradient: true),

        new ThemeDefinition(
            "aurora", "Aurora",
            PreviewBg: "linear-gradient(135deg, #1a0a2e 0%, #16213e 50%, #0f3460 100%)",
            PreviewAccent: "#a78bfa",
            HasGradient: true),

        new ThemeDefinition(
            "terminal", "Terminal",
            PreviewBg: "#07090a",
            PreviewAccent: "#33ff66",
            HasGradient: false),

        new ThemeDefinition(
            "neon-glow", "Neon Glow",
            PreviewBg: "linear-gradient(135deg, #0d0820 0%, #2a0f3d 55%, #4a1240 100%)",
            PreviewAccent: "#ff2e88",
            HasGradient: true),

        new ThemeDefinition(
            "teahouse", "Tea House",
            PreviewBg: "url('/images/themes/teahouse/preview.webp') center/cover",
            PreviewAccent: "#e0c060",
            HasGradient: false),

        new ThemeDefinition(
            "light", "Daylight",
            PreviewBg: "#f5f5f7",
            PreviewAccent: "#5865f2",
            HasGradient: false),

        // Preview is base3 on blue — the two colours the palette is recognised by.
        new ThemeDefinition(
            "solarized-light", "Solarized Light",
            PreviewBg: "#fdf6e3",
            PreviewAccent: "#268bd2",
            HasGradient: false),
    };

    public static bool IsKnown(string? id) =>
        !string.IsNullOrEmpty(id) && All.Any(t => t.Id == id);
}
