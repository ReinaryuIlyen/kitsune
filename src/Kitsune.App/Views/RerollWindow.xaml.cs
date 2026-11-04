using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kitsune.App.Services;
using Kitsune.Core.Avd;
using Kitsune.Core.Emulator;
using Kitsune.Core.Reroll;

namespace Kitsune.App.Views;

public partial class RerollWindow : Window
{
    private sealed class CloneRow : INotifyPropertyChanged
    {
        public RerollClone Clone { get; }
        public string Name => Clone.Name;
        public string Status { get; private set; } = "";
        public bool IsRunning { get; private set; }
        public bool CanLaunch { get; private set; } = true;
        public CloneRow(RerollClone c) => Clone = c;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string status, bool running, bool canLaunch)
        {
            Status = status; IsRunning = running; CanLaunch = canLaunch;
            foreach (var p in new[] { nameof(Status), nameof(IsRunning), nameof(CanLaunch) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        }
    }

    private readonly AppState _state;
    private readonly MainWindow _main;
    private readonly RerollManager _rm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<int> _reservedPorts = [];
    private IReadOnlyList<RerollSet> _sets = [];
    private RerollSet? _set;
    private List<CloneRow> _rows = [];
    private bool _busy;

    public RerollWindow(AppState state, MainWindow main)
    {
        _state = state;
        _main = main;
        _rm = new RerollManager(state.Paths);
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshRows();
        Loaded += (_, _) => { RefreshSets(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void RefreshSets()
    {
        var current = _set?.Name;
        _sets = _rm.List();
        SetCombo.ItemsSource = _sets.Select(s => s.Name).ToList();
        SetCombo.SelectedItem = _sets.Select(s => s.Name).FirstOrDefault(n => n == current) ?? _sets.Select(s => s.Name).FirstOrDefault();
        if (_sets.Count == 0) { _set = null; _rows = []; CloneList.ItemsSource = null; SetInfo.Text = "No reroll sets yet. Create one from a stopped instance that has the game installed."; }
        UpdateButtons();
    }

    private void SetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _set = _sets.FirstOrDefault(s => s.Name == SetCombo.SelectedItem as string);
        _rows = _set?.Clones.Select(c => new CloneRow(c)).ToList() ?? [];
        CloneList.ItemsSource = _rows;
        RefreshRows();
        UpdateButtons();
    }

    private void RefreshRows()
    {
        if (_set is null) return;
        int running = 0;
        foreach (var row in _rows)
        {
            var session = _main.Sessions.TryGetValue(row.Name, out var s) ? s : null;
            bool live = session is not null && session.State is SessionState.Starting or SessionState.Booting or SessionState.Running;
            if (live) running++;
            string status = session is null
                ? (RerollManager.IsAvdRunning(_set.CloneDir(row.Clone)) ? "running outside the launcher" : $"stopped · reset {row.Clone.LastReset.LocalDateTime:t}")
                : session.State switch
                {
                    SessionState.Starting => "starting...",
                    SessionState.Booting => $"booting... (adb {session.Running.AdbSerial})",
                    SessionState.Running => $"running · booted in {session.BootSeconds:F0}s · adb {session.Running.AdbSerial} · gRPC {session.Running.GrpcPort}",
                    SessionState.Stopping => "stopping...",
                    SessionState.Exited => "exited" + (session.Error is null ? "" : ": " + session.Error),
                    SessionState.Failed => "failed: " + session.Error,
                    _ => "",
                };
            bool canLaunch = !live && !_busy && !RerollManager.IsAvdRunning(_set.CloneDir(row.Clone));
            row.Update(status, live, canLaunch);
        }
        long overlayBytes = _set.Clones.Sum(c => RerollManager.CloneOwnedBytes(_set.CloneDir(c)));
        SetInfo.Text = $"{_set.Clones.Count} clones of '{_set.Template}' · {_set.RamMb} MB / {_set.Cores} vCPU each · {running} running · overlays {overlayBytes / 1048576.0:F0} MB · room for {RerollManager.MaxClonesFor(_state.Host, _set.RamMb)} more right now";
    }

    private void UpdateButtons()
    {
        bool have = _set is not null;
        LaunchAllBtn.IsEnabled = have && !_busy;
        StopAllBtn.IsEnabled = have;
        ResetAllBtn.IsEnabled = have && !_busy;
        TileBtn.IsEnabled = have;
        AddCloneBtn.IsEnabled = have && !_busy;
        DeleteSetBtn.IsEnabled = have && !_busy;
    }

    // ---- set creation ------------------------------------------------------------------------

    private void NewSetBtn_Click(object sender, RoutedEventArgs e)
    {
        var instances = _state.Avds.List();
        TemplateCombo.ItemsSource = instances.Select(i => i.Name).ToList();
        TemplateCombo.SelectedItem = _state.Settings.LastInstance is { } last && instances.Any(i => i.Name == last) ? last : instances.FirstOrDefault()?.Name;
        NewNameBox.Text = NextSetName();
        CountBox.Text = Math.Min(4, RerollManager.MaxClonesFor(_state.Host, 3072)).ToString();
        CreateStatus.Text = "";
        NewSetPanel.Visibility = Visibility.Visible;
    }

    private string NextSetName()
    {
        var names = _sets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++) { var name = n == 1 ? "reroll" : $"reroll-{n}"; if (!names.Contains(name)) return name; }
    }

    private void CancelNewSet_Click(object sender, RoutedEventArgs e) => NewSetPanel.Visibility = Visibility.Collapsed;

    private async void CreateSetBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var template = _state.Avds.Get(TemplateCombo.SelectedItem as string ?? "") ?? throw new InvalidOperationException("Pick a template instance.");
            if (_main.Sessions.TryGetValue(template.Name, out var s) && s.State is SessionState.Starting or SessionState.Booting or SessionState.Running)
                throw new InvalidOperationException($"Stop '{template.Name}' first; its disks are frozen as the base.");
            int count = int.Parse(CountBox.Text.Trim()), ram = int.Parse(RamBox.Text.Trim()), cores = int.Parse(CoresBox.Text.Trim());
            _busy = true; UpdateButtons(); CreateSetBtn.IsEnabled = false;
            var progress = new Progress<string>(m => CreateStatus.Text = m);
            var set = await _rm.CreateAsync(NewNameBox.Text.Trim(), template, count, ram, cores, SnapshotsBox.IsChecked == true, progress);
            NewSetPanel.Visibility = Visibility.Collapsed;
            RefreshSets();
            SetCombo.SelectedItem = set.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; CreateSetBtn.IsEnabled = true; UpdateButtons(); }
    }

    private void DeleteSetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        if (MessageBox.Show(this, $"Delete reroll set '{_set.Name}' and all of its clones? Instances you kept are not affected.", "Reroll", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _rm.Delete(_set); RefreshSets(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void AddCloneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        try { await _rm.AddCloneAsync(_set); _rows.Add(new CloneRow(_set.Clones[^1])); CloneList.ItemsSource = null; CloneList.ItemsSource = _rows; RefreshRows(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- launching ---------------------------------------------------------------------------

    private EmulatorSession? Launch(RerollClone clone)
    {
        if (_set is null) return null;
        RerollManager.TrimSnapshots(_set, clone);
        var inst = _rm.CloneInstance(_set, clone);
        int console = EmulatorLauncher.FindFreeConsolePort(_reservedPorts);
        _reservedPorts.Add(console); _reservedPorts.Add(console + 1);
        int grpc = EmulatorLauncher.FindFreeTcpPort(8554, _reservedPorts);
        _reservedPorts.Add(grpc);
        var opts = new LaunchOptions(ConsolePort: console, GrpcPort: grpc, NoSnapshot: !_set.SaveSnapshots);
        var session = _main.StartSession(inst, opts);
        var set = _set;
        session.Changed += () =>
        {
            if (session.State is SessionState.Exited or SessionState.Failed)
                Dispatcher.BeginInvoke(() => RerollManager.TrimSnapshots(set, clone));
        };
        return session;
    }

    private void CloneLaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CloneRow row) return;
        try { Launch(row.Clone); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshRows();
    }

    private async void LaunchAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        _busy = true; UpdateButtons();
        try
        {
            int started = 0;
            foreach (var row in _rows)
            {
                if (row.IsRunning) continue;
                _state.RefreshHost();
                if (RerollManager.MaxClonesFor(_state.Host, _set.RamMb) < 1)
                {
                    Status.Text = $"Stopped after {started}: only {_state.Host.AvailableRamBytes / 1073741824.0:F1} GB of RAM is free, not enough for another {_set.RamMb} MB clone.";
                    break;
                }
                try { Launch(row.Clone); started++; }
                catch (Exception ex) { Status.Text = $"{row.Name}: {ex.Message}"; break; }
                RefreshRows();
                await Task.Delay(1500); // stagger the boot storm so the first clones get through their cold boot
            }
            if (started > 0) Status.Text = $"Launched {started} clone(s).";
        }
        finally { _busy = false; UpdateButtons(); RefreshRows(); }
    }

    private async void CloneStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CloneRow row) return;
        await _main.StopSessionAsync(row.Name);
        RefreshRows();
    }

    private async void StopAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        await Task.WhenAll(_rows.Where(r => r.IsRunning).Select(r => _main.StopSessionAsync(r.Name)));
        RefreshRows();
    }

    private void CloneShow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CloneRow row && _main.Sessions.TryGetValue(row.Name, out var s) && s.Window.Found)
            Native.Win32.SetForegroundWindow(s.Window.Hwnd);
    }

    // ---- reset / keep ------------------------------------------------------------------------

    private async void CloneReset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CloneRow row || _set is null) return;
        try { await _rm.ResetAsync(_set, row.Clone); Status.Text = $"{row.Name} reset to the template state."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshRows();
    }

    private async void ResetAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        int n = 0;
        foreach (var row in _rows.Where(r => r.CanLaunch))
        {
            try { await _rm.ResetAsync(_set, row.Clone); n++; } catch { }
        }
        Status.Text = $"Reset {n} clone(s).";
        RefreshRows();
    }

    private async void CloneKeep_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CloneRow row || _set is null) return;
        var name = Prompt.Show(this, "Keep this reroll", "Name for the new instance (the clone stays in the set and can be reset):", $"{_set.Template}-win");
        if (string.IsNullOrWhiteSpace(name)) return;
        _busy = true; UpdateButtons();
        try
        {
            var progress = new Progress<string>(m => Status.Text = m);
            var inst = await _rm.KeepAsync(_set, row.Clone, name.Trim(), progress);
            Status.Text = $"Kept {row.Name} as instance '{inst.Name}'.";
            _main.RefreshInstancesPublic();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Reroll", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _busy = false; UpdateButtons(); RefreshRows(); }
    }

    // ---- tiling ------------------------------------------------------------------------------

    private void TileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_set is null) return;
        var sessions = _rows.Select(r => _main.Sessions.TryGetValue(r.Name, out var s) ? s : null)
                            .Where(s => s is not null && s.Window.Found && !s.Window.Minimized).Cast<EmulatorSession>().ToList();
        if (sessions.Count == 0) { Status.Text = "No clone windows to tile yet."; return; }
        _main.TileWindows(sessions);
        Status.Text = $"Tiled {sessions.Count} window(s).";
    }
}
