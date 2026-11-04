using System.IO;
using System.Net.Http;
using Kitsune.Core;
using Kitsune.Core.Avd;
using Kitsune.Core.Emulator;
using Kitsune.Core.Host;
using Kitsune.Core.Input;
using Kitsune.Core.Sdk;
using Kitsune.Core.Settings;

namespace Kitsune.App.Services;

/// <summary>Process-wide services shared by the windows.</summary>
public sealed class AppState : IDisposable
{
    public KitsunePaths Paths { get; }
    public AppSettings Settings { get; }
    public HttpClient Http { get; }
    public ComponentManager Components { get; }
    public AvdManager Avds { get; }
    public AdbTools Adb { get; }
    public HostInfo Host { get; private set; }

    public AppState()
    {
        Paths = KitsunePaths.Resolve();
        Paths.EnsureDirectories();
        Settings = AppSettings.Load(Paths);
        Http = ComponentManager.CreateHttpClient();
        Components = new ComponentManager(Paths, Http);
        Avds = new AvdManager(Paths);
        Adb = new AdbTools(Paths);
        Host = HostProbe.Probe();
        SeedProfiles();
    }

    public void RefreshHost() => Host = HostProbe.Probe();

    public string? EmulatorVersion
    {
        get
        {
            var props = Path.Combine(Paths.EmulatorDir, "source.properties");
            if (!File.Exists(props)) return null;
            foreach (var line in File.ReadLines(props))
                if (line.StartsWith("Pkg.Revision=", StringComparison.Ordinal)) return line["Pkg.Revision=".Length..].Trim();
            return "?";
        }
    }

    /// <summary>Copies the bundled starter profiles into the user's profile folder when they are not there yet.</summary>
    private void SeedProfiles()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (!Directory.Exists(bundled)) return;
        foreach (var file in Directory.EnumerateFiles(bundled, "*.json"))
        {
            var dest = Path.Combine(Paths.Profiles, Path.GetFileName(file));
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
    }

    public IReadOnlyList<(string File, KeymapProfile Profile)> LoadProfiles()
    {
        var list = new List<(string, KeymapProfile)>();
        if (!Directory.Exists(Paths.Profiles)) return list;
        foreach (var file in Directory.EnumerateFiles(Paths.Profiles, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add((file, KeymapProfile.Load(file))); } catch { }
        }
        return list;
    }

    public string ProfilePath(string name)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (safe.Length == 0) safe = "profile";
        return Path.Combine(Paths.Profiles, safe + ".json");
    }

    public void Dispose() => Http.Dispose();
}
