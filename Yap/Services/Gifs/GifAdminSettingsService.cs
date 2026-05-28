using System.Text.Json;

namespace Yap.Services.Gifs;

/// <summary>
/// Admin-mutable settings for the GIF feature. Persisted to Data/gif-settings.json
/// (same pattern as RegistrationGateService and LinkPreviewSettingsService).
/// Changes take effect on the next provider request — no restart required.
/// </summary>
public class GifAdminSettingsService
{
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<GifAdminSettingsService> _logger;

    private KlipyRating _klipyRating = KlipyRating.PG;

    public GifAdminSettingsService(IWebHostEnvironment env, ILogger<GifAdminSettingsService> logger)
    {
        _env = env;
        _logger = logger;
        LoadSettings();
    }

    private string SettingsFilePath => Path.Combine(_env.ContentRootPath, "Data", "gif-settings.json");

    public KlipyRating KlipyRating => _klipyRating;

    public async Task SetKlipyRatingAsync(KlipyRating value)
    {
        _klipyRating = value;
        await SaveSettingsAsync();
        _logger.LogInformation("Klipy rating set to {Value}", value);
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<GifSettings>(json);
                if (settings != null)
                {
                    _klipyRating = settings.KlipyRating;
                }
                _logger.LogInformation("Loaded GIF settings: KlipyRating={Rating}", _klipyRating);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load GIF settings from {Path}", SettingsFilePath);
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = new GifSettings
            {
                KlipyRating = _klipyRating
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(SettingsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save GIF settings to {Path}", SettingsFilePath);
        }
    }

    private class GifSettings
    {
        public KlipyRating KlipyRating { get; set; } = KlipyRating.PG;
    }
}

/// <summary>
/// Klipy's 4-level content rating. Maps to API parameter values: g, pg, pg-13, r.
/// </summary>
public enum KlipyRating
{
    G,
    PG,
    PG13,
    R,
}
