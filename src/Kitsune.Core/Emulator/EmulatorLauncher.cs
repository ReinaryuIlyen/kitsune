using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Kitsune.Core.Avd;
using Kitsune.Core.Host;

namespace Kitsune.Core.Emulator;

/// <param name="CaptureOutput">
/// Redirect the emulator's stdout/stderr into the log file. Only safe when the launching process
/// outlives the emulator (the GUI does); a short-lived CLI must leave the pipes alone.
/// </param>
public sealed record LaunchOptions(
    bool ColdBoot = false,
    bool WipeData = false,
    bool ReadOnly = false,
    bool Headless = false,
    int? ConsolePort = null,
    int? GrpcPort = null,
    bool CaptureOutput = true);

/// <summary>A running emulator instance started by Kitsune.</summary>
public sealed class RunningEmulator : IDisposable
{
    public Instance Instance { get; }
    public Process Process { get; }
    public int ConsolePort { get; }
    public int AdbPort => ConsolePort + 1;
    public int GrpcPort { get; }
    public string AdbSerial => $"emulator-{ConsolePort}";
    public string LogFile { get; }
    public string WindowTitle => $"Android Emulator - {Instance.Name}:{ConsolePort}";
    public bool HasExited => Process.HasExited;

    internal RunningEmulator(Instance instance, Process process, int consolePort, int grpcPort, string logFile)
    {
        Instance = instance; Process = process; ConsolePort = consolePort; GrpcPort = grpcPort; LogFile = logFile;
    }

    public void Dispose() => Process.Dispose();
}

/// <summary>Builds the emulator command line and environment from an instance configuration and the host probe.</summary>
public sealed class EmulatorLauncher
{
    private readonly KitsunePaths _paths;
    public EmulatorLauncher(KitsunePaths paths) => _paths = paths;

    public sealed record Plan(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, int ConsolePort, int GrpcPort, Accelerator Accelerator);

    public Plan BuildPlan(Instance inst, HostInfo host, LaunchOptions? options = null)
    {
        options ??= new LaunchOptions();
        var cfg = inst.Config;
        var accel = cfg.AcceleratorOverride ?? host.Recommended;
        if (accel == Accelerator.None)
            throw new InvalidOperationException(host.HardwareVirtualization
                ? "No usable hypervisor: Hyper-V is active but the Windows Hypervisor Platform feature is off. Either enable 'Windows Hypervisor Platform' or turn off Hyper-V, Virtual Machine Platform, WSL2 and memory integrity so AEHD can be used."
                : "This CPU does not expose VT-x/AMD-V. Enable virtualization in the firmware setup.");
        if (accel == Accelerator.Aehd && host.Aehd != AehdState.Running)
            throw new InvalidOperationException("AEHD is the chosen accelerator but its driver is not running. Install it from the launcher (one UAC prompt).");

        int consolePort = options.ConsolePort ?? FindFreeConsolePort();
        int grpcPort = options.GrpcPort ?? FindFreeTcpPort(8554);

        var args = new List<string>
        {
            "-avd", inst.Name,
            "-port", consolePort.ToString(),
            "-grpc", grpcPort.ToString(),
            "-accel", "on",
            "-gpu", cfg.Gpu switch { GpuMode.Angle => "angle_indirect", GpuMode.SwiftShader => "swiftshader_indirect", _ => "host" },
            "-cores", Math.Clamp(cfg.Cores, 1, Defaults.MaxCores).ToString(),
            "-memory", cfg.RamMb.ToString(),
            "-no-metrics",
            "-no-boot-anim",
            "-netfast",
            "-feature", cfg.Vulkan ? "Vulkan" : "-Vulkan",
        };
        if (!cfg.Audio) args.Add("-no-audio");
        if (!cfg.Quickboot || options.ColdBoot) args.Add("-no-snapshot-load");
        if (options.WipeData) args.Add("-wipe-data");
        if (options.ReadOnly) args.Add("-read-only");
        if (options.Headless) args.Add("-no-window");
        if (!cfg.Camera) { args.Add("-camera-back"); args.Add("none"); args.Add("-camera-front"); args.Add("none"); }
        args.AddRange(cfg.ExtraArgs);

        var env = new Dictionary<string, string>
        {
            ["ANDROID_SDK_ROOT"] = _paths.Sdk,
            ["ANDROID_HOME"] = _paths.Sdk,
            ["ANDROID_AVD_HOME"] = _paths.AvdHome,
            ["ANDROID_EMULATOR_HOME"] = _paths.EmulatorHome,
        };
        if (cfg.PinDiscreteGpu && host.PreferredVulkanIcd is { } icd)
        {
            env["VK_DRIVER_FILES"] = icd.Path;
            env["VK_ICD_FILENAMES"] = icd.Path;
        }
        return new Plan(_paths.EmulatorExe, args, env, consolePort, grpcPort, accel);
    }

    public RunningEmulator Launch(Instance inst, HostInfo host, LaunchOptions? options = null)
    {
        if (!File.Exists(_paths.EmulatorExe)) throw new FileNotFoundException("The emulator is not installed.", _paths.EmulatorExe);
        var plan = BuildPlan(inst, host, options);

        if (inst.Config.PinDiscreteGpu && File.Exists(_paths.QemuExe))
        {
            try { GpuPreference.SetHighPerformance(_paths.QemuExe); } catch { /* non-fatal */ }
        }

        Directory.CreateDirectory(_paths.Logs);
        var logFile = Path.Combine(_paths.Logs, $"{inst.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        bool capture = options?.CaptureOutput ?? true;
        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            WorkingDirectory = _paths.EmulatorDir,
            UseShellExecute = false,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            CreateNoWindow = true,
        };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        foreach (var kv in plan.Environment) psi.Environment[kv.Key] = kv.Value;
        psi.Environment["PATH"] = _paths.PlatformToolsDir + ";" + (psi.Environment.TryGetValue("PATH", out var p) ? p : "");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the emulator process.");
        var writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
        writer.WriteLine($"# {plan.Executable} {string.Join(' ', plan.Arguments)}");
        foreach (var kv in plan.Environment) writer.WriteLine($"# {kv.Key}={kv.Value}");
        if (capture)
        {
            var gate = new object();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) writer.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) writer.WriteLine(e.Data); };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => { lock (gate) { writer.WriteLine($"# exited with code {process.ExitCode}"); writer.Dispose(); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        else
        {
            writer.WriteLine("# output not captured (detached launch)");
            writer.Dispose();
        }

        return new RunningEmulator(inst, process, plan.ConsolePort, plan.GrpcPort, logFile);
    }

    /// <summary>Console ports are even numbers from 5554 to 5682; adb uses console+1.</summary>
    public static int FindFreeConsolePort()
    {
        for (int port = 5554; port <= 5682; port += 2)
            if (IsFree(port) && IsFree(port + 1)) return port;
        throw new InvalidOperationException("No free emulator console port between 5554 and 5682.");
    }

    public static int FindFreeTcpPort(int start)
    {
        for (int port = start; port < start + 200; port++)
            if (IsFree(port)) return port;
        throw new InvalidOperationException("No free TCP port for the gRPC control endpoint.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>Kills the emulator and the QEMU child it spawns.</summary>
    public static void KillTree(RunningEmulator running)
    {
        try { if (!running.Process.HasExited) running.Process.Kill(entireProcessTree: true); } catch { }
    }
}
