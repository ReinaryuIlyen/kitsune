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
    bool CaptureOutput = true,
    /// <summary>Neither load nor save a quick-boot snapshot (reroll clones: always a clean cold boot, no multi-GB RAM files).</summary>
    bool NoSnapshot = false);

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

    /// <summary>Wraps an emulator this process did not start (found through <see cref="EmulatorDiscovery"/>).</summary>
    public static RunningEmulator Attach(Instance instance, DiscoveredEmulator found, string logFile)
    {
        var process = Process.GetProcessById(found.Pid);
        return new RunningEmulator(instance, process, found.ConsolePort, found.GrpcPort, logFile);
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
        if (options.NoSnapshot)
        {
            // No quick-boot at all. QuickbootFileBacked is the feature that maps guest RAM to
            // snapshots/default_boot/ram.img from the moment the VM starts (a RAM-sized file per
            // instance, written whether or not a snapshot is ever saved); off, RAM is plain memory.
            args.Add("-no-snapshot"); args.Add("-no-snapshot-save");
            args.Add("-feature"); args.Add("-QuickbootFileBacked");
        }
        else if (!cfg.Quickboot || options.ColdBoot) args.Add("-no-snapshot-load");
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
        // Output is always redirected: an inherited console handle would keep the parent's pipe open for
        // the emulator's whole life (a `kitsune launch | ...` pipeline would never finish). When not
        // capturing, the streams are drained and discarded; a closed pipe is harmless to the emulator on Windows.
        var psi = new ProcessStartInfo
        {
            FileName = plan.Executable,
            WorkingDirectory = _paths.EmulatorDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
        foreach (var kv in plan.Environment) psi.Environment[kv.Key] = kv.Value;
        psi.Environment["PATH"] = _paths.PlatformToolsDir + ";" + (psi.Environment.TryGetValue("PATH", out var p) ? p : "");

        // With redirected streams .NET starts the child with handle inheritance on, so our own console
        // handles (possibly a pipe from a calling shell) would leak into the emulator and keep that pipe
        // open for its whole life. Make them non-inheritable first.
        foreach (var std in new[] { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE })
        {
            var h = GetStdHandle(std);
            if (h != IntPtr.Zero && h != INVALID_HANDLE_VALUE) SetHandleInformation(h, HANDLE_FLAG_INHERIT, 0);
        }

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
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        try { process.StandardInput.Close(); } catch { }

        return new RunningEmulator(inst, process, plan.ConsolePort, plan.GrpcPort, logFile);
    }

    /// <summary>Console ports are even numbers from 5554 to 5682; adb uses console+1. <paramref name="reserved"/> skips ports handed out but not yet bound.</summary>
    public static int FindFreeConsolePort(ISet<int>? reserved = null)
    {
        for (int port = 5554; port <= 5682; port += 2)
            if (!(reserved?.Contains(port) ?? false) && !(reserved?.Contains(port + 1) ?? false) && IsFree(port) && IsFree(port + 1)) return port;
        throw new InvalidOperationException("No free emulator console port between 5554 and 5682.");
    }

    public static int FindFreeTcpPort(int start, ISet<int>? reserved = null)
    {
        for (int port = start; port < start + 200; port++)
            if (!(reserved?.Contains(port) ?? false) && IsFree(port)) return port;
        throw new InvalidOperationException("No free TCP port for the gRPC control endpoint.");
    }

    /// <summary>
    /// A port is free when nothing listens on it and no connection uses it. A trial bind is not enough:
    /// a listener that set SO_REUSEADDR (gRPC servers do) lets a second bind succeed on Windows.
    /// </summary>
    private static bool IsFree(int port)
    {
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            if (props.GetActiveTcpListeners().Any(e => e.Port == port)) return false;
            if (props.GetActiveTcpConnections().Any(c => c.LocalEndPoint.Port == port)) return false;
        }
        catch { }
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    private const int STD_INPUT_HANDLE = -10, STD_OUTPUT_HANDLE = -11, STD_ERROR_HANDLE = -12;
    private const uint HANDLE_FLAG_INHERIT = 1;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    /// <summary>Kills the emulator and the QEMU child it spawns.</summary>
    public static void KillTree(RunningEmulator running)
    {
        try { if (!running.Process.HasExited) running.Process.Kill(entireProcessTree: true); } catch { }
    }
}
