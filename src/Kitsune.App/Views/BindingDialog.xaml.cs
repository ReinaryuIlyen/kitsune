using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kitsune.Core.Input;
using Binding = Kitsune.Core.Input.Binding;

namespace Kitsune.App.Views;

public partial class BindingDialog : Window
{
    private static readonly (BindingKind Kind, string Title, string Help)[] Kinds =
    [
        (BindingKind.Tap, "Tap / hold", "Touches the position while the key is held."),
        (BindingKind.TapAtCursor, "Tap at cursor", "Touches wherever the mouse cursor is while the key is held. Handy for gacha menus."),
        (BindingKind.Repeat, "Autofire", "Taps the position repeatedly while the key is held."),
        (BindingKind.Joystick, "Joystick", "Four keys move a virtual stick. The touch stays down while any of them is held."),
        (BindingKind.Swipe, "Swipe", "One scripted swipe per key press. Bind WheelUp/WheelDown for scrolling."),
        (BindingKind.Pinch, "Pinch zoom", "Two-finger pinch around the position. Bind WheelUp for zoom in and WheelDown for zoom out."),
        (BindingKind.AimToggle, "Mouse look", "Toggles mouse-look: moving the mouse drags a held touch from the anchor. Left click fires at the anchor unless LeftButton is bound elsewhere."),
        (BindingKind.AndroidKey, "Android key", "Sends a device key such as Back or Home."),
        (BindingKind.ToggleKeymap, "Toggle keymap", "Turns the whole keymap on and off so you can type."),
    ];

    public Binding Result { get; }

    public BindingDialog(Binding binding)
    {
        Result = binding;
        InitializeComponent();
        KindCombo.ItemsSource = Kinds.Select(k => k.Title).ToList();
        KindCombo.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == binding.Kind);
        AndroidCombo.ItemsSource = AndroidKeys.AllNames.ToList();
        KeyBox.Text = binding.Key ?? "";
        LabelBox.Text = binding.Label ?? "";
        XBox.Text = F(binding.X); YBox.Text = F(binding.Y);
        UpBox.Text = binding.Up ?? ""; DownBox.Text = binding.Down ?? ""; LeftBox.Text = binding.Left ?? ""; RightBox.Text = binding.Right ?? "";
        RadiusBox.Text = F(binding.Radius); RampBox.Text = binding.RampMs.ToString();
        ToXBox.Text = F(binding.ToX); ToYBox.Text = F(binding.ToY); DurationBox.Text = binding.DurationMs.ToString();
        IntervalBox.Text = binding.IntervalMs.ToString();
        DeltaBox.Text = F(binding.Delta); PinchDurationBox.Text = binding.DurationMs.ToString();
        SensBox.Text = F(binding.Sensitivity); ResetBox.Text = F(binding.ResetRadius); InvertBox.IsChecked = binding.InvertY;
        AndroidCombo.SelectedItem = binding.AndroidKey ?? "Back";
        Loaded += (_, _) => { if (string.IsNullOrEmpty(KeyBox.Text)) KeyBox.Focus(); };
    }

    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    private BindingKind Kind => Kinds[Math.Max(0, KindCombo.SelectedIndex)].Kind;

    private void KindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var k = Kind;
        JoystickPanel.Visibility = k == BindingKind.Joystick ? Visibility.Visible : Visibility.Collapsed;
        SwipePanel.Visibility = k == BindingKind.Swipe ? Visibility.Visible : Visibility.Collapsed;
        RepeatPanel.Visibility = k == BindingKind.Repeat ? Visibility.Visible : Visibility.Collapsed;
        PinchPanel.Visibility = k == BindingKind.Pinch ? Visibility.Visible : Visibility.Collapsed;
        AimPanel.Visibility = k == BindingKind.AimToggle ? Visibility.Visible : Visibility.Collapsed;
        AndroidPanel.Visibility = k == BindingKind.AndroidKey ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.IsEnabled = k != BindingKind.Joystick;
        Help.Text = Kinds[Math.Max(0, KindCombo.SelectedIndex)].Help;
    }

    private static string? NameFromKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return HostKeys.NameOf(vk);
    }

    private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var name = NameFromKey(e);
        if (name is null) return;
        if (name == "Escape" && KeyBox.Text.Length > 0 && e.Key == Key.Escape) { /* Escape still binds; Cancel button closes */ }
        KeyBox.Text = name;
        e.Handled = true;
    }

    private void KeyBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!KeyBox.IsKeyboardFocused) { KeyBox.Focus(); if (e.ChangedButton == MouseButton.Left) { e.Handled = true; return; } }
        KeyBox.Text = e.ChangedButton switch
        {
            MouseButton.Left => "LeftButton", MouseButton.Right => "RightButton", MouseButton.Middle => "MiddleButton",
            MouseButton.XButton1 => "XButton1", _ => "XButton2",
        };
        e.Handled = true;
    }

    private void KeyBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!KeyBox.IsKeyboardFocused) return;
        KeyBox.Text = e.Delta > 0 ? "WheelUp" : "WheelDown";
        e.Handled = true;
    }

    private void DirBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var name = NameFromKey(e);
        if (name is null || sender is not TextBox box) return;
        box.Text = name;
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var r = Result;
            r.Kind = Kind;
            r.Key = r.Kind == BindingKind.Joystick ? null : KeyBox.Text.Trim();
            r.Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim();
            r.X = D(XBox, "X"); r.Y = D(YBox, "Y");
            r.Up = UpBox.Text; r.Down = DownBox.Text; r.Left = LeftBox.Text; r.Right = RightBox.Text;
            r.Radius = D(RadiusBox, "radius"); r.RampMs = I(RampBox, "ramp");
            r.ToX = D(ToXBox, "to X"); r.ToY = D(ToYBox, "to Y");
            r.DurationMs = r.Kind == BindingKind.Pinch ? I(PinchDurationBox, "duration") : I(DurationBox, "duration");
            r.IntervalMs = I(IntervalBox, "interval");
            r.Delta = D(DeltaBox, "zoom amount", -1, 1);
            r.Sensitivity = D(SensBox, "sensitivity", 0.01, 20);
            r.ResetRadius = D(ResetBox, "re-anchor radius");
            r.InvertY = InvertBox.IsChecked == true;
            r.AndroidKey = AndroidCombo.SelectedItem as string;
            if (r.Kind == BindingKind.Joystick) { r.Up = Req(UpBox, "Up"); r.Down = Req(DownBox, "Down"); r.Left = Req(LeftBox, "Left"); r.Right = Req(RightBox, "Right"); }
            else if (!HostKeys.TryParse(r.Key, out _)) throw new ArgumentException("Pick a key: click the Key box and press one.");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Binding", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Req(TextBox box, string what) =>
        HostKeys.TryParse(box.Text, out _) ? box.Text.Trim() : throw new ArgumentException($"{what} needs a key.");

    private static double D(TextBox box, string what, double min = 0, double max = 1)
    {
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new ArgumentException($"{what} must be a number.");
        if (v < min || v > max) throw new ArgumentException($"{what} must be between {min} and {max}.");
        return v;
    }

    private static int I(TextBox box, string what)
    {
        if (!int.TryParse(box.Text.Trim(), out var v) || v < 0) throw new ArgumentException($"{what} must be a non-negative whole number.");
        return v;
    }
}
