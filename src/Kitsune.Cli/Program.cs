using Kitsune.Core;
using Kitsune.Core.Avd;
using Kitsune.Core.Emulator;
using Kitsune.Core.Host;
using Kitsune.Core.Input;
using Kitsune.Core.Reroll;
using Kitsune.Core.Sdk;

namespace Kitsune.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        var rest = args.Skip(1).ToArray();
        try
        {
            return args[0] switch
            {
                "probe" => Probe(),
                "catalog" => await Catalog(),
                "install" => await Install(rest),
                "aehd" => await Aehd(rest),
                "list" => List(),
                "create" => Create(rest),
                "delete" => Delete(rest),
                "plan" => Plan(rest),
                "launch" => await Launch(rest),
                "ctl" => await Ctl(rest),
                "reroll" => await Reroll(rest),
                "keymap" => Keymap(rest),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        Usage();
        return 1;
    }

    private static void Usage()
    {
        Console.WriteLine("kitsune <command> [options]");
        Console.WriteLine("  probe                               Host CPU, hypervisor, GPU and accelerator recommendation");
        Console.WriteLine("  catalog                             Components available in the SDK repository");
        Console.WriteLine("  install [--api N] [--variant playstore|googleapis] [--no-image]");
        Console.WriteLine("                                      Install or update emulator, adb, AEHD package and a system image");
        Console.WriteLine("  aehd status|install|uninstall       Manage the Android Emulator Hypervisor Driver (install/uninstall prompt for UAC)");
        Console.WriteLine("  list                                Instances");
        Console.WriteLine("  create <name> [--api N] [--variant playstore|googleapis] [--profile ID] [--cores N] [--ram MB]");
        Console.WriteLine("  delete <name>");
        Console.WriteLine("  plan <name>                         Print the exact emulator command line and environment");
        Console.WriteLine("  launch <name> [--cold] [--wipe] [--headless] [--wait] [--attach]");
        Console.WriteLine("                                      --wait blocks until the guest has booted; --attach keeps running until the emulator exits");
        Console.WriteLine("  ctl <grpc-port> status|displays|screenshot <file.png>|tap X Y|swipe X0 Y0 X1 Y1 [ms]|key <AndroidKey>|text <string>|shutdown");
        Console.WriteLine("                                      Talk to a running instance over its gRPC control port");
        Console.WriteLine("  keymap validate <file.json>");
        Console.WriteLine("  keymap selftest                     Exercise every gesture type against a recording sink");
        Console.WriteLine("  reroll create <set> --from <instance> [--count N] [--ram MB] [--cores N] [--snapshots]");
        Console.WriteLine("                                      Freeze the instance's disks and create N copy-on-write clones");
        Console.WriteLine("  reroll list | add <set> [N] | reset <set> [clone#] | remove <set> <clone#> | delete <set>");
        Console.WriteLine("  reroll keep <set> <clone#> <new-instance>   Flatten a winning clone into a normal instance");
        Console.WriteLine("  reroll launch <set> [--count N] [--stagger ms] [--wait] [--attach]");
        Console.WriteLine("                                      Boot the clones in parallel; --attach keeps them until Ctrl+C");
        Console.WriteLine();
        Console.WriteLine("Device profiles:");
        foreach (var p in DeviceProfile.All) Console.WriteLine($"  {p.Id,-14} {p.DisplayName}");
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Flag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static string Positional(string[] args, int index, string what)
    {
        var positional = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--"))).ToArray();
        return positional.Length > index ? positional[index] : throw new ArgumentException($"Missing {what}.");
    }

    private static int Probe()
    {
        var h = HostProbe.Probe();
        Console.WriteLine($"CPU:            {h.CpuName} ({h.CpuVendor})");
        Console.WriteLine($"Cores:          {h.PhysicalCores} physical, {h.PerformanceCores} performance, {h.LogicalProcessors} logical");
        Console.WriteLine($"RAM:            {h.TotalRamBytes / (1024.0 * 1024 * 1024):F1} GB");
        Console.WriteLine($"VT-x/AMD-V:     {(h.HardwareVirtualization ? "yes" : "no")}");
        Console.WriteLine($"Hypervisor:     {(h.HypervisorPresent ? $"present ({h.HypervisorVendor})" : "none")}");
        Console.WriteLine($"WHPX:           {(h.WhpxAvailable ? "available" : "unavailable")}");
        Console.WriteLine($"AEHD service:   {h.Aehd}");
        Console.WriteLine($"Recommended:    {h.Recommended}");
        foreach (var g in h.Gpus)
            Console.WriteLine($"GPU:            {g.Name} [{g.Vendor}{(g.IsDiscrete ? ", discrete" : "")}] driver {g.DriverVersion}");
        foreach (var i in h.VulkanIcds)
            Console.WriteLine($"Vulkan ICD:     {i.Path} [{i.Vendor}]");
        Console.WriteLine($"Preferred GPU:  {h.PreferredGpu?.Name ?? "n/a"}");
        Console.WriteLine($"Preferred ICD:  {h.PreferredVulkanIcd?.Path ?? "n/a"}");
        Console.WriteLine($"Kitsune root:   {KitsunePaths.Resolve().Root}");
        return 0;
    }

    private static async Task<int> Catalog()
    {
        var paths = KitsunePaths.Resolve();
        using var http = ComponentManager.CreateHttpClient();
        var cm = new ComponentManager(paths, http);
        var catalog = await cm.CatalogAsync();
        foreach (var p in new[] { PackageSelector.Emulator(catalog), PackageSelector.PlatformTools(catalog), PackageSelector.Aehd(catalog) })
            if (p is not null) Console.WriteLine($"{p.Path,-60} {p.Version,-10} {p.WindowsArchive!.Size / 1048576.0,8:F1} MB  {(cm.Installer.IsInstalled(p) ? "installed" : "")}");
        foreach (var p in PackageSelector.GamingSystemImages(catalog))
            Console.WriteLine($"{p.Path,-60} r{p.Version.Major,-9} {p.WindowsArchive!.Size / 1048576.0,8:F1} MB  {(cm.Installer.IsInstalled(p) ? "installed" : "")}");
        return 0;
    }

    private static async Task<int> Install(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        paths.EnsureDirectories();
        using var http = ComponentManager.CreateHttpClient();
        var cm = new ComponentManager(paths, http);
        var progress = new ConsoleProgress();

        await cm.InstallCoreAsync(progress, CancellationToken.None);
        if (!Flag(args, "--no-image"))
        {
            var api = Opt(args, "--api") ?? "35";
            var tag = (Opt(args, "--variant") ?? "playstore") == "googleapis" ? "google_apis" : "google_apis_playstore";
            await cm.InstallImageAsync(new SystemImageRef(api, tag, "x86_64"), progress, CancellationToken.None);
        }
        progress.Finish();
        Console.WriteLine($"Installed into {paths.Sdk}");
        return 0;
    }

    private static async Task<int> Aehd(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        var sub = args.Length > 0 ? args[0] : "status";
        switch (sub)
        {
            case "status":
                Console.WriteLine($"AEHD package: {(AehdInstaller.PackagePresent(paths) ? "present" : "missing")}");
                Console.WriteLine($"AEHD service: {HostProbe.AehdServiceState()}");
                return 0;
            case "install":
            {
                var (ok, output) = await AehdInstaller.InstallAsync(paths);
                if (output.Length > 0) Console.WriteLine(output);
                Console.WriteLine(ok ? "AEHD is installed and running." : "AEHD install did not complete.");
                return ok ? 0 : 3;
            }
            case "uninstall":
            {
                var (ok, output) = await AehdInstaller.UninstallAsync(paths);
                if (output.Length > 0) Console.WriteLine(output);
                Console.WriteLine(ok ? "AEHD removed." : "AEHD uninstall did not complete.");
                return ok ? 0 : 3;
            }
            default:
                return Unknown("aehd " + sub);
        }
    }

    private static int List()
    {
        var paths = KitsunePaths.Resolve();
        var avd = new AvdManager(paths);
        var list = avd.List();
        if (list.Count == 0) { Console.WriteLine("No instances. Create one with: kitsune create <name>"); return 0; }
        foreach (var i in list)
        {
            var c = i.Config;
            Console.WriteLine($"{i.Name,-20} {c.SystemImage,-55} {c.Width}x{c.Height}@{c.Density}  {c.Cores} cores  {c.RamMb} MB  gpu={c.Gpu}{(c.Vulkan ? "+vk" : "")}");
        }
        return 0;
    }

    private static int Create(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        paths.EnsureDirectories();
        var name = Positional(args, 0, "instance name");
        var host = HostProbe.Probe();
        var api = Opt(args, "--api") ?? "35";
        var tag = (Opt(args, "--variant") ?? "playstore") == "googleapis" ? "google_apis" : "google_apis_playstore";
        var profile = DeviceProfile.Find(Opt(args, "--profile") ?? DeviceProfile.Phone1080p2400.Id)
            ?? throw new ArgumentException("Unknown device profile.");

        var cfg = new InstanceConfig
        {
            Name = name,
            SystemImage = new SystemImageRef(api, tag, "x86_64").PackagePath,
            Cores = int.TryParse(Opt(args, "--cores"), out var c) ? c : Defaults.Cores(host),
            RamMb = int.TryParse(Opt(args, "--ram"), out var r) ? r : Defaults.RamMb(host),
        };
        cfg.ApplyProfile(profile);
        var inst = new AvdManager(paths).Create(cfg);
        Console.WriteLine($"Created {inst.Name} at {inst.AvdDir}");
        Console.WriteLine($"  image   {cfg.SystemImage}");
        Console.WriteLine($"  screen  {cfg.Width}x{cfg.Height} @ {cfg.Density} dpi, {cfg.RefreshRate} Hz");
        Console.WriteLine($"  cpu/ram {cfg.Cores} cores, {cfg.RamMb} MB");
        return 0;
    }

    private static int Delete(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        var name = Positional(args, 0, "instance name");
        var avd = new AvdManager(paths);
        var inst = avd.Get(name) ?? throw new ArgumentException($"No instance named {name}.");
        avd.Delete(inst);
        Console.WriteLine($"Deleted {name}.");
        return 0;
    }

    private static int Plan(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        var name = Positional(args, 0, "instance name");
        var inst = new AvdManager(paths).Get(name) ?? throw new ArgumentException($"No instance named {name}.");
        var host = HostProbe.Probe();
        var plan = new EmulatorLauncher(paths).BuildPlan(inst, host);
        foreach (var kv in plan.Environment) Console.WriteLine($"set {kv.Key}={kv.Value}");
        Console.WriteLine($"\"{plan.Executable}\" {string.Join(' ', plan.Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        Console.WriteLine($"# accelerator={plan.Accelerator} console={plan.ConsolePort} adb={plan.ConsolePort + 1} grpc={plan.GrpcPort}");
        return 0;
    }

    private static async Task<int> Launch(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        var name = Positional(args, 0, "instance name");
        var inst = new AvdManager(paths).Get(name) ?? throw new ArgumentException($"No instance named {name}.");
        var host = HostProbe.Probe();
        var launcher = new EmulatorLauncher(paths);
        var options = new LaunchOptions(ColdBoot: Flag(args, "--cold"), WipeData: Flag(args, "--wipe"), Headless: Flag(args, "--headless"), CaptureOutput: Flag(args, "--attach"));
        var plan = launcher.BuildPlan(inst, host, options);
        var running = launcher.Launch(inst, host, options with { ConsolePort = plan.ConsolePort, GrpcPort = plan.GrpcPort });
        Console.WriteLine($"Started {name}: pid {running.Process.Id}, console {running.ConsolePort}, adb {running.AdbSerial}, grpc {running.GrpcPort}, accel {plan.Accelerator}");
        Console.WriteLine($"Log: {running.LogFile}");

        if (Flag(args, "--wait") || Flag(args, "--attach"))
        {
            await using var control = new EmulatorControl(running.GrpcPort);
            var started = DateTime.UtcNow;
            var status = await control.WaitForBootAsync(TimeSpan.FromMinutes(5));
            Console.WriteLine($"Booted in {(DateTime.UtcNow - started).TotalSeconds:F1}s: emulator {status.Version}, hypervisor {status.VmConfig.HypervisorType}, {status.VmConfig.NumberOfCpuCores} cores, {status.VmConfig.RamSizeBytes / 1048576} MB");
            var displays = await control.GetDisplaysAsync();
            foreach (var d in displays.Displays) Console.WriteLine($"Display {d.Display}: {d.Width}x{d.Height} @ {d.Dpi} dpi");
        }
        if (Flag(args, "--attach"))
        {
            Console.WriteLine("Attached; the emulator's output goes to the log file. Ctrl+C stops the emulator.");
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; EmulatorLauncher.KillTree(running); };
            await running.Process.WaitForExitAsync();
            Console.WriteLine($"Emulator exited with code {running.Process.ExitCode}.");
        }
        return 0;
    }

    private static async Task<int> Ctl(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var port)) { Usage(); return 1; }
        await using var control = new EmulatorControl(port);
        var a = args.Skip(2).ToArray();
        switch (args[1])
        {
            case "status":
            {
                var s = await control.GetStatusAsync();
                Console.WriteLine($"version   {s.Version}");
                Console.WriteLine($"booted    {s.Booted}");
                Console.WriteLine($"uptime    {s.Uptime / 1000.0:F1}s");
                Console.WriteLine($"hypervisor {s.VmConfig.HypervisorType}, {s.VmConfig.NumberOfCpuCores} cores, {s.VmConfig.RamSizeBytes / 1048576} MB");
                foreach (var e in s.HardwareConfig.Entry.Where(e => e.Key.StartsWith("hw.gpu") || e.Key.StartsWith("hw.lcd") || e.Key.StartsWith("hw.cpu") || e.Key == "hw.ramSize"))
                    Console.WriteLine($"  {e.Key} = {e.Value}");
                return 0;
            }
            case "displays":
            {
                var d = await control.GetDisplaysAsync();
                foreach (var x in d.Displays) Console.WriteLine($"display {x.Display}: {x.Width}x{x.Height} @ {x.Dpi} dpi");
                return 0;
            }
            case "screenshot":
            {
                var png = await control.ScreenshotPngAsync();
                await File.WriteAllBytesAsync(a[0], png);
                Console.WriteLine($"{png.Length} bytes -> {a[0]}");
                return 0;
            }
            case "tap":
            {
                int x = int.Parse(a[0]), y = int.Parse(a[1]);
                control.SendTouch(1, x, y, true);
                await Task.Delay(60);
                control.SendTouch(1, x, y, false);
                await Task.Delay(100);
                Console.WriteLine($"tapped ({x},{y})");
                return 0;
            }
            case "swipe":
            {
                int x0 = int.Parse(a[0]), y0 = int.Parse(a[1]), x1 = int.Parse(a[2]), y1 = int.Parse(a[3]);
                int ms = a.Length > 4 ? int.Parse(a[4]) : 250;
                int steps = Math.Max(2, ms / 8);
                for (int i = 0; i <= steps; i++)
                {
                    double t = i / (double)steps;
                    control.SendTouch(1, (int)(x0 + (x1 - x0) * t), (int)(y0 + (y1 - y0) * t), true);
                    await Task.Delay(ms / steps);
                }
                control.SendTouch(1, x1, y1, false);
                await Task.Delay(100);
                Console.WriteLine($"swiped ({x0},{y0}) -> ({x1},{y1}) in {ms} ms");
                return 0;
            }
            case "key":
            {
                if (!AndroidKeys.TryGet(a[0], out var ev)) { Console.Error.WriteLine("unknown Android key"); return 1; }
                control.SendEvdevKey(ev, true);
                await Task.Delay(40);
                control.SendEvdevKey(ev, false);
                await Task.Delay(100);
                Console.WriteLine($"sent {a[0]} (evdev {ev})");
                return 0;
            }
            case "text":
                control.SendText(string.Join(' ', a));
                await Task.Delay(100);
                return 0;
            case "shutdown":
                await control.ShutdownAsync();
                Console.WriteLine("shutdown requested");
                return 0;
            default:
                return Unknown("ctl " + args[1]);
        }
    }

    private static async Task<int> Reroll(string[] args)
    {
        var paths = KitsunePaths.Resolve();
        var rm = new RerollManager(paths);
        var sub = args.Length > 0 ? args[0] : "list";
        var a = args.Skip(1).ToArray();
        var progress = new Progress<string>(s => Console.WriteLine("  " + s));
        switch (sub)
        {
            case "list":
            {
                var sets = rm.List();
                if (sets.Count == 0) { Console.WriteLine("No reroll sets. Create one with: kitsune reroll create <set> --from <instance>"); return 0; }
                foreach (var s in sets)
                {
                    Console.WriteLine($"{s.Name,-16} from {s.Template,-16} {s.Clones.Count} clones  {s.RamMb} MB / {s.Cores} cores  snapshots={(s.SaveSnapshots ? "yes" : "no")}");
                    foreach (var c in s.Clones)
                    {
                        var dir = s.CloneDir(c);
                        long bytes = RerollManager.CloneOwnedBytes(dir);
                        Console.WriteLine($"    #{c.Index,-3} {c.Name,-20} {(RerollManager.IsAvdRunning(dir) ? "running" : "stopped"),-8} {bytes / 1048576.0,8:F1} MB written  reset {c.LastReset.LocalDateTime:g}");
                    }
                }
                return 0;
            }
            case "create":
            {
                var name = Positional(a, 0, "set name");
                var from = Opt(a, "--from") ?? throw new ArgumentException("--from <instance> is required.");
                var template = new AvdManager(paths).Get(from) ?? throw new ArgumentException($"No instance named {from}.");
                var host = HostProbe.Probe();
                int ram = int.TryParse(Opt(a, "--ram"), out var r) ? r : 3072;
                int count = int.TryParse(Opt(a, "--count"), out var n) ? n : Math.Clamp(RerollManager.MaxClonesFor(host, ram), 1, 4);
                int cores = int.TryParse(Opt(a, "--cores"), out var c) ? c : 2;
                var set = await rm.CreateAsync(name, template, count, ram, cores, Flag(a, "--snapshots"), progress);
                Console.WriteLine($"Created set {set.Name}: {set.Clones.Count} clones of {template.Name} at {set.Dir}");
                Console.WriteLine($"Host can run {RerollManager.MaxClonesFor(host, ram)} clones at {ram} MB each right now ({host.AvailableRamBytes / 1073741824.0:F1} GB free of {host.TotalRamBytes / 1073741824.0:F0} GB).");
                return 0;
            }
            case "add":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                int n = a.Length > 1 && int.TryParse(a[1], out var k) ? k : 1;
                for (int i = 0; i < n; i++) { var cl = await rm.AddCloneAsync(set); Console.WriteLine($"added {cl.Name}"); }
                return 0;
            }
            case "reset":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                var targets = a.Length > 1 ? [CloneByIndex(set, a[1])] : set.Clones.ToArray();
                foreach (var cl in targets) { await rm.ResetAsync(set, cl); Console.WriteLine($"reset {cl.Name}"); }
                return 0;
            }
            case "remove":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                var cl = CloneByIndex(set, Positional(a, 1, "clone number"));
                await rm.RemoveCloneAsync(set, cl);
                Console.WriteLine($"removed {cl.Name}");
                return 0;
            }
            case "delete":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                rm.Delete(set);
                Console.WriteLine($"deleted set {set.Name}");
                return 0;
            }
            case "keep":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                var cl = CloneByIndex(set, Positional(a, 1, "clone number"));
                var inst = await rm.KeepAsync(set, cl, Positional(a, 2, "new instance name"), progress);
                Console.WriteLine($"Kept {cl.Name} as instance {inst.Name} at {inst.AvdDir}");
                return 0;
            }
            case "launch":
            {
                var set = rm.Get(Positional(a, 0, "set name")) ?? throw new ArgumentException("No such set.");
                int count = int.TryParse(Opt(a, "--count"), out var n) ? Math.Min(n, set.Clones.Count) : set.Clones.Count;
                int stagger = int.TryParse(Opt(a, "--stagger"), out var st) ? st : 1500;
                var host = HostProbe.Probe();
                int fit = RerollManager.MaxClonesFor(host, set.RamMb);
                if (count > fit)
                {
                    Console.WriteLine($"Only {fit} clone(s) fit in the {host.AvailableRamBytes / 1073741824.0:F1} GB currently free; launching {fit}.");
                    count = fit;
                }
                var launcher = new EmulatorLauncher(paths);
                var running = new List<RunningEmulator>();
                var used = new HashSet<int>();
                foreach (var cl in set.Clones.Take(count))
                {
                    RerollManager.TrimSnapshots(set, cl);
                    var inst = rm.CloneInstance(set, cl);
                    int console = EmulatorLauncher.FindFreeConsolePort(used);
                    used.Add(console); used.Add(console + 1);
                    int grpc = EmulatorLauncher.FindFreeTcpPort(8554, used);
                    used.Add(grpc);
                    var opts = new LaunchOptions(ConsolePort: console, GrpcPort: grpc, NoSnapshot: !set.SaveSnapshots, CaptureOutput: Flag(a, "--attach"));
                    var r = launcher.Launch(inst, host, opts);
                    running.Add(r);
                    Console.WriteLine($"{cl.Name,-20} pid {r.Process.Id,-6} console {r.ConsolePort} adb {r.AdbSerial} grpc {r.GrpcPort}");
                    if (stagger > 0) await Task.Delay(stagger);
                }
                if (Flag(a, "--wait") || Flag(a, "--attach"))
                {
                    var started = DateTime.UtcNow;
                    var waits = running.Select(async r =>
                    {
                        await using var control = new EmulatorControl(r.GrpcPort);
                        try
                        {
                            await control.WaitForBootAsync(TimeSpan.FromMinutes(8));
                            Console.WriteLine($"{r.Instance.Name,-20} booted after {(DateTime.UtcNow - started).TotalSeconds:F0}s");
                        }
                        catch (Exception ex) { Console.WriteLine($"{r.Instance.Name,-20} FAILED: {ex.Message}"); }
                    });
                    await Task.WhenAll(waits);
                }
                if (Flag(a, "--attach"))
                {
                    Console.WriteLine("Attached. Ctrl+C stops every clone.");
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; foreach (var r in running) EmulatorLauncher.KillTree(r); };
                    await Task.WhenAll(running.Select(r => r.Process.WaitForExitAsync()));
                    foreach (var cl in set.Clones.Take(count)) RerollManager.TrimSnapshots(set, cl);
                }
                return 0;
            }
            default:
                return Unknown("reroll " + sub);
        }

        static RerollClone CloneByIndex(RerollSet set, string text)
        {
            if (!int.TryParse(text.TrimStart('#'), out var idx)) throw new ArgumentException("Clone number expected.");
            return set.Clones.FirstOrDefault(c => c.Index == idx) ?? throw new ArgumentException($"No clone #{idx} in {set.Name}.");
        }
    }

    private static int Keymap(string[] args)
    {
        if (args.Length >= 2 && args[0] == "validate")
        {
            var profile = KeymapProfile.Load(args[1]);
            var problems = profile.Validate();
            Console.WriteLine($"{profile.Name}: {profile.Bindings.Count} bindings, {(problems.Count == 0 ? "OK" : problems.Count + " problem(s)")}");
            foreach (var p in problems) Console.WriteLine("  " + p);
            return problems.Count == 0 ? 0 : 4;
        }
        if (args.Length >= 1 && args[0] == "selftest") return EngineSelfTest.Run();
        return Unknown("keymap");
    }

    private sealed class ConsoleProgress : IProgress<DownloadProgress>
    {
        private string? _lastPkg;
        private int _lastPct = -1;
        public void Report(DownloadProgress p)
        {
            if (p.Package != _lastPkg) { if (_lastPkg is not null) Console.WriteLine(); _lastPkg = p.Package; _lastPct = -1; }
            int pct = (int)(p.Fraction * 100);
            if (p.Phase == "downloading" && pct == _lastPct) return;
            _lastPct = pct;
            Console.Write($"\r{p.Package,-60} {p.Phase,-12} {pct,3}%  {p.BytesDone / 1048576.0,8:F1} MB");
            if (p.Phase is "done" or "up to date" or "cached") Console.WriteLine();
        }
        public void Finish() { Console.WriteLine(); }
    }
}
