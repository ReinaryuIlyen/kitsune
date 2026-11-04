using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Kitsune.App.Native;
using Kitsune.App.Services;
using Kitsune.Core.Input;
using Binding = Kitsune.Core.Input.Binding;

namespace Kitsune.App.Views;

/// <summary>
/// Transparent window that sits exactly over the guest display. In play mode it is click-through and
/// only draws key badges. In edit mode it takes the mouse so bindings can be dragged into place.
/// In mouse-look mode it becomes the capture surface so the cursor is hidden and the game never sees it.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly EmulatorSession _session;
    private readonly LowLevelHooks _hooks;
    private IntPtr _hwnd;
    private KeymapProfile _profile = new();
    private KeymapProfile? _editing;
    private string? _profileFile;
    private bool _showBadges = true;
    private FrameworkElement? _selected;
    private Point _dragOffset;
    private bool _dragging;
    private bool _aim;

    public event Action<KeymapProfile>? ProfileSaved;

    public bool EditMode => _editing is not null;
    public bool IsCaptureSurface => _aim;

    public bool ShowBadges
    {
        get => _showBadges;
        set { _showBadges = value; Rebuild(); }
    }

    public OverlayWindow(EmulatorSession session, LowLevelHooks hooks)
    {
        _session = session;
        _hooks = hooks;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeClickThrough(_hwnd, true);
            Reposition();
        };
        _session.WindowMoved += () => Dispatcher.BeginInvoke(Reposition);
        _session.Changed += () => Dispatcher.BeginInvoke(() => { Reposition(); });
        _session.Engine.AimModeChanged += on => Dispatcher.BeginInvoke(() => SetAim(on));
        _session.Engine.EnabledChanged += _ => Dispatcher.BeginInvoke(Rebuild);
        var vis = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        vis.Tick += (_, _) => { UpdateVisibility(); UpdateMacroBanner(); };
        vis.Start();
        Closed += (_, _) => vis.Stop();
        KeyDown += Overlay_KeyDown;
    }

    public bool IsForegroundWindow() => _hwnd != IntPtr.Zero && Win32.GetForegroundWindow() == _hwnd;

    public void SetProfile(KeymapProfile profile, string? file)
    {
        _profile = profile;
        _profileFile = file;
        if (!EditMode) Rebuild();
    }

    // ---- geometry ----------------------------------------------------------------------------

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        var w = _session.Window;
        if (!w.Found || w.Minimized || w.DisplayRect.Width <= 0) { Hide(); return; }
        var r = w.DisplayRect;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        if (_aim) _hooks.PinCursorTo = new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
        Dispatcher.BeginInvoke(Rebuild, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void UpdateMacroBanner()
    {
        if (_session.Recorder.Recording)
        {
            MacroBannerText.Text = $"● REC {_session.Recorder.ElapsedMs / 1000.0:F0}s · {_session.Recorder.StepCount} steps · F9 checkpoint · F8 stop";
            MacroBanner.Visibility = Visibility.Visible;
        }
        else if (_session.IsPlaying)
        {
            MacroBannerText.Text = "▶ " + _session.MacroStatus + " · F10 stop";
            MacroBanner.Visibility = Visibility.Visible;
        }
        else MacroBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateVisibility()
    {
        if (_hwnd == IntPtr.Zero) return;
        var w = _session.Window;
        bool running = _session.State == SessionState.Running;
        bool front = w.IsForeground() || IsForegroundWindow() || (Owner is not null && Owner.IsActive);
        bool busy = _session.Recorder.Recording || _session.IsPlaying;
        bool show = running && w.Found && !w.Minimized && (EditMode || _aim || front || busy);
        if (show && !IsVisible) { Show(); Reposition(); }
        else if (!show && IsVisible) Hide();
    }

    private double Scale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    private (double W, double H) CanvasSize()
    {
        var r = _session.Window.DisplayRect;
        var s = Scale;
        return (r.Width / s, r.Height / s);
    }

    // ---- badges ------------------------------------------------------------------------------

    private void Rebuild()
    {
        BadgeCanvas.Children.Clear();
        var profile = _editing ?? _profile;
        bool show = EditMode || (_showBadges && _session.Engine.Enabled);
        if (!show) return;
        var (cw, ch) = CanvasSize();
        if (cw <= 0 || ch <= 0) return;

        foreach (var b in profile.Bindings)
        {
            if (b.Kind == BindingKind.ToggleKeymap || (b.Kind == BindingKind.AndroidKey && !EditMode)) continue;
            var el = MakeBadge(b, cw, ch);
            BadgeCanvas.Children.Add(el);
        }
    }

    private FrameworkElement MakeBadge(Binding b, double cw, double ch)
    {
        double x = b.X * cw, y = b.Y * ch;
        var short_ = Math.Min(cw, ch);
        FrameworkElement el;
        var accent = (Brush)FindResource("Accent");
        var text = Label(b);
        switch (b.Kind)
        {
            case BindingKind.Joystick:
            {
                double r = Math.Max(24, b.Radius * short_);
                var g = new Grid { Width = r * 2, Height = r * 2 };
                g.Children.Add(new Ellipse { Stroke = accent, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 232, 115, 74)) });
                g.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
                el = g;
                Canvas.SetLeft(el, x - r); Canvas.SetTop(el, y - r);
                break;
            }
            case BindingKind.AimToggle:
            {
                double r = 22;
                var g = new Grid { Width = r * 2, Height = r * 2 };
                g.Children.Add(new Ellipse { Stroke = accent, StrokeThickness = 2, StrokeDashArray = [3, 2] });
                g.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
                el = g;
                Canvas.SetLeft(el, x - r); Canvas.SetTop(el, y - r);
                break;
            }
            case BindingKind.Swipe:
            {
                var g = new Grid();
                var line = new Line { X1 = 0, Y1 = 0, X2 = (b.ToX - b.X) * cw, Y2 = (b.ToY - b.Y) * ch, Stroke = accent, StrokeThickness = 2, StrokeDashArray = [4, 2] };
                var badge = Pill(text, accent);
                g.Children.Add(line);
                g.Children.Add(badge);
                badge.HorizontalAlignment = HorizontalAlignment.Left; badge.VerticalAlignment = VerticalAlignment.Top;
                badge.Margin = new Thickness(-14, -12, 0, 0);
                el = g;
                Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
                break;
            }
            default:
            {
                el = Pill(text, accent);
                el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(el, x - el.DesiredSize.Width / 2); Canvas.SetTop(el, y - el.DesiredSize.Height / 2);
                break;
            }
        }
        el.Opacity = EditMode ? 1.0 : 0.55;
        el.Tag = b;
        if (EditMode)
        {
            el.Cursor = Cursors.SizeAll;
            el.MouseLeftButtonDown += Badge_MouseLeftButtonDown;
            el.MouseMove += Badge_MouseMove;
            el.MouseLeftButtonUp += Badge_MouseLeftButtonUp;
        }
        return el;
    }

    private static Border Pill(string text, Brush accent) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(200, 30, 32, 38)),
        BorderBrush = accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(8, 3, 8, 3),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 12 },
    };

    private static string Label(Binding b) => b.Kind switch
    {
        BindingKind.Joystick => b.Label ?? $"{b.Up}\n{b.Left} {b.Right}\n{b.Down}",
        BindingKind.AimToggle => b.Label ?? $"{b.Key} aim",
        BindingKind.Repeat => (b.Label ?? b.Key ?? "?") + " ↻",
        BindingKind.Pinch => (b.Label ?? b.Key ?? "?") + (b.Delta >= 0 ? " +" : " −"),
        BindingKind.AndroidKey => $"{b.Key} → {b.AndroidKey}",
        BindingKind.TapAtCursor => (b.Label ?? b.Key ?? "?") + " @cursor",
        _ => b.Label is null ? b.Key ?? "?" : $"{b.Key} {b.Label}",
    };

    // ---- aim mode ----------------------------------------------------------------------------

    private void SetAim(bool on)
    {
        _aim = on;
        AimBanner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_hwnd == IntPtr.Zero) return;
        if (on)
        {
            Win32.MakeClickThrough(_hwnd, false);
            Cursor = Cursors.None;
            var r = _session.Window.DisplayRect;
            var centre = new POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 };
            _hooks.PinCursorTo = centre;
            Win32.SetCursorPos(centre.X, centre.Y);
            UpdateVisibility();
        }
        else
        {
            _hooks.PinCursorTo = null;
            Cursor = Cursors.Arrow;
            if (!EditMode) Win32.MakeClickThrough(_hwnd, true);
        }
    }

    // ---- editing -----------------------------------------------------------------------------

    public void BeginEdit()
    {
        if (_session.Engine.AimMode) _session.Engine.ReleaseAll();
        _editing = _profile.Clone();
        Toolbar.Visibility = Visibility.Visible;
        ToolbarTitle.Text = _editing.Name;
        Win32.MakeClickThrough(_hwnd, false);
        Rebuild();
        UpdateVisibility();
        Activate();
    }

    private void EndEdit()
    {
        _editing = null;
        _selected = null;
        Toolbar.Visibility = Visibility.Collapsed;
        Win32.MakeClickThrough(_hwnd, true);
        Rebuild();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null) return;
        var problems = _editing.Validate();
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join('\n', problems), "Keymap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _profile = _editing;
        var file = _profileFile ?? App.State.ProfilePath(_profile.Name);
        try { _profile.Save(file); _profileFile = file; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Keymap", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        EndEdit();
        ProfileSaved?.Invoke(_profile);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => EndEdit();

    private void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (!EditMode) return;
        if (e.Key == Key.Delete && _selected?.Tag is Binding b) { _editing!.Bindings.Remove(b); _selected = null; Rebuild(); }
        if (e.Key == Key.Escape) EndEdit();
    }

    private Point _lastRightClick;

    private void Canvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!EditMode) return;
        _lastRightClick = e.GetPosition(BadgeCanvas);
        var menu = new ContextMenu();
        void Item(string header, BindingKind kind) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => AddAt(kind, _lastRightClick); menu.Items.Add(mi); }
        Item("Tap here", BindingKind.Tap);
        Item("Autofire here", BindingKind.Repeat);
        Item("Joystick here", BindingKind.Joystick);
        Item("Mouse-look anchor here", BindingKind.AimToggle);
        Item("Swipe from here", BindingKind.Swipe);
        Item("Pinch here", BindingKind.Pinch);
        Item("Android key", BindingKind.AndroidKey);
        Item("Tap at cursor position", BindingKind.TapAtCursor);
        menu.IsOpen = true;
    }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EditMode && e.Source == BadgeCanvas) { _selected = null; }
    }

    private void AddTap_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.Tap, Centre());
    private void AddJoystick_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.Joystick, new Point(CanvasSize().W * 0.18, CanvasSize().H * 0.72));
    private void AddAim_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.AimToggle, new Point(CanvasSize().W * 0.62, CanvasSize().H * 0.5));
    private void AddSwipe_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.Swipe, Centre());
    private void AddRepeat_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.Repeat, Centre());
    private void AddPinch_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.Pinch, Centre());
    private void AddAndroidKey_Click(object s, RoutedEventArgs e) => AddAt(BindingKind.AndroidKey, new Point(CanvasSize().W * 0.5, CanvasSize().H * 0.05));

    private Point Centre() { var (w, h) = CanvasSize(); return new Point(w / 2, h / 2); }

    private void AddAt(BindingKind kind, Point p)
    {
        if (_editing is null) return;
        var (cw, ch) = CanvasSize();
        var b = new Binding { Kind = kind, X = Math.Clamp(p.X / cw, 0, 1), Y = Math.Clamp(p.Y / ch, 0, 1) };
        if (kind == BindingKind.Swipe) { b.ToX = Math.Clamp(b.X, 0, 1); b.ToY = Math.Clamp(b.Y - 0.2, 0, 1); }
        if (kind == BindingKind.Joystick) { b.Up = "W"; b.Down = "S"; b.Left = "A"; b.Right = "D"; }
        var dlg = new BindingDialog(b) { Owner = this };
        if (dlg.ShowDialog() == true) { _editing.Bindings.Add(dlg.Result); Rebuild(); }
    }

    private void Badge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not Binding b) return;
        _selected = el;
        if (e.ClickCount == 2)
        {
            var dlg = new BindingDialog(b.Clone()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                var idx = _editing!.Bindings.IndexOf(b);
                if (idx >= 0) _editing.Bindings[idx] = dlg.Result;
                Rebuild();
            }
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragOffset = e.GetPosition(el);
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Badge_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || sender is not FrameworkElement el || el.Tag is not Binding b) return;
        var p = e.GetPosition(BadgeCanvas);
        var (cw, ch) = CanvasSize();
        double left = p.X - _dragOffset.X, top = p.Y - _dragOffset.Y;
        Canvas.SetLeft(el, left); Canvas.SetTop(el, top);
        double cx = left + el.ActualWidth / 2, cy = top + el.ActualHeight / 2;
        if (b.Kind == BindingKind.Swipe)
        {
            double dx = b.ToX - b.X, dy = b.ToY - b.Y;
            b.X = Math.Clamp(left / cw, 0, 1); b.Y = Math.Clamp(top / ch, 0, 1);
            b.ToX = Math.Clamp(b.X + dx, 0, 1); b.ToY = Math.Clamp(b.Y + dy, 0, 1);
        }
        else
        {
            b.X = Math.Clamp(cx / cw, 0, 1); b.Y = Math.Clamp(cy / ch, 0, 1);
        }
    }

    private void Badge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el) el.ReleaseMouseCapture();
        _dragging = false;
    }
}
