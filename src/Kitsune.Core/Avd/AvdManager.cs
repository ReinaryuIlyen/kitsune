using System.Text;

namespace Kitsune.Core.Avd;

public sealed record Instance(string Name, string AvdDir, InstanceConfig Config)
{
    public string ConfigIni => Path.Combine(AvdDir, "config.ini");
    public string KitsuneJson => Path.Combine(AvdDir, "kitsune.json");
}

/// <summary>
/// Creates and maintains AVDs in Kitsune's private AVD home. The on-disk layout is exactly what the
/// emulator expects (name.ini + name.avd/config.ini) so the stock emulator tooling keeps working.
/// </summary>
public sealed class AvdManager
{
    private readonly KitsunePaths _paths;
    public AvdManager(KitsunePaths paths) => _paths = paths;

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    public IReadOnlyList<Instance> List()
    {
        var list = new List<Instance>();
        if (!Directory.Exists(_paths.AvdHome)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_paths.AvdHome, "*.avd"))
        {
            var json = Path.Combine(dir, "kitsune.json");
            if (!File.Exists(json)) continue;
            try
            {
                var cfg = InstanceConfig.Load(json);
                list.Add(new Instance(cfg.Name, dir, cfg));
            }
            catch { /* skip unreadable instance */ }
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Instance? Get(string name) => List().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public Instance Create(InstanceConfig cfg)
    {
        if (!IsValidName(cfg.Name)) throw new ArgumentException("Instance names may contain letters, digits, '_', '-' and '.' only.");
        var image = cfg.Image;
        var sysdir = _paths.SystemImageDir(image);
        if (!Directory.Exists(sysdir)) throw new DirectoryNotFoundException($"System image {image} is not installed ({sysdir}).");

        var avdDir = Path.Combine(_paths.AvdHome, cfg.Name + ".avd");
        if (Directory.Exists(avdDir)) throw new IOException($"Instance '{cfg.Name}' already exists.");
        Directory.CreateDirectory(avdDir);

        // Seed userdata from the image when it ships one (newer images let the emulator create it).
        var userdata = Path.Combine(sysdir, "userdata.img");
        if (File.Exists(userdata)) File.Copy(userdata, Path.Combine(avdDir, "userdata.img"));

        var inst = new Instance(cfg.Name, avdDir, cfg);
        WriteConfig(inst);
        return inst;
    }

    /// <summary>Rewrites config.ini and kitsune.json from the instance configuration. Safe to call on an existing instance.</summary>
    public void WriteConfig(Instance inst)
    {
        var cfg = inst.Config;
        var image = cfg.Image;
        cfg.Save(inst.KitsuneJson);
        File.WriteAllText(inst.ConfigIni, BuildConfigIni(cfg, image), new UTF8Encoding(false));

        var pointer = new StringBuilder()
            .Append("avd.ini.encoding=UTF-8\n")
            .Append("path=").Append(inst.AvdDir).Append('\n')
            .Append("path.rel=avd/").Append(cfg.Name).Append(".avd\n")
            .Append("target=android-").Append(image.ApiLevel).Append('\n');
        File.WriteAllText(Path.Combine(_paths.AvdHome, cfg.Name + ".ini"), pointer.ToString(), new UTF8Encoding(false));
    }

    public void Delete(Instance inst)
    {
        if (Directory.Exists(inst.AvdDir)) Directory.Delete(inst.AvdDir, true);
        var ini = Path.Combine(_paths.AvdHome, inst.Name + ".ini");
        if (File.Exists(ini)) File.Delete(ini);
    }

    /// <summary>Clones an instance including its user data (installed games, accounts). Snapshots are not copied.</summary>
    public Instance Clone(Instance source, string newName)
    {
        if (!IsValidName(newName)) throw new ArgumentException("Invalid instance name.");
        var dest = Path.Combine(_paths.AvdHome, newName + ".avd");
        if (Directory.Exists(dest)) throw new IOException($"Instance '{newName}' already exists.");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source.AvdDir))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.StartsWith("snapshot", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dest, name));
        }
        var cfg = InstanceConfig.Load(source.KitsuneJson);
        cfg.Name = newName;
        var inst = new Instance(newName, dest, cfg);
        WriteConfig(inst);
        return inst;
    }

    /// <summary>
    /// Builds the AVD hardware configuration. Keys follow the emulator's hardware-properties.ini.
    /// Choices that matter for games: real cores and RAM, GPU on, no cameras or microphone,
    /// a large data partition, no device frame, and the LCD geometry from the device profile.
    /// </summary>
    public static string BuildConfigIni(InstanceConfig cfg, SystemImageRef image)
    {
        var sb = new StringBuilder();
        void Add(string k, string v) => sb.Append(k).Append('=').Append(v).Append('\n');

        Add("AvdId", cfg.Name);
        Add("avd.ini.displayname", cfg.Name);
        Add("avd.ini.encoding", "UTF-8");
        Add("abi.type", image.Abi);
        Add("hw.cpu.arch", image.Abi);
        Add("hw.cpu.ncore", Math.Clamp(cfg.Cores, 1, Defaults.MaxCores).ToString());
        Add("hw.ramSize", cfg.RamMb.ToString());
        Add("vm.heapSize", cfg.VmHeapMb.ToString());
        Add("disk.dataPartition.size", $"{cfg.DataPartitionGb}G");
        Add("image.sysdir.1", image.SysDir);
        Add("tag.id", image.Tag);
        Add("tag.display", image.HasPlayStore ? "Google Play" : "Google APIs");
        Add("PlayStore.enabled", image.HasPlayStore ? "true" : "false");
        Add("hw.device.manufacturer", "Google");
        Add("hw.device.name", cfg.Width > cfg.Height ? "pixel_tablet" : "pixel_8");
        Add("hw.lcd.width", cfg.Width.ToString());
        Add("hw.lcd.height", cfg.Height.ToString());
        Add("hw.lcd.density", cfg.Density.ToString());
        Add("hw.lcd.depth", "32");
        Add("hw.lcd.vsync", cfg.RefreshRate.ToString());
        Add("hw.initialOrientation", cfg.Width > cfg.Height ? "landscape" : "portrait");
        Add("hw.gpu.enabled", "yes");
        // hw.gpu.mode only knows auto/host/software; ANGLE and SwiftShader are selected with -gpu on the command line.
        Add("hw.gpu.mode", cfg.Gpu == GpuMode.Host ? "host" : "auto");
        Add("hw.keyboard", "yes");
        Add("hw.keyboard.lid", "no");
        Add("hw.mainKeys", "no");
        Add("hw.dPad", "no");
        Add("hw.trackBall", "no");
        Add("hw.camera.back", cfg.Camera ? "virtualscene" : "none");
        Add("hw.camera.front", cfg.Camera ? "emulated" : "none");
        Add("hw.audioInput", "no");
        Add("hw.audioOutput", cfg.Audio ? "yes" : "no");
        Add("hw.accelerometer", "yes");
        Add("hw.gyroscope", "yes");
        Add("hw.sensors.orientation", "yes");
        Add("hw.sensors.proximity", "no");
        Add("hw.sensors.light", "no");
        Add("hw.sensors.pressure", "no");
        Add("hw.sensors.humidity", "no");
        Add("hw.sensors.magnetic_field", "yes");
        Add("hw.battery", "yes");
        Add("hw.gps", cfg.Gps ? "yes" : "no");
        Add("hw.sdCard", "no");
        Add("hw.arc", "false");
        Add("skin.name", $"{cfg.Width}x{cfg.Height}");
        Add("skin.path", "_no_skin");
        Add("skin.dynamic", "yes");
        Add("showDeviceFrame", "no");
        Add("fastboot.forceColdBoot", cfg.Quickboot ? "no" : "yes");
        Add("runtime.network.latency", "none");
        Add("runtime.network.speed", "full");
        return sb.ToString();
    }
}
