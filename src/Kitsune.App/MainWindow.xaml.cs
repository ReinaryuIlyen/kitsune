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

    public MainWindow()
    {
        InitializeComponent();
        ShowBadgesBox.IsChecked = _state.Settings.ShowOverlayBadges;
        _hooks.IsActive = HooksActive;
        _hooks.OnKey = (vk, down) => ActiveSession()?.Engine.OnKey(vk, down) == true;
        _hooks.OnMouseButton = (b, down, x, y) => ActiveSession()?.Engine.OnMouseButton(b, down, x, y) == true;
        _hooks.OnMouseMove = (dx, dy, x, y) => ActiveSession()?.Engine.OnMouseMove(dx, dy, x, y) == true;
        _hooks.OnWheel = delta => ActiveSession()?.Engine.OnWheel(delta) == true;
        _logTimer.Tick += (_, _) => RefreshLog();
        _logTimer.Start();
        Loaded += (_, _) =>
        {
            try { _hooks.Install(); } catch (Exception ex) { StateText.Text = "Input hooks unavailable: " + ex.Message; }
            RefreshAll();
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
        return s.Engine.Enabled;
    }

    // ---- refresh -----------------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshHeader();
        RefreshInstances();
        RefreshProfiles();
        RefreshSelected();
    }

    private void RefreshHeader()
    {
        _state.RefreshHost();
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
            StateText.Text = session is null ? "Not running." : session.State switch
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
        LaunchBtn.IsEnabled = inst is not null && !running && _state.EmulatorVersion is not null;
        ColdBtn.IsEnabled = LaunchBtn.IsEnabled;
        StopBtn.IsEnabled = running;
        EditBtn.IsEnabled = inst is not null && !running;
        FocusBtn.IsEnabled = session?.State == SessionState.Running;
        CloneBtn.IsEnabled = inst is not null && !running;
        DeleteBtn.IsEnabled = inst is not null && !running;
        EditOverlayBtn.IsEnabled = session?.State == SessionState.Running && ProfileCombo.SelectedItem is not null;
        KeymapEnabledBox.IsEnabled = session?.State == SessionState.Running;
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
            SystemImage = images.FirstOrDefault(i => i.PackagePath == _state.Settings.DefaultImage).PackagePath is { Length: > 0 } p ? p : images[0].PackagePath,
            Cores = Defaults.Cores(_state.Host),
            RamMb = Defaults.RamMb(_state.Host),
        };
        cfg.ApplyProfile(DeviceProfile.Phone1080p2400);
        var dlg = new InstanceDialog(_state, cfg, isNew: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try { _state.Avds.Create(dlg.Config); _state.Settings.LastInstance = dlg.Config.Name; }
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
        _state.RefreshHost();
        try
        {
            if (_sessions.TryGetValue(inst.Name, out var old)) { old.Dispose(); _sessions.Remove(inst.Name); }
            if (_overlays.TryGetValue(inst.Name, out var oldOverlay)) { oldOverlay.Close(); _overlays.Remove(inst.Name); }

            var session = new EmulatorSession(new EmulatorLauncher(_state.Paths), inst, _state.Host, new LaunchOptions(ColdBoot: cold));
            _sessions[inst.Name] = session;
            session.Changed += () => Dispatcher.BeginInvoke(() => { RefreshSelected(); RefreshHeader(); });

            var overlay = new OverlayWindow(session, _hooks) { ShowBadges = _state.Settings.ShowOverlayBadges };
            _overlays[inst.Name] = overlay;
            ApplyProfile(session, overlay);
            overlay.ProfileSaved += profile => OnProfileSaved(inst, profile);
            overlay.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot launch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshSelected();
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { } session) return;
        StopBtn.IsEnabled = false;
        await session.StopAsync();
        RefreshSelected();
    }

    private void FocusBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is { } s && s.Window.Found) Win32.SetForegroundWindow(s.Window.Hwnd);
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

    // ---- components --------------------------------------------------------------------------

    private async void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunInstall(async progress =>
        {
            await _state.Components.InstallCoreAsync(progress, CancellationToken.None);
            if (_state.Components.InstalledImages().Count == 0)
                await _state.Components.InstallImageAsync(Core.SystemImageRef.Parse(_state.Settings.DefaultImage), progress, CancellationToken.None);
        });
    }

    private async void ImagesBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ImagesDialog(_state) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedImage is { } image)
            await RunInstall(progress => _state.Components.InstallImageAsync(image, progress, CancellationToken.None));
    }

    private async Task RunInstall(Func<IProgress<DownloadProgress>, Task> work)
    {
        if (_installing) return;
        _installing = true;
        RefreshHeader();
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
            await work(progress);
            InstallStatus.Text = "Components are up to date.";
        }
        catch (Exception ex)
        {
            InstallStatus.Text = "Install failed: " + ex.Message;
        }
        finally
        {
            _installing = false;
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
        RefreshHeader();
        RefreshSelected();
    }
}
