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
    public WindowTracker Window { get; }
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
    {
        Instance = instance;
        Running = launcher.Launch(instance, host, options);
        Accelerator = instance.Config.AcceleratorOverride ?? host.Recommended;
        Control = new EmulatorControl(Running.GrpcPort);
        Engine = new KeymapEngine(new GrpcInputSink(Control));
        GuestWidth = instance.Config.Width;
        GuestHeight = instance.Config.Height;
        Engine.SetGuestDisplay(GuestWidth, GuestHeight);
        Window = new WindowTracker(Running.WindowTitle);

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
                    if (primary is not null && primary.Width > 0)
                    {
                        GuestWidth = (int)primary.Width; GuestHeight = (int)primary.Height;
                        Engine.SetGuestDisplay(GuestWidth, GuestHeight);
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

    public async Task StopAsync()
    {
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
        _timer.Stop();
        _cts.Cancel();
        Engine.Dispose();
        _ = Control.DisposeAsync();
        Running.Dispose();
        _cts.Dispose();
    }
}
