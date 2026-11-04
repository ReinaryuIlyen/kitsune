using System.Text;
using Kitsune.Core.Avd;
using Kitsune.Core.Host;

namespace Kitsune.Core.Reroll;

/// <summary>
/// Builds and maintains reroll sets on disk.
///
/// The emulator keeps three writable disks per AVD, each as a qcow2 overlay (<c>name.qcow2</c>) over a
/// base file of the same name beside it (<c>userdata-qemu.img</c>, which the emulator itself writes in
/// qcow2 format, plus raw <c>cache.img</c> and <c>encryptionkey.img</c>). It recreates any overlay whose
/// backing file is not exactly that sibling name, so a reroll set reproduces the native layout:
/// the template's current disks are flattened once into <c>base/</c>, every clone folder gets NTFS hard
/// links under the expected names pointing at those base files, and an empty overlay backed by the link.
/// Clones then boot from the template's state while writing only to their own overlays; the base files
/// are marked read-only so nothing can truncate them through a link.
/// </summary>
public sealed class RerollManager
{
    private sealed record Disk(string Name, string Format);

    private static readonly Disk[] Disks =
    [
        new("userdata-qemu.img", "qcow2"),
        new("cache.img", "raw"),
        new("encryptionkey.img", "raw"),
    ];

    private readonly KitsunePaths _paths;
    private readonly QemuImg _qemuImg;
    private readonly AvdManager _avds;

    public RerollManager(KitsunePaths paths)
    {
        _paths = paths;
        _qemuImg = new QemuImg(paths);
        _avds = new AvdManager(paths);
    }

    public static bool IsValidName(string name) => AvdManager.IsValidName(name) && !name.Contains('.');

    public IReadOnlyList<RerollSet> List()
    {
        var list = new List<RerollSet>();
        if (!Directory.Exists(_paths.RerollDir)) return list;
        foreach (var dir in Directory.EnumerateDirectories(_paths.RerollDir))
        {
            if (!File.Exists(Path.Combine(dir, "reroll.json"))) continue;
            try { list.Add(RerollSet.Load(dir)); } catch { }
        }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RerollSet? Get(string name) => List().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when an emulator currently holds this AVD folder. The emulator leaves its lock files behind
    /// after a clean exit, so existence alone means nothing: a live emulator keeps the lock open, which
    /// makes an exclusive open fail; a stale lock opens fine. Lock directories carry a pid file instead.
    /// </summary>
    public static bool IsAvdRunning(string avdDir)
    {
        foreach (var name in new[] { "multiinstance.lock", "hardware-qemu.ini.lock" })
        {
            var path = Path.Combine(avdDir, name);
            if (Directory.Exists(path))
            {
                var pidFile = Path.Combine(path, "pid");
                if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                {
                    try { using var p = System.Diagnostics.Process.GetProcessById(pid); if (!p.HasExited) return true; }
                    catch (ArgumentException) { }
                }
                continue;
            }
            if (!File.Exists(path)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Drops any snapshot folder left in a clone that runs without snapshots (for example from a run
    /// before the clone was launched with file-backed RAM disabled). A RAM-sized file per clone otherwise.
    /// </summary>
    public static void TrimSnapshots(RerollSet set, RerollClone clone)
    {
        if (set.SaveSnapshots) return;
        var dir = Path.Combine(set.CloneDir(clone), "snapshots");
        if (!Directory.Exists(dir) || IsAvdRunning(set.CloneDir(clone))) return;
        try { DeleteTree(dir); } catch { }
    }

    /// <summary>Bytes a clone really owns: its overlays and snapshots, not the hard-linked base disks.</summary>
    public static long CloneOwnedBytes(string cloneDir)
    {
        if (!Directory.Exists(cloneDir)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(cloneDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name is "userdata-qemu.img" or "cache.img" or "encryptionkey.img") continue;
            total += new FileInfo(f).Length;
        }
        return total;
    }

    /// <summary>
    /// How many more clones the host can take right now: whichever is smaller of total RAM minus 6 GB
    /// for the host and GPU translation, and RAM actually free at this moment minus 2 GB of headroom.
    /// Clones run with plain (not file-backed) guest memory, so this is a real ceiling, not a hint.
    /// </summary>
    public static int MaxClonesFor(HostInfo host, int ramMbPerClone)
    {
        long perClone = Math.Max(1024, ramMbPerClone);
        long byTotal = (host.TotalRamBytes / 1048576 - 6144) / perClone;
        long byFree = host.AvailableRamBytes > 0 ? (host.AvailableRamBytes / 1048576 - 2048) / perClone : byTotal;
        return (int)Math.Clamp(Math.Min(byTotal, byFree), 0, 40);
    }

    public async Task<RerollSet> CreateAsync(string name, Instance template, int count, int ramMb, int cores, bool saveSnapshots,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsValidName(name)) throw new ArgumentException("Set names may contain letters, digits, '_' and '-' only.");
        if (count < 1 || count > 40) throw new ArgumentException("Clone count must be between 1 and 40.");
        if (IsAvdRunning(template.AvdDir)) throw new InvalidOperationException($"Stop '{template.Name}' before freezing it as a reroll base.");
        var dir = Path.Combine(_paths.RerollDir, name);
        if (Directory.Exists(dir)) throw new IOException($"Reroll set '{name}' already exists.");
        foreach (var d in Disks)
            if (!File.Exists(Path.Combine(template.AvdDir, d.Name + ".qcow2")))
                throw new InvalidOperationException($"'{template.Name}' has not been booted yet ({d.Name}.qcow2 is missing). Boot it once, set the game up, then stop it.");

        var set = new RerollSet
        {
            Name = name, Template = template.Name, SystemImage = template.Config.SystemImage,
            RamMb = ramMb, Cores = Math.Clamp(cores, 1, Defaults.MaxCores), SaveSnapshots = saveSnapshots,
            CreatedAt = DateTimeOffset.UtcNow, Dir = dir,
        };
        Directory.CreateDirectory(set.BaseDir);
        try
        {
            foreach (var d in Disks)
            {
                progress?.Report($"Freezing {d.Name} from {template.Name}...");
                var dest = Path.Combine(set.BaseDir, d.Name);
                await _qemuImg.ConvertAsync(Path.Combine(template.AvdDir, d.Name + ".qcow2"), dest, d.Format, ct).ConfigureAwait(false);
                File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.ReadOnly);
            }
            File.Copy(template.ConfigIni, Path.Combine(set.BaseDir, "config.ini"));
            template.Config.Save(Path.Combine(set.BaseDir, "kitsune.json"));
            set.Save();
            for (int i = 1; i <= count; i++)
            {
                progress?.Report($"Creating clone {i} of {count}...");
                await AddCloneAsync(set, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            try { DeleteTree(dir); } catch { }
            throw;
        }
        return set;
    }

    public async Task<RerollClone> AddCloneAsync(RerollSet set, CancellationToken ct = default)
    {
        int index = set.Clones.Count == 0 ? 1 : set.Clones.Max(c => c.Index) + 1;
        var clone = new RerollClone { Index = index, Name = $"{set.Name}-{index:00}", LastReset = DateTimeOffset.UtcNow };
        var dir = set.CloneDir(clone);
        Directory.CreateDirectory(dir);
        await WriteDisksAsync(set, clone, ct).ConfigureAwait(false);
        WriteCloneConfig(set, clone);
        set.Clones.Add(clone);
        set.Save();
        return clone;
    }

    /// <summary>Discards everything the clone wrote: its overlays are recreated empty, so the next boot is the template again.</summary>
    public async Task ResetAsync(RerollSet set, RerollClone clone, CancellationToken ct = default)
    {
        var dir = set.CloneDir(clone);
        if (IsAvdRunning(dir)) throw new InvalidOperationException($"Stop {clone.Name} before resetting it.");
        // Subfolders first (the emulator leaves read-only files under data/), then the top-level files.
        foreach (var d in Directory.EnumerateDirectories(dir).ToList()) DeleteTree(d);
        foreach (var f in Directory.EnumerateFiles(dir).ToList())
        {
            var n = Path.GetFileName(f);
            if (n is "config.ini" or "kitsune.json") continue;
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        await WriteDisksAsync(set, clone, ct).ConfigureAwait(false);
        clone.LastReset = DateTimeOffset.UtcNow;
        set.Save();
    }

    /// <summary>
    /// Keeps a winning clone: its overlay chain is flattened into standalone disks inside a brand-new
    /// normal instance, laid out exactly as the emulator would have written them. The clone itself stays
    /// in the set and can be reset for the next run.
    /// </summary>
    public async Task<Instance> KeepAsync(RerollSet set, RerollClone clone, string newInstanceName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!AvdManager.IsValidName(newInstanceName)) throw new ArgumentException("Invalid instance name.");
        var cloneDir = set.CloneDir(clone);
        if (IsAvdRunning(cloneDir)) throw new InvalidOperationException($"Stop {clone.Name} before keeping it; the emulator flushes its disks on shutdown.");
        var dest = Path.Combine(_paths.AvdHome, newInstanceName + ".avd");
        if (Directory.Exists(dest)) throw new IOException($"Instance '{newInstanceName}' already exists.");
        Directory.CreateDirectory(dest);
        try
        {
            foreach (var d in Disks)
            {
                progress?.Report($"Flattening {d.Name}...");
                var basePath = Path.Combine(dest, d.Name);
                await _qemuImg.ConvertAsync(Path.Combine(cloneDir, d.Name + ".qcow2"), basePath, d.Format, ct).ConfigureAwait(false);
                await _qemuImg.CreateOverlayAsync(basePath + ".qcow2", d.Name, d.Format, ct).ConfigureAwait(false);
            }
            var cfg = InstanceConfig.Load(Path.Combine(set.BaseDir, "kitsune.json"));
            cfg.Name = newInstanceName;
            var inst = new Instance(newInstanceName, dest, cfg);
            _avds.WriteConfig(inst);
            return inst;
        }
        catch
        {
            try { Directory.Delete(dest, true); } catch { }
            throw;
        }
    }

    public void Delete(RerollSet set)
    {
        foreach (var c in set.Clones)
            if (IsAvdRunning(set.CloneDir(c))) throw new InvalidOperationException($"Stop {c.Name} before deleting the set.");
        foreach (var c in set.Clones)
        {
            var ini = Path.Combine(_paths.AvdHome, c.Name + ".ini");
            if (File.Exists(ini)) File.Delete(ini);
        }
        DeleteTree(set.Dir);
    }

    public Task RemoveCloneAsync(RerollSet set, RerollClone clone)
    {
        var dir = set.CloneDir(clone);
        if (IsAvdRunning(dir)) throw new InvalidOperationException($"Stop {clone.Name} before removing it.");
        if (Directory.Exists(dir)) DeleteTree(dir);
        var ini = Path.Combine(_paths.AvdHome, clone.Name + ".ini");
        if (File.Exists(ini)) File.Delete(ini);
        set.Clones.Remove(clone);
        set.Save();
        return Task.CompletedTask;
    }

    /// <summary>The clone as a launchable instance: the template's configuration with the set's RAM and core budget.</summary>
    public Instance CloneInstance(RerollSet set, RerollClone clone)
    {
        var cfg = InstanceConfig.Load(Path.Combine(set.BaseDir, "kitsune.json"));
        cfg.Name = clone.Name;
        cfg.RamMb = set.RamMb;
        cfg.Cores = set.Cores;
        cfg.Quickboot = set.SaveSnapshots;
        return new Instance(clone.Name, set.CloneDir(clone), cfg);
    }

    private async Task WriteDisksAsync(RerollSet set, RerollClone clone, CancellationToken ct)
    {
        var dir = set.CloneDir(clone);
        foreach (var d in Disks)
        {
            var link = Path.Combine(dir, d.Name);
            QemuImg.LinkOrCopy(Path.Combine(set.BaseDir, d.Name), link);
            var overlay = link + ".qcow2";
            if (File.Exists(overlay)) File.Delete(overlay);
            await _qemuImg.CreateOverlayAsync(overlay, d.Name, d.Format, ct).ConfigureAwait(false);
        }
    }

    private void WriteCloneConfig(RerollSet set, RerollClone clone)
    {
        var dir = set.CloneDir(clone);
        var cfg = InstanceConfig.Load(Path.Combine(set.BaseDir, "kitsune.json"));
        cfg.Name = clone.Name;
        cfg.RamMb = set.RamMb;
        cfg.Cores = set.Cores;
        cfg.Quickboot = set.SaveSnapshots;
        cfg.Save(Path.Combine(dir, "kitsune.json"));
        File.WriteAllText(Path.Combine(dir, "config.ini"), AvdManager.BuildConfigIni(cfg, cfg.Image), new UTF8Encoding(false));

        var pointer = new StringBuilder()
            .Append("avd.ini.encoding=UTF-8\n")
            .Append("path=").Append(dir).Append('\n')
            .Append("path.rel=avd/reroll/").Append(set.Name).Append('/').Append(clone.Name).Append(".avd\n")
            .Append("target=android-").Append(cfg.Image.ApiLevel).Append('\n');
        File.WriteAllText(Path.Combine(_paths.AvdHome, clone.Name + ".ini"), pointer.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Deletes a tree that may contain read-only base files, hard links to them, and read-only emulator state.</summary>
    private static void DeleteTree(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(d, FileAttributes.Normal);
        try { Directory.Delete(dir, true); }
        catch (IOException)
        {
            // Windows occasionally refuses while a handle is closing; one retry after a short pause covers it.
            Thread.Sleep(300);
            Directory.Delete(dir, true);
        }
    }
}
