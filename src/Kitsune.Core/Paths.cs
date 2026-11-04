namespace Kitsune.Core;

/// <summary>
/// Resolves every directory Kitsune uses. Nothing is written outside <see cref="Root"/>.
/// Resolution order: KITSUNE_HOME environment variable, then a "kitsune.portable" marker
/// next to the executable (portable mode: everything lives beside the exe), then
/// %LOCALAPPDATA%\Kitsune.
/// </summary>
public sealed class KitsunePaths
{
    public string Root { get; }
    public string Sdk => Path.Combine(Root, "sdk");
    public string EmulatorDir => Path.Combine(Sdk, "emulator");
    public string EmulatorExe => Path.Combine(EmulatorDir, "emulator.exe");
    public string QemuExe => Path.Combine(EmulatorDir, "qemu", "windows-x86_64", "qemu-system-x86_64.exe");
    public string PlatformToolsDir => Path.Combine(Sdk, "platform-tools");
    public string AdbExe => Path.Combine(PlatformToolsDir, "adb.exe");
    public string SystemImagesDir => Path.Combine(Sdk, "system-images");
    public string AehdDir => Path.Combine(Sdk, "extras", "google", "Android_Emulator_Hypervisor_Driver");
    public string AvdHome => Path.Combine(Root, "avd");
    /// <summary>Reroll sets: a frozen base image plus copy-on-write clones, each a tiny AVD of its own.</summary>
    public string RerollDir => Path.Combine(AvdHome, "reroll");
    public string QemuImgExe => Path.Combine(EmulatorDir, "qemu-img.exe");
    /// <summary>ANDROID_EMULATOR_HOME: where the emulator keeps its own per-user files. Kept inside Root so nothing leaks into ~/.android.</summary>
    public string EmulatorHome => Path.Combine(Root, "emulator-home");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Logs => Path.Combine(Root, "logs");
    public string Profiles => Path.Combine(Root, "profiles");
    public string Macros => Path.Combine(Root, "macros");
    public string Screenshots => Path.Combine(Root, "screenshots");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string InstalledFile => Path.Combine(Sdk, "kitsune-installed.json");

    public KitsunePaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public static KitsunePaths Resolve()
    {
        var env = Environment.GetEnvironmentVariable("KITSUNE_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return new KitsunePaths(env);

        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "kitsune.portable")))
            return new KitsunePaths(exeDir);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new KitsunePaths(Path.Combine(local, "Kitsune"));
    }

    public void EnsureDirectories()
    {
        foreach (var d in new[] { Root, Sdk, AvdHome, EmulatorHome, Downloads, Logs, Profiles, Macros, Screenshots })
            Directory.CreateDirectory(d);
    }

    public string SystemImageDir(SystemImageRef image) =>
        Path.Combine(SystemImagesDir, $"android-{image.ApiLevel}", image.Tag, image.Abi);
}

/// <summary>Identifies a system image the way the SDK repository does: system-images;android-35;google_apis_playstore;x86_64.</summary>
public readonly record struct SystemImageRef(string ApiLevel, string Tag, string Abi)
{
    public string PackagePath => $"system-images;android-{ApiLevel};{Tag};{Abi}";
    public bool HasPlayStore => Tag.StartsWith("google_apis_playstore", StringComparison.Ordinal);
    /// <summary>Relative path the emulator expects in config.ini image.sysdir.1 (trailing slash included).</summary>
    public string SysDir => $"system-images/android-{ApiLevel}/{Tag}/{Abi}/";
    public override string ToString() => PackagePath;

    public static SystemImageRef Parse(string packagePath)
    {
        var parts = packagePath.Split(';');
        if (parts.Length != 4 || parts[0] != "system-images" || !parts[1].StartsWith("android-", StringComparison.Ordinal))
            throw new FormatException($"Not a system image package path: {packagePath}");
        return new SystemImageRef(parts[1]["android-".Length..], parts[2], parts[3]);
    }
}
