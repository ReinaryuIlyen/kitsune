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
    public string? LastMacro { get; set; }
    /// <summary>Host key names (see HostKeys) for the macro hotkeys. They are reserved while the emulator window is in front.</summary>
    public string RecordKey { get; set; } = "F8";
    public string CheckpointKey { get; set; } = "F9";
    public string StopMacroKey { get; set; } = "F10";
    /// <summary>Size of a checkpoint region as a fraction of the shorter screen side.</summary>
    public double CheckpointSize { get; set; } = 0.12;
    public int CheckpointTolerance { get; set; } = 14;
    public int CheckpointTimeoutMs { get; set; } = 30000;

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
