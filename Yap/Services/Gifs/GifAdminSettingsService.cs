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

    private TenorContentFilter _tenorContentFilter = TenorContentFilter.Medium;

    public GifAdminSettingsService(IWebHostEnvironment env, ILogger<GifAdminSettingsService> logger)
    {
        _env = env;
        _logger = logger;
        LoadSettings();
    }

    private string SettingsFilePath => Path.Combine(_env.ContentRootPath, "Data", "gif-settings.json");

    public TenorContentFilter TenorContentFilter => _tenorContentFilter;

    public async Task SetTenorContentFilterAsync(TenorContentFilter value)
    {
        _tenorContentFilter = value;
        await SaveSettingsAsync();
        _logger.LogInformation("Tenor content filter set to {Value}", value);
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
                    _tenorContentFilter = settings.TenorContentFilter;
                }
                _logger.LogInformation("Loaded GIF settings: TenorContentFilter={Filter}", _tenorContentFilter);
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
                TenorContentFilter = _tenorContentFilter
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
        public TenorContentFilter TenorContentFilter { get; set; } = TenorContentFilter.Medium;
    }
}

public enum TenorContentFilter
{
    Off,
    Low,
    Medium,
    High,
}
