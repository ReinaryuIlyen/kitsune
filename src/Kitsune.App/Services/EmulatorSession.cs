using System.Windows.Threading;
using Android.Emulation.Control;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Kitsune.App.Native;
using Kitsune.Core.Avd;
using Kitsune.Core.Emulator;
using Kitsune.Core.Host;
using Kitsune.Core.Input;

namespace Kitsune.App.Services;

public enum SessionState { Starting, Booting, Running, Stopping, Exited, Failed }

/// <summary>
/// One running emulator: the process, its gRPC control channel, the keymap engine wired to it, and the
/// tracker that follows the emulator window around the screen.
/// </summary>
public sealed class EmulatorSession : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _textFieldFocused;
    private bool _userEnabled = true;

    public Instance Instance { get; }
    public RunningEmulator Running { get; }
    public EmulatorControl Control { get; }
    public KeymapEngine Engine { get; }
    /// <summary>Tees everything the engine sends; also receives host mouse input from the hooks while recording.</summary>
    public MacroRecorder Recorder { get; }
    public GrpcMacroTarget MacroTarget { get; }
    public WindowTracker Window { get; }
    public MacroPlayer? Player { get; private set; }
    public string MacroStatus { get; private set; } = "";
    private CancellationTokenSource? _playCts;
    public SessionState State { get; private set; } = SessionState.Starting;
    public string? Error { get; private set; }
    public int GuestWidth { get; private set; }
    public int GuestHeight { get; private set; }
    public string HypervisorName { get; private set; } = "";
    public double BootSeconds { get; private set; }
    public Accelerator Accelerator { get; }

    public event Action? Changed;
    public event Action? WindowMoved;

    public EmulatorSession(EmulatorLauncher launcher, Instance instance, HostInfo host, LaunchOptions options)
        : this(instance, launcher.Launch(instance, host, options), instance.Config.AcceleratorOverride ?? host.Recommended) { }

    /// <summary>Re-attaches to an emulator that is already running (started by the CLI, or by a launcher that was closed since).</summary>
    public static EmulatorSession Attach(Instance instance, DiscoveredEmulator found, HostInfo host, string logFile) =>
        new(instance, RunningEmulator.Attach(instance, found, logFile), instance.Config.AcceleratorOverride ?? host.Recommended);

    private EmulatorSession(Instance instance, RunningEmulator running, Accelerator accelerator)
    {
        Instance = instance;
        Running = running;
        Accelerator = accelerator;
        Control = new EmulatorControl(Running.GrpcPort);
        Recorder = new MacroRecorder(new GrpcInputSink(Control));
        Engine = new KeymapEngine(Recorder);
        GuestWidth = instance.Config.Width;
        GuestHeight = instance.Config.Height;
        Engine.SetGuestDisplay(GuestWidth, GuestHeight);
        Recorder.SetGuestDisplay(GuestWidth, GuestHeight);
        MacroTarget = new GrpcMacroTarget(Control, GuestWidth, GuestHeight);
        Window = new WindowTracker(Running.WindowTitle);
        // A ToggleKeymap key flips the user's switch, so the checkbox follows it and a text-field pause
        // or a disabled engine never swallows the way back.
        Engine.ToggleRequested += () => KeymapEnabled = !KeymapEnabled;

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = BootAsync();
    }

    /// <summary>The user's on/off switch. Text-field focus in the guest suspends mapping independently.</summary>
    public bool KeymapEnabled
    {
        get => _userEnabled;
        set { _userEnabled = value; ApplyEnabled(); Changed?.Invoke(); }
    }

    public bool TextFieldFocused => _textFieldFocused;

    private void ApplyEnabled() => Engine.Enabled = _userEnabled && !_textFieldFocused && State == SessionState.Running;

    private async Task BootAsync()
    {
        var started = DateTime.UtcNow;
        try
        {
            State = SessionState.Booting; Changed?.Invoke();
            var status = await Control.WaitForBootAsync(TimeSpan.FromMinutes(5), _cts.Token);
            BootSeconds = (DateTime.UtcNow - started).TotalSeconds;

            // The port could belong to another emulator if ours failed to start: trust only an answer that names our AVD.
            if (Running.HasExited)
                throw new InvalidOperationException($"The emulator exited with code {Running.Process.ExitCode} (see the log). Something else answers on port {Running.GrpcPort}.");
            var avdName = status.HardwareConfig.Entry.FirstOrDefault(e => e.Key == "avd.name")?.Value;
            if (avdName is not null && !string.Equals(avdName, Instance.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Port {Running.GrpcPort} is answered by emulator '{avdName}', not '{Instance.Name}'.");
            // The control service does not always name AEHD; fall back to what the launcher selected.
            var hv = status.VmConfig.HypervisorType;
            HypervisorName = hv is VmConfiguration.Types.VmHypervisorType.Unknown or VmConfiguration.Types.VmHypervisorType.None
                ? Accelerator.ToString().ToUpperInvariant() : hv.ToString();
            try
            {
                var displays = await Control.GetDisplaysAsync(_cts.Token);
                var primary = displays.Displays.FirstOrDefault(d => d.Display == 0);
                if (primary is not null && primary.Width > 0 && primary.Height > 0)
                {
                    GuestWidth = (int)primary.Width; GuestHeight = (int)primary.Height;
                    Engine.SetGuestDisplay(GuestWidth, GuestHeight);
                    Recorder.SetGuestDisplay(GuestWidth, GuestHeight);
                    MacroTarget.GuestWidth = GuestWidth; MacroTarget.GuestHeight = GuestHeight;
                }
            }
            catch { }
            State = SessionState.Running;
            ApplyEnabled();
            Changed?.Invoke();
            _ = WatchNotificationsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Running.HasExited) { State = SessionState.Exited; Error = $"The emulator exited with code {Running.Process.ExitCode}. See {Running.LogFile}."; }
            else { State = SessionState.Failed; Error = ex.Message; }
            Changed?.Invoke();
        }
    }

    /// <summary>Follows guest text-field focus so mapped keys stop stealing keystrokes while typing, and display changes (rotation).</summary>
    private async Task WatchNotificationsAsync()
    {
        try
        {
            using var call = Control.Client.streamNotification(new Empty(), cancellationToken: _cts.Token);
            await foreach (var n in call.ResponseStream.ReadAllAsync(_cts.Token))
            {
                if (n.TextViewFocus is { } tf)
                {
                    _textFieldFocused = tf.TextViewHasFocus;
                    ApplyEnabled();
                    Changed?.Invoke();
                }
                if (n.DisplayConfigurationsChangedNotification is { } dc)
                {
                    var primary = dc.DisplayConfigurations.Displays.FirstOrDefault(d => d.Display == 0);
                    if (primary is not null && primary.Width > 0 && primary.Height > 0)
                    {
                        GuestWidth = (int)primary.Width; GuestHeight = (int)primary.Height;
                        Engine.SetGuestDisplay(GuestWidth, GuestHeight);
                        Recorder.SetGuestDisplay(GuestWidth, GuestHeight);
                        MacroTarget.GuestWidth = GuestWidth; MacroTarget.GuestHeight = GuestHeight;
                        Changed?.Invoke();
                    }
                }
            }
        }
        catch { }
    }

    private void Tick()
    {
        if (Running.HasExited && State is not (SessionState.Exited or SessionState.Failed))
        {
            State = SessionState.Exited;
            Engine.Enabled = false;
            Changed?.Invoke();
            _timer.Stop();
            return;
        }
        if (Window.Update(GuestWidth, GuestHeight))
        {
            var r = Window.DisplayRect;
            Engine.SetViewport(r.Left, r.Top, r.Width, r.Height);
            WindowMoved?.Invoke();
        }
    }

    // ---- macros ------------------------------------------------------------------------------

    public bool IsPlaying => _playCts is not null && !_playCts.IsCancellationRequested;

    /// <summary>Plays a macro on this instance; the keymap keeps working alongside it. One macro at a time per instance.</summary>
    public async Task<int> PlayMacroAsync(Macro macro, int repeat, double speed = 1.0)
    {
        StopMacro();
        var cts = new CancellationTokenSource();
        _playCts = cts;
        Player = new MacroPlayer(MacroTarget) { Speed = speed };
        Player.Progress += p => { MacroStatus = $"{macro.Name}: iteration {p.Iteration}, step {p.StepIndex}/{p.StepCount}, {p.Message}"; Changed?.Invoke(); };
        MacroStatus = $"{macro.Name}: starting"; Changed?.Invoke();
        try
        {
            int done = await Player.RunAsync(macro, repeat, cts.Token);
            MacroStatus = $"{macro.Name}: finished {done} iteration(s)";
            return done;
        }
        catch (OperationCanceledException) { MacroStatus = $"{macro.Name}: stopped"; return 0; }
        catch (Exception ex) { MacroStatus = $"{macro.Name}: failed, {ex.Message}"; return 0; }
        finally
        {
            if (_playCts == cts) _playCts = null;
            cts.Dispose();
            Changed?.Invoke();
        }
    }

    public void StopMacro()
    {
        try { _playCts?.Cancel(); } catch { }
    }

    /// <summary>Captures a checkpoint around a guest point from the live screen and appends it to the current recording.</summary>
    public async Task AddCheckpointAsync(double nx, double ny, double size, int tolerance, int timeoutMs)
    {
        if (!Recorder.Recording) return;
        var (fw, fh) = ScreenMatcher.FrameSizeFor(GuestWidth, GuestHeight);
        var (rgb, w, h) = await Control.GetFrameAsync(fw, fh);
        double aspect = GuestWidth / (double)Math.Max(1, GuestHeight);
        double nw = aspect >= 1 ? size / aspect : size, nh = aspect >= 1 ? size : size * aspect;
        double x = Math.Clamp(nx - nw / 2, 0, 1 - nw), y = Math.Clamp(ny - nh / 2, 0, 1 - nh);
        Recorder.AddCheckpoint(rgb, w, h, x, y, nw, nh, tolerance, timeoutMs, $"checkpoint {Recorder.StepCount + 1}");
        Changed?.Invoke();
    }

    /// <summary>Installs a package on this instance through adb; the keymap and macros keep running meanwhile.</summary>
    public Task<AdbTools.Result> InstallApkAsync(AdbTools adb, string file, IProgress<string>? progress = null, CancellationToken ct = default) =>
        adb.InstallAsync(Running.AdbSerial, file, progress, ct);

    public async Task StopAsync()
    {
        StopMacro();
        State = SessionState.Stopping; Changed?.Invoke();
        Engine.Enabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Control.ShutdownAsync(timeout.Token);
        }
        catch { }
        try { await Task.WhenAny(Running.Process.WaitForExitAsync(), Task.Delay(15000)); } catch { }
        if (!Running.HasExited) EmulatorLauncher.KillTree(Running);
        State = SessionState.Exited; Changed?.Invoke();
    }

    public void Dispose()
    {
        StopMacro();
        _timer.Stop();
        _cts.Cancel();
        Engine.Dispose();
        _ = Control.DisposeAsync();
        Running.Dispose();
        _cts.Dispose();
    }
}
