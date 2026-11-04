using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitsune.Core.Settings;

/// <summary>Launcher-wide preferences. Instance-specific tuning lives in each instance's kitsune.json.</summary>
public sealed class AppSettings
{
    public string? LastInstance { get; set; }
    public string DefaultImage { get; set; } = "system-images;android-35;google_apis_playstore;x86_64";
    public bool OverlayEnabled { get; set; } = true;
    public bool ShowOverlayBadges { get; set; } = true;
    public bool CheckRepositoryOnStart { get; set; } = false;

    public static AppSettings Load(KitsunePaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
                return JsonSerializer.Deserialize(File.ReadAllText(paths.SettingsFile), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save(KitsunePaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.SettingsFile, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext { }
