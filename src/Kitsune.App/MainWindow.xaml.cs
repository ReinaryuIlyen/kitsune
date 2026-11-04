using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kitsune.App.Native;
using Kitsune.App.Services;
using Kitsune.App.Views;
using Kitsune.Core.Avd;
using Kitsune.Core.Emulator;
using Kitsune.Core.Host;
using Kitsune.Core.Input;
using Kitsune.Core.Sdk;
using Macro = Kitsune.Core.Input.Macro;

namespace Kitsune.App;

public partial class MainWindow : Window
{
    private sealed record InstanceRow(Instance Instance)
    {
        public string Name => Instance.Name;
        public string Summary => $"Android {ApiToVersion(Instance.Config.Image.ApiLevel)} · {Instance.Config.Width}x{Instance.Config.Height} · {Instance.Config.Cores}c/{Instance.Config.RamMb / 1024}G";
        private static string ApiToVersion(string api) => api switch
        {
            "33" => "13", "34" => "14", "35" => "15", "36" => "16", "36.1" => "16 QPR", "37.0" => "17", "37.1" => "17 QPR", _ => "API " + api,
        };
    }

    private readonly AppState _state = App.State;
    private readonly Dictionary<string, EmulatorSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlayWindow> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly LowLevelHooks _hooks = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IReadOnlyList<(string File, KeymapProfile Profile)> _profiles = [];
    private bool _installing;
    private CancellationTokenSource? _installCts;
    private int _discoveryTick;
    // An emulator found running for the selected instance outside this launcher. Discovery is a WMI query
    // (tens to hundreds of milliseconds), so it runs off the UI thread and only the cached answer is read
    // on it: a stalled UI thread makes Windows drop the low-level input hooks.
    private DiscoveredEmulator? _orphan;
    private string? _orphanFor;
    private bool _discovering;

    public MainWindow()
    {
        InitializeComponent();
        ShowBadgesBox.IsChecked = _state.Settings.ShowOverlayBadges;
        _hooks.IsActive = HooksActive;
        _hooks.OnKey = OnHookKey;
        _hooks.OnMouseButton = OnHookMouseButton;
        _hooks.OnMouseMove = OnHookMouseMove;
        _hooks.OnWheel = delta => ActiveSession()?.Engine.OnWheel(delta) == true;
        _logTimer.Tick += (_, _) =>
        {
            RefreshLog();
            if (SelectedSession?.Recorder.Recording == true || SelectedSession?.IsPlaying == true) RefreshMacroButtons();
            // Emulators started elsewhere (CLI, an earlier launcher) show up as attachable within a few seconds.
            if (++_discoveryTick % 3 == 0) DiscoverOrphan();
        };
        _logTimer.Start();
        Loaded += (_, _) =>
        {
            try { _hooks.Install(); } catch (Exception ex) { StateText.Text = "Input hooks unavailable: " + ex.Message; }
            RefreshAll();
            DiscoverOrphan();
        };
        Closing += (_, _) => { _hooks.Dispose(); foreach (var s in _sessions.Values) s.Dispose(); foreach (var o in _overlays.Values) o.Close(); };
    }

    // ---- hooks -------------------------------------------------------------------------------

    private EmulatorSession? ActiveSession()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.State != SessionState.Running) continue;
            if (s.Window.IsForeground()) return s;
            if (_overlays.TryGetValue(s.Instance.Name, out var o) && o.IsCaptureSurface && o.IsForegroundWindow()) return s;
        }
        return null;
    }

    private bool HooksActive()
    {
        var s = ActiveSession();
        if (s is null) return false;
        if (_overlays.TryGetValue(s.Instance.Name, out var o) && o.EditMode) return false;
        // Macro hotkeys and mouse recording must work even while the keymap is paused.
        return true;
    }

    private bool OnHookKey(int vk, bool down)
    {
        var s = ActiveSession();
        if (s is null) return false;
        if (HostKeys.TryParse(_state.Settings.RecordKey, out var rec) && HostKeys.Matches(rec, vk))
        {
            if (down) Dispatcher.BeginInvoke(() => ToggleRecording(s));
            return true;
        }
        if (HostKeys.TryParse(_state.Settings.CheckpointKey, out var cp) && HostKeys.Matches(cp, vk))
        {
            if (down && s.Recorder.Recording)
            {
                Win32.GetCursorPos(out var pt);
                var (gx, gy) = s.Engine.CursorToGuest(pt.X, pt.Y);
                double nx = gx / (double)Math.Max(1, s.GuestWidth - 1), ny = gy / (double)Math.Max(1, s.GuestHeight - 1);
                var st = _state.Settings;
                _ = s.AddCheckpointAsync(nx, ny, st.CheckpointSize, st.CheckpointTolerance, st.CheckpointTimeoutMs);
            }
            return true;
        }
        if (HostKeys.TryParse(_state.Settings.StopMacroKey, out var stop) && HostKeys.Matches(stop, vk))
        {
            if (down) s.StopMacro();
            return true;
        }
        // The engine itself ignores everything but ToggleKeymap keys while it is paused.
        return s.Engine.OnKey(vk, down);
    }

    private bool OnHookMouseButton(HostMouseButton b, bool down, int x, int y)
    {
        var s = ActiveSession();
        if (s is null) return false;
        bool consumed = s.Engine.OnMouseButton(b, down, x, y);
        // A left click the emulator window handles itself is still part of a recording.
        if (!consumed && b == HostMouseButton.Left && s.Recorder.Recording && InViewport(s, x, y))
        {
            var (gx, gy) = s.Engine.CursorToGuest(x, y);
            s.Recorder.RecordMouse(gx, gy, down);
        }
        return consumed;
    }

    private bool OnHookMouseMove(int dx, int dy, int x, int y)
    {
        var s = ActiveSession();
        if (s is null) return false;
        bool consumed = s.Engine.OnMouseMove(dx, dy, x, y);
        if (!consumed && s.Recorder.Recording && InViewport(s, x, y))
        {
            var (gx, gy) = s.Engine.CursorToGuest(x, y);
            s.Recorder.RecordMouse(gx, gy, null);
        }
        return consumed;
    }

    private static bool InViewport(EmulatorSession s, int x, int y)
    {
        var r = s.Window.DisplayRect;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // ---- refresh -----------------------------------------------------------------------------

    private void RefreshAll()
    {
        _state.RefreshHost();
        RefreshHeader();
        RefreshInstances();
        RefreshProfiles();
        RefreshMacros();
        RefreshSelected();
    }

    /// <summary>Redraws the header chips from the cached host probe; callers that changed the host re-probe first.</summary>
    private void RefreshHeader()
    {
        var h = _state.Host;
        var ver = _state.EmulatorVersion;
        EmulatorChip.Text = ver is null ? "Emulator: not installed" : $"Emulator {ver}";
        AccelChip.Text = h.Recommended switch
        {
            Accelerator.Aehd => h.Aehd == AehdState.Running ? "AEHD: running" : "AEHD: driver not installed",
            Accelerator.Whpx => "WHPX (Hyper-V present)",
            _ => h.HardwareVirtualization ? "No accelerator available" : "VT-x/AMD-V disabled in firmware",
        };
        AccelChip.Foreground = (h.Recommended == Accelerator.Aehd && h.Aehd != AehdState.Running) || h.Recommended == Accelerator.None
            ? (System.Windows.Media.Brush)FindResource("Warn") : (System.Windows.Media.Brush)FindResource("Good");
        GpuChip.Text = "GPU: " + (h.PreferredGpu?.Name ?? "unknown");
        AehdBtn.IsEnabled = h.Recommended == Accelerator.Aehd && h.Aehd != AehdState.Running && AehdInstaller.PackagePresent(_state.Paths) && !_installing;
        AehdBtn.Content = h.Aehd == AehdState.Running ? "AEHD installed" : "Install AEHD driver";
        InstallBtn.IsEnabled = !_installing;
        ImagesBtn.IsEnabled = !_installing;
    }

    private void RefreshInstances()
    {
        var selected = (InstanceList.SelectedItem as InstanceRow)?.Name ?? _state.Settings.LastInstance;
        var rows = _state.Avds.List().Select(i => new InstanceRow(i)).ToList();
        InstanceList.ItemsSource = rows;
        InstanceList.SelectedItem = rows.FirstOrDefault(r => string.Equals(r.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
    }

    private void RefreshProfiles()
    {
        _profiles = _state.LoadProfiles();
        var current = ProfileCombo.SelectedItem as string;
        ProfileCombo.ItemsSource = _profiles.Select(p => p.Profile.Name).ToList();
        var inst = Selected;
        var want = inst?.Config.KeymapProfile ?? current;
        ProfileCombo.SelectedItem = _profiles.Select(p => p.Profile.Name).FirstOrDefault(n => n == want) ?? _profiles.Select(p => p.Profile.Name).FirstOrDefault();
    }

    private Instance? Selected => (InstanceList.SelectedItem as InstanceRow)?.Instance;

    private EmulatorSession? SelectedSession => Selected is { } i && _sessions.TryGetValue(i.Name, out var s) ? s : null;

    private void RefreshSelected()
    {
        var inst = Selected;
        var session = SelectedSession;
        bool running = session is not null && session.State is SessionState.Starting or SessionState.Booting or SessionState.Running;
        var orphan = inst is not null && !running && string.Equals(_orphanFor, inst.Name, StringComparison.OrdinalIgnoreCase) ? _orphan : null;
        if (inst is null)
        {
            InstanceTitle.Text = "No instance selected";
            InstanceDetails.Text = _state.EmulatorVersion is null
                ? "Install the components first, then create an instance."
                : "Create an instance to get started.";
            StateText.Text = "";
        }
        else
        {
            var c = inst.Config;
            InstanceTitle.Text = inst.Name;
            InstanceDetails.Text = $"{c.SystemImage}\n{c.Width} x {c.Height} @ {c.Density} dpi, {c.RefreshRate} Hz · {c.Cores} vCPU · {c.RamMb} MB RAM · GPU {c.Gpu}{(c.Vulkan ? " + Vulkan" : "")}{(c.Quickboot ? " · quick boot" : " · cold boot")}";
            StateText.Text = orphan is not null
                ? $"Running outside the launcher (pid {orphan.Pid}, console {orphan.ConsolePort}, gRPC {orphan.GrpcPort}). Attach to control it here."
                : session is null ? "Not running." : session.State switch
            {
                SessionState.Starting => "Starting emulator...",
                SessionState.Booting => $"Booting (console {session.Running.ConsolePort}, gRPC {session.Running.GrpcPort})...",
                SessionState.Running => $"Running on {session.HypervisorName} · booted in {session.BootSeconds:F0}s · adb {session.Running.AdbSerial} · gRPC {session.Running.GrpcPort}"
                                        + (session.TextFieldFocused ? " · keymap paused (text field focused)" : ""),
                SessionState.Stopping => "Stopping...",
                SessionState.Exited => "Exited." + (session.Error is null ? "" : " " + session.Error),
                SessionState.Failed => "Failed: " + session.Error,
                _ => "",
            };
            KeymapEnabledBox.IsChecked = session?.KeymapEnabled ?? true;
        }
        LaunchBtn.IsEnabled = inst is not null && !running && orphan is null && _state.EmulatorVersion is not null;
        ColdBtn.IsEnabled = LaunchBtn.IsEnabled;
        AttachBtn.Visibility = orphan is not null && orphan.GrpcPort > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.IsEnabled = running;
        EditBtn.IsEnabled = inst is not null && !running;
        FocusBtn.IsEnabled = session?.State == SessionState.Running;
        CloneBtn.IsEnabled = inst is not null && !running;
        DeleteBtn.IsEnabled = inst is not null && !running;
        EditOverlayBtn.IsEnabled = session?.State == SessionState.Running && ProfileCombo.SelectedItem is not null;
        KeymapEnabledBox.IsEnabled = session?.State == SessionState.Running;
        ApkBtn.IsEnabled = session?.State == SessionState.Running && !_apkInstalling && _state.Adb.Available;
        ShotBtn.IsEnabled = session?.State == SessionState.Running;
        AdbBtn.IsEnabled = session?.State == SessionState.Running && _state.Adb.Available;
        RefreshMacroButtons();
    }

    /// <summary>Looks for an emulator running the selected instance that this launcher did not start, off the UI thread.</summary>
    private async void DiscoverOrphan()
    {
        var inst = Selected;
        var session = SelectedSession;
        bool running = session is not null && session.State is SessionState.Starting or SessionState.Booting or SessionState.Running;
        if (inst is null || running)
        {
            if (_orphan is not null) { _orphan = null; _orphanFor = null; RefreshSelected(); }
            return;
        }
        if (_discovering) return;
        _discovering = true;
        var name = inst.Name;
        DiscoveredEmulator? found = null;
        try { found = await Task.Run(() => EmulatorDiscovery.Find(name)); } catch { }
        _discovering = false;
        bool changed = found?.Pid != _orphan?.Pid || !string.Equals(name, _orphanFor, StringComparison.OrdinalIgnoreCase);
        _orphan = found; _orphanFor = name;
        if (changed) RefreshSelected();
    }

    private void RefreshLog()
    {
        var session = SelectedSession;
        if (session is null) { if (LogBox.Text.Length > 0) LogBox.Clear(); return; }
        try
        {
            using var fs = new FileStream(session.Running.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new Queue<string>(80);
            while (sr.ReadLine() is { } line) { if (lines.Count == 80) lines.Dequeue(); lines.Enqueue(line); }
            var text = string.Join('\n', lines);
            if (text != LogBox.Text) { LogBox.Text = text; LogBox.ScrollToEnd(); }
        }
        catch { }
    }

    // ---- instances ---------------------------------------------------------------------------

    private void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } i) _state.Settings.LastInstance = i.Name;
        RefreshProfiles();
        RefreshSelected();
        RefreshLog();
        DiscoverOrphan();
    }

    private void NewBtn_Click(object sender, RoutedEventArgs e)
    {
        var images = _state.Components.InstalledImages();
        if (images.Count == 0)
        {
            MessageBox.Show(this, "No system image is installed yet. Use 'Install / update components' first.", "Kitsune", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cfg = new InstanceConfig
        {
            Name = NextName(),
            SystemImage = images.Any(i => i.PackagePath == _state.Settings.DefaultImage) ? _state.Settings.DefaultImage : images[0].PackagePath,
            Cores = Defaults.Cores(_state.Host),
            RamMb = Defaults.RamMb(_state.Host),
        };
        cfg.ApplyProfile(DeviceProfile.Phone1080p2400);
        var dlg = new InstanceDialog(_state, cfg, isNew: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                _state.Avds.Create(dlg.Config);
                _state.Settings.LastInstance = dlg.Config.Name;
                _state.Settings.DefaultImage = dlg.Config.SystemImage;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kitsune", MessageBoxButton.OK, MessageBoxImage.Error); }
            RefreshInstances();
        }
    }

    private string NextName()
    {
        var existing = _state.Avds.List().Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++) { var name = n == 1 ? "kitsune" : $"kitsune-{n}"; if (!existing.Contains(name)) return name; }
    }

    private void CloneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inst) return;
        var name = Prompt.Show(this, "Clone instance", "Name for the copy:", inst.Name + "-copy");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { _state.Avds.Clone(inst, name.Trim()); _state.Settings.LastInstance = name.Trim(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kitsune", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshInstances();
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inst) return;
        if (MessageBox.Show(this, $"Delete '{inst.Name}' and all of its data (installed games, accounts, saves)?", "Kitsune", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _state.Avds.Delete(inst); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kitsune", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshInstances();
    }

    private void EditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inst) return;
        var copy = InstanceConfig.Load(inst.KitsuneJson);
        var dlg = new InstanceDialog(_state, copy, isNew: false) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try { _state.Avds.WriteConfig(new Instance(inst.Name, inst.AvdDir, dlg.Config)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kitsune", MessageBoxButton.OK, MessageBoxImage.Error); }
            RefreshInstances();
        }
    }

    // ---- launch / stop -----------------------------------------------------------------------

    private void LaunchBtn_Click(object sender, RoutedEventArgs e) => Launch(cold: false);
    private void ColdBtn_Click(object sender, RoutedEventArgs e) => Launch(cold: true);

    private void AttachBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inst) return;
        try { AttachSession(inst); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot attach", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshSelected();
    }

    /// <summary>Takes over an emulator found running for this instance, with overlay and hooks like a launched one.</summary>
    internal EmulatorSession AttachSession(Instance inst)
    {
        var found = EmulatorDiscovery.Find(inst.Name) ?? throw new InvalidOperationException($"No running emulator for '{inst.Name}'.");
        if (found.GrpcPort <= 0) throw new InvalidOperationException("That emulator was started without a gRPC port; Kitsune cannot control it. Stop it and launch it from here.");
        if (_sessions.TryGetValue(inst.Name, out var old)) { old.Dispose(); _sessions.Remove(inst.Name); }
        if (_overlays.TryGetValue(inst.Name, out var oldOverlay)) { oldOverlay.Close(); _overlays.Remove(inst.Name); }

        var logFile = Path.Combine(_state.Paths.Logs, $"{inst.Name}-attached-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(logFile, $"# attached to pid {found.Pid}: {found.CommandLine}\n");
        var session = EmulatorSession.Attach(inst, found, _state.Host, logFile);
        _sessions[inst.Name] = session;
        _orphan = null; _orphanFor = null;
        session.Changed += () => Dispatcher.BeginInvoke(() => { RefreshSelected(); RefreshHeader(); });
        var overlay = new OverlayWindow(session, _hooks) { ShowBadges = _state.Settings.ShowOverlayBadges };
        _overlays[inst.Name] = overlay;
        ApplyProfile(session, overlay);
        overlay.ProfileSaved += profile => OnProfileSaved(inst, profile);
        overlay.Show();
        return session;
    }

    public void LaunchByName(string name)
    {
        void Go()
        {
            RefreshInstances();
            var row = (InstanceList.ItemsSource as IEnumerable<InstanceRow>)?.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (row is null) { StateText.Text = $"No instance named '{name}'."; return; }
            InstanceList.SelectedItem = row;
            Launch(cold: false);
        }
        if (IsLoaded) Go(); else Loaded += (_, _) => Dispatcher.BeginInvoke(Go);
    }

    private void Launch(bool cold)
    {
        if (Selected is not { } inst) return;
        try { StartSession(inst, new LaunchOptions(ColdBoot: cold)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Cannot launch", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshSelected();
    }

    /// <summary>Every running emulator started from this launcher, keyed by instance name (reroll clones included).</summary>
    internal IReadOnlyDictionary<string, EmulatorSession> Sessions => _sessions;

    /// <summary>Starts an instance with its keymap overlay and registers it so the global hooks route to it when it is in front.</summary>
    internal EmulatorSession StartSession(Instance inst, LaunchOptions options)
    {
        _state.RefreshHost();
        if (_sessions.TryGetValue(inst.Name, out var old))
        {
            if (old.State is SessionState.Starting or SessionState.Booting or SessionState.Running)
                throw new InvalidOperationException($"'{inst.Name}' is already running.");
            old.Dispose(); _sessions.Remove(inst.Name);
        }
        if (_overlays.TryGetValue(inst.Name, out var oldOverlay)) { oldOverlay.Close(); _overlays.Remove(inst.Name); }

        var session = new EmulatorSession(new EmulatorLauncher(_state.Paths), inst, _state.Host, options);
        _sessions[inst.Name] = session;
        session.Changed += () => Dispatcher.BeginInvoke(() => { RefreshSelected(); RefreshHeader(); });

        var overlay = new OverlayWindow(session, _hooks) { ShowBadges = _state.Settings.ShowOverlayBadges };
        _overlays[inst.Name] = overlay;
        ApplyProfile(session, overlay);
        overlay.ProfileSaved += profile => OnProfileSaved(inst, profile);
        overlay.Show();
        return session;
    }

    internal async Task StopSessionAsync(string name)
    {
        if (!_sessions.TryGetValue(name, out var session)) return;
        if (session.State is SessionState.Starting or SessionState.Booting or SessionState.Running) await session.StopAsync();
    }

    internal void RefreshInstancesPublic() => RefreshInstances();

    /// <summary>Arranges emulator windows in a grid over the primary monitor's work area. The emulator keeps its own aspect ratio inside each cell.</summary>
    internal void TileWindows(IReadOnlyList<EmulatorSession> sessions)
    {
        if (sessions.Count == 0) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var wa = SystemParameters.WorkArea;
        int left = (int)(wa.Left * scale), top = (int)(wa.Top * scale), width = (int)(wa.Width * scale), height = (int)(wa.Height * scale);
        int cols = (int)Math.Ceiling(Math.Sqrt(sessions.Count * (double)width / height * 0.45));
        cols = Math.Clamp(cols, 1, sessions.Count);
        int rows = (int)Math.Ceiling(sessions.Count / (double)cols);
        int cw = width / cols, ch = height / rows;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            int x = left + (i % cols) * cw, y = top + (i / cols) * ch;
            // Fit the guest aspect inside the cell; the emulator snaps to its own ratio anyway.
            double aspect = s.GuestWidth / (double)Math.Max(1, s.GuestHeight);
            int w = cw - 8, h = (int)(w / aspect);
            if (h > ch - 8) { h = ch - 8; w = (int)(h * aspect); }
            Win32.SetWindowPos(s.Window.Hwnd, IntPtr.Zero, x + 4, y + 4, Math.Max(200, w), Math.Max(200, h), Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { } session) return;
        StopBtn.IsEnabled = false;
        await session.StopAsync();
        RefreshSelected();
    }

    private void RerollBtn_Click(object sender, RoutedEventArgs e) => OpenReroll();

    internal void OpenReroll()
    {
        if (_rerollWindow is null || !_rerollWindow.IsLoaded)
        {
            _rerollWindow = new RerollWindow(_state, this) { Owner = this };
            _rerollWindow.Closed += (_, _) => { _rerollWindow = null; RefreshInstances(); };
            _rerollWindow.Show();
        }
        else _rerollWindow.Activate();
    }

    private RerollWindow? _rerollWindow;

    private void FocusBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } s && s.Window.Found) Win32.SetForegroundWindow(s.Window.Hwnd);
    }

    // ---- tools: apk, screenshot, adb ---------------------------------------------------------

    private bool _apkInstalling;

    private void ApkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { State: SessionState.Running } s) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Install on " + s.Instance.Name,
            Filter = "Android packages (*.apk, *.apks, *.xapk)|*.apk;*.apks;*.xapk;*.zip|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) _ = InstallApksAsync(s, dlg.FileNames);
    }

    private async Task InstallApksAsync(EmulatorSession s, IReadOnlyList<string> files)
    {
        if (_apkInstalling) { ToolsStatus.Text = "An install is already running."; return; }
        _apkInstalling = true;
        RefreshSelected();
        var progress = new Progress<string>(m => ToolsStatus.Text = m);
        try
        {
            int ok = 0;
            foreach (var file in files)
            {
                if (s.State != SessionState.Running) { ToolsStatus.Text = "The instance stopped."; break; }
                ToolsStatus.Text = $"Installing {Path.GetFileName(file)} on {s.Instance.Name}...";
                var r = await s.InstallApkAsync(_state.Adb, file, progress);
                if (r.Ok) ok++;
                else
                {
                    ToolsStatus.Text = $"{Path.GetFileName(file)} failed.";
                    MessageBox.Show(this, r.Output.Length > 0 ? r.Output : "adb reported an error.", $"Install {Path.GetFileName(file)}", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            if (ok > 0) ToolsStatus.Text = ok == 1 ? $"Installed {Path.GetFileName(files[0])} on {s.Instance.Name}." : $"Installed {ok} of {files.Count} packages on {s.Instance.Name}.";
        }
        finally
        {
            _apkInstalling = false;
            RefreshSelected();
        }
    }

    private static string[] DroppedPackages(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && files.All(AdbTools.IsInstallable)
            ? files : [];

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = SelectedSession is { State: SessionState.Running } && !_apkInstalling && DroppedPackages(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var files = DroppedPackages(e);
        if (files.Length == 0) return;
        if (SelectedSession is not { State: SessionState.Running } s) { ToolsStatus.Text = "Launch the instance first, then drop the APK."; return; }
        _ = InstallApksAsync(s, files);
    }

    private async void ShotBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { State: SessionState.Running } s) return;
        try
        {
            Directory.CreateDirectory(_state.Paths.Screenshots);
            var file = Path.Combine(_state.Paths.Screenshots, $"{s.Instance.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            var png = await s.Control.ScreenshotPngAsync();
            await File.WriteAllBytesAsync(file, png);
            ToolsStatus.Text = "Saved " + file;
        }
        catch (Exception ex) { ToolsStatus.Text = "Screenshot failed: " + ex.Message; }
    }

    private void AdbBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { State: SessionState.Running } s) return;
        try { _state.Adb.OpenShell(s.Running.AdbSerial); }
        catch (Exception ex) { ToolsStatus.Text = "adb shell: " + ex.Message; }
    }

    private void ShotsFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_state.Paths.Screenshots);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", _state.Paths.Screenshots) { UseShellExecute = true });
        }
        catch (Exception ex) { ToolsStatus.Text = ex.Message; }
    }

    // ---- keymap ------------------------------------------------------------------------------

    private (string File, KeymapProfile Profile)? SelectedProfile =>
        ProfileCombo.SelectedItem is string name ? _profiles.FirstOrDefault(p => p.Profile.Name == name) : null;

    private void ApplyProfile(EmulatorSession session, OverlayWindow overlay)
    {
        var p = SelectedProfile;
        if (p is null || p.Value.Profile is null) return;
        session.Engine.Profile = p.Value.Profile.Clone();
        overlay.SetProfile(p.Value.Profile.Clone(), p.Value.File);
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is { } inst && SelectedProfile is { } p)
        {
            if (inst.Config.KeymapProfile != p.Profile.Name)
            {
                inst.Config.KeymapProfile = p.Profile.Name;
                try { inst.Config.Save(inst.KitsuneJson); } catch { }
            }
            if (_sessions.TryGetValue(inst.Name, out var s) && _overlays.TryGetValue(inst.Name, out var o)) ApplyProfile(s, o);
        }
        RefreshSelected();
    }

    private void KeymapEnabledBox_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } s) s.KeymapEnabled = KeymapEnabledBox.IsChecked == true;
    }

    private void ShowBadgesBox_Click(object sender, RoutedEventArgs e)
    {
        _state.Settings.ShowOverlayBadges = ShowBadgesBox.IsChecked == true;
        foreach (var o in _overlays.Values) o.ShowBadges = _state.Settings.ShowOverlayBadges;
    }

    private void EditOverlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inst || !_overlays.TryGetValue(inst.Name, out var overlay)) return;
        if (SelectedProfile is null) return;
        overlay.BeginEdit();
        if (SelectedSession is { } s && s.Window.Found) Win32.SetForegroundWindow(s.Window.Hwnd);
    }

    private void NewProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt.Show(this, "New keymap profile", "Profile name:", "My game");
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = new KeymapProfile
        {
            Name = name.Trim(),
            Orientation = Selected is { } i && i.Config.Width > i.Config.Height ? "landscape" : "portrait",
            Bindings = [new Binding { Kind = BindingKind.ToggleKeymap, Key = "F12", Label = "Toggle keymap" }],
        };
        var path = _state.ProfilePath(profile.Name);
        if (File.Exists(path)) { MessageBox.Show(this, "A profile with that name already exists.", "Kitsune"); return; }
        profile.Save(path);
        RefreshProfiles();
        ProfileCombo.SelectedItem = profile.Name;
    }

    private void OnProfileSaved(Instance inst, KeymapProfile profile)
    {
        if (_sessions.TryGetValue(inst.Name, out var s)) s.Engine.Profile = profile.Clone();
        RefreshProfiles();
        ProfileCombo.SelectedItem = profile.Name;
    }

    // ---- macros ------------------------------------------------------------------------------

    private IReadOnlyList<(string File, Macro Macro)> _macros = [];

    private void RefreshMacros()
    {
        var current = MacroCombo.SelectedItem as string ?? _state.Settings.LastMacro;
        var list = new List<(string, Macro)>();
        if (Directory.Exists(_state.Paths.Macros))
            foreach (var f in Directory.EnumerateFiles(_state.Paths.Macros, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { list.Add((f, Macro.Load(f))); } catch { }
            }
        _macros = list;
        MacroCombo.ItemsSource = _macros.Select(m => m.Macro.Name).ToList();
        MacroCombo.SelectedItem = _macros.Select(m => m.Macro.Name).FirstOrDefault(n => n == current) ?? _macros.Select(m => m.Macro.Name).FirstOrDefault();
        RefreshMacroButtons();
    }

    private (string File, Macro Macro)? SelectedMacro =>
        MacroCombo.SelectedItem is string name ? _macros.FirstOrDefault(m => m.Macro.Name == name) : null;

    private void RefreshMacroButtons()
    {
        var s = SelectedSession;
        bool running = s?.State == SessionState.Running;
        bool recording = s?.Recorder.Recording == true;
        MacroRecordBtn.Content = recording ? $"Stop recording (F8) · {s!.Recorder.StepCount} steps" : "Record (F8)";
        MacroRecordBtn.IsEnabled = running;
        MacroPlayBtn.IsEnabled = running && !recording && SelectedMacro is not null && s?.IsPlaying != true;
        MacroStopBtn.IsEnabled = s?.IsPlaying == true;
        MacroDeleteBtn.IsEnabled = SelectedMacro is not null;
        MacroStatusText.Text = s is null ? "" : recording ? $"recording {s.Recorder.ElapsedMs / 1000.0:F1}s" : s.MacroStatus;
    }

    private void ToggleRecording(EmulatorSession s)
    {
        if (s.State != SessionState.Running) return;
        if (!s.Recorder.Recording)
        {
            s.StopMacro();
            var name = $"{s.Instance.Name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            s.Recorder.Start(name);
            MacroStatusText.Text = "recording";
        }
        else
        {
            var macro = s.Recorder.Stop();
            if (macro is not null && macro.Steps.Count > 0)
            {
                Directory.CreateDirectory(_state.Paths.Macros);
                var file = Path.Combine(_state.Paths.Macros, SafeName(macro.Name) + ".json");
                macro.Save(file);
                _state.Settings.LastMacro = macro.Name;
                RefreshMacros();
                MacroCombo.SelectedItem = macro.Name;
                MacroStatusText.Text = $"saved {macro.Steps.Count} steps ({macro.DurationMs / 1000.0:F1}s) to {Path.GetFileName(file)}";
            }
            else MacroStatusText.Text = "nothing recorded";
        }
        RefreshMacroButtons();
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' or '.' ? c : '_')).Trim();

    private void MacroRecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } s) ToggleRecording(s);
    }

    private async void MacroPlayBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { } s || SelectedMacro is not { } m) return;
        int repeat = int.TryParse(MacroRepeatBox.Text.Trim(), out var r) ? Math.Max(0, r) : 1;
        double speed = double.TryParse(MacroSpeedBox.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp) && sp > 0 ? sp : 1.0;
        _state.Settings.LastMacro = m.Macro.Name;
        RefreshMacroButtons();
        await s.PlayMacroAsync(m.Macro, repeat, speed);
        RefreshMacroButtons();
    }

    private void MacroStopBtn_Click(object sender, RoutedEventArgs e)
    {
        SelectedSession?.StopMacro();
        RefreshMacroButtons();
    }

    private void MacroDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMacro is not { } m) return;
        if (MessageBox.Show(this, $"Delete macro '{m.Macro.Name}'?", "Kitsune", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { File.Delete(m.File); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Kitsune"); }
        RefreshMacros();
    }

    private void MacroCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedMacro is { } m) _state.Settings.LastMacro = m.Macro.Name;
        RefreshMacroButtons();
    }

    /// <summary>For the reroll window: the macros on disk, freshly read.</summary>
    internal IReadOnlyList<(string File, Macro Macro)> LoadMacros() { RefreshMacros(); return _macros; }

    // ---- components --------------------------------------------------------------------------

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunInstall(async (progress, ct) =>
        {
            await _state.Components.InstallCoreAsync(progress, ct);
            if (_state.Components.InstalledImages().Count == 0)
                await _state.Components.InstallImageAsync(Core.SystemImageRef.Parse(_state.Settings.DefaultImage), progress, ct);
        });
    }

    private async void ImagesBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ImagesDialog(_state) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedImage is { } image)
            await RunInstall((progress, ct) => _state.Components.InstallImageAsync(image, progress, ct));
    }

    private void CancelInstallBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelInstallBtn.IsEnabled = false;
        InstallStatus.Text = "Cancelling...";
        try { _installCts?.Cancel(); } catch { }
    }

    private async Task RunInstall(Func<IProgress<DownloadProgress>, CancellationToken, Task> work)
    {
        if (_installing) return;
        _installing = true;
        using var cts = new CancellationTokenSource();
        _installCts = cts;
        RefreshHeader();
        CancelInstallBtn.Visibility = Visibility.Visible;
        CancelInstallBtn.IsEnabled = true;
        InstallProgress.Visibility = Visibility.Visible;
        var progress = new Progress<DownloadProgress>(p =>
        {
            InstallProgress.Value = p.Phase is "extracting" or "verifying" ? 1 : p.Fraction;
            InstallProgress.IsIndeterminate = p.Phase is "extracting";
            InstallStatus.Text = p.Phase switch
            {
                "downloading" => $"{p.Package}: {p.BytesDone / 1048576.0:F0} / {p.BytesTotal / 1048576.0:F0} MB",
                "verifying" => $"{p.Package}: verifying SHA-1",
                "extracting" => $"{p.Package}: extracting",
                "done" => $"{p.Package}: installed",
                _ => $"{p.Package}: {p.Phase}",
            };
        });
        try
        {
            _state.Components.InvalidateCatalog();
            await work(progress, cts.Token);
            InstallStatus.Text = "Components are up to date.";
        }
        catch (OperationCanceledException)
        {
            InstallStatus.Text = "Install cancelled. Finished downloads are kept and a partial one resumes next time.";
        }
        catch (Exception ex)
        {
            InstallStatus.Text = "Install failed: " + ex.Message;
        }
        finally
        {
            _installing = false;
            _installCts = null;
            CancelInstallBtn.Visibility = Visibility.Collapsed;
            InstallProgress.Visibility = Visibility.Collapsed;
            InstallProgress.IsIndeterminate = false;
            RefreshAll();
        }
    }

    private async void AehdBtn_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Install the Android Emulator Hypervisor Driver (AEHD)?\n\nThis is Google's kernel driver that gives the emulator direct VT-x access. Windows will show one UAC prompt. It can be removed later with 'kitsune aehd uninstall'.",
            "Kitsune", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        AehdBtn.IsEnabled = false;
        InstallStatus.Text = "Installing AEHD (waiting for UAC)...";
        var (ok, output) = await AehdInstaller.InstallAsync(_state.Paths);
        InstallStatus.Text = ok ? "AEHD installed and running." : "AEHD install did not complete. " + output;
        _state.RefreshHost();
        RefreshHeader();
        RefreshSelected();
    }
}
