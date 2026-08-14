using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyePeeOnMyTV.Models;

namespace EyePeeOnMyTV.Services;

/// <summary>
/// Loads/saves app settings as JSON under %AppData%\EyePeeOnMyTV\settings.json — this app has no
/// other persistence mechanism, so this is the one place settings are read or written.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EyePeeOnMyTV",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file — fall back to defaults rather than crash startup.
        }

        return CreateFirstRunDefaults();
    }

    public static void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    /// <summary>
    /// The values this app shipped with hardcoded in DataSourceService before Settings existed —
    /// used only as the seed for a brand-new settings file, so behavior is unchanged on first run
    /// until the user edits Settings. Once saved once, the real file takes over and these are
    /// never consulted again.
    /// </summary>
    private static AppSettings CreateFirstRunDefaults() => new()
    {
        PlaylistMode = PlaylistSourceMode.M3uUrl,
        M3uUrl = "https://your-provider.example/m3u/USERNAME/PASSWORD",
        EpgUrls = new List<string> { "https://epg.iptv.cat/epg.xml" },
    };
}
