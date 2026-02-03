using System.Text.RegularExpressions;

namespace Yap.Services;

public partial class CustomEmojiService
{
    private readonly Dictionary<string, CustomEmoji> _emojis = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CustomEmojiService> _logger;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".svg", ".gif", ".webp", ".jpg", ".jpeg"
    };

    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex ValidFilenameRegex();

    public bool HasCustomEmojis => _emojis.Count > 0;

    public CustomEmojiService(IWebHostEnvironment env, ILogger<CustomEmojiService> logger)
    {
        _logger = logger;

        var folder = Path.Combine(env.ContentRootPath, "Data", "custom-emojis");
        Directory.CreateDirectory(folder);

        ScanFolder(folder);
    }

    private void ScanFolder(string folder)
    {
        foreach (var file in Directory.GetFiles(folder))
        {
            var ext = Path.GetExtension(file);
            if (!AllowedExtensions.Contains(ext))
                continue;

            var name = Path.GetFileNameWithoutExtension(file);
            if (!ValidFilenameRegex().IsMatch(name))
            {
                _logger.LogWarning("Skipping custom emoji with invalid filename: {File}", Path.GetFileName(file));
                continue;
            }

            var shortcode = name.ToLowerInvariant();
            if (_emojis.ContainsKey(shortcode))
            {
                _logger.LogWarning("Duplicate custom emoji shortcode '{Shortcode}', skipping {File}", shortcode, Path.GetFileName(file));
                continue;
            }

            _emojis[shortcode] = new CustomEmoji
            {
                Shortcode = shortcode,
                Filename = Path.GetFileName(file),
                Url = $"/custom-emojis/{Path.GetFileName(file)}"
            };
        }

        _logger.LogInformation("Loaded {Count} custom emoji(s) from {Folder}", _emojis.Count, folder);
    }

    public IReadOnlyCollection<CustomEmoji> GetAll() => _emojis.Values;

    public CustomEmoji? GetByShortcode(string shortcode) =>
        _emojis.GetValueOrDefault(shortcode);

    public bool IsCustomEmoji(string shortcode) =>
        _emojis.ContainsKey(shortcode);
}

public class CustomEmoji
{
    public required string Shortcode { get; init; }
    public required string Filename { get; init; }
    public required string Url { get; init; }
}
