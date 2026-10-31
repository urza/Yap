using System.Text.Json;

namespace Yap.Endpoints;

/// <summary>
/// The generated parts of the shell, from one scan of wwwroot at startup: the import map that gives every module a
/// URL with the build stamp, the modulepreload links for the same modules, and the service worker's list of files
/// to precache. One scan, so the three never disagree and no list is kept by hand. Every deploy changes the stamp
/// and so every URL; a per-file hash would invalidate less and was not worth its machinery (docs/performance-notes.md).
/// A file added to wwwroot shows up after a restart, as a new build does.
/// </summary>
public sealed class ShellAssets
{
    public string ImportMap { get; }
    public string ModulePreloads { get; }
    public string ShellList { get; }

    public ShellAssets(string webRoot, string build)
    {
        var v = Uri.EscapeDataString(build);
        var modules = Files(webRoot, "js", "*.js", recurse: true);
        var versioned = modules
            .Concat(Files(webRoot, "lib", "*.js"))
            .Concat(Files(webRoot, ".", "*.css")).Concat(Files(webRoot, "css", "*.css")).Concat(Files(webRoot, "themes", "*.css"))
            .Select(p => $"{p}?v={v}");
        // These URLs carry no stamp: the page and the CSS request them bare. The regular font only: italic text is
        // rare, and the worker keeps the italic file from its first use. The theme backgrounds (images/themes,
        // 1.5 MB) are not precached for the same reason.
        var plain = Files(webRoot, "fonts", "*.woff2").Where(f => !f.Contains("Italic"))
            .Concat(Files(webRoot, ".", "*.svg")).Concat(Files(webRoot, ".", "*.png")).Concat(Files(webRoot, ".", "*.mp3"))
            .Concat(Files(webRoot, "images", "*.png")).Concat(Files(webRoot, "images", "*.svg"));

        ImportMap = JsonSerializer.Serialize(new { imports = modules.ToDictionary(p => p, p => $"{p}?v={v}") });
        // Data modules (js/data: the picker's emoji list, 44 KB) are imported on demand and must not be preloaded: a preload
        // fetches and compiles them at every start, for a picker that may never open (PK-11). They stay in the import map
        // and in the worker's list, so the import on first use carries the build stamp and is a cache hit.
        ModulePreloads = string.Join("\n    ", modules.Where(p => !p.StartsWith("/js/data/")).Select(p => $"""<link rel="modulepreload" href="{p}?v={v}" />"""));
        ShellList = JsonSerializer.Serialize(new[] { "/login" }.Concat(versioned).Concat(plain));
    }

    // Paths as the page requests them: from the root, forward slashes, sorted so the output is stable.
    private static List<string> Files(string webRoot, string dir, string pattern, bool recurse = false)
    {
        var root = Path.GetFullPath(Path.Combine(webRoot, dir));
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, pattern, recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Select(f => "/" + Path.GetRelativePath(webRoot, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToList();
    }
}
