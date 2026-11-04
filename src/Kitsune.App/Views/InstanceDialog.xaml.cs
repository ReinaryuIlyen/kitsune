using System.Windows;
using System.Windows.Controls;
using Kitsune.App.Services;
using Kitsune.Core.Avd;
using Kitsune.Core.Host;

namespace Kitsune.App.Views;

public partial class InstanceDialog : Window
{
    private readonly AppState _state;
    private readonly bool _isNew;
    public InstanceConfig Config { get; }

    public InstanceDialog(AppState state, InstanceConfig config, bool isNew)
    {
        _state = state;
        _isNew = isNew;
        Config = config;
        InitializeComponent();

        var images = _state.Components.InstalledImages().Select(i => i.PackagePath).ToList();
        if (!images.Contains(config.SystemImage) && config.SystemImage.Length > 0) images.Insert(0, config.SystemImage);
        ImageCombo.ItemsSource = images;
        ImageCombo.SelectedItem = config.SystemImage;
        ImageCombo.IsEnabled = isNew;

        ProfileCombo.ItemsSource = DeviceProfile.All.Select(p => p.DisplayName).Append("Custom").ToList();
        ProfileCombo.SelectedItem = DeviceProfile.Find(config.ProfileId) is { } p && p.Width == config.Width && p.Height == config.Height ? p.DisplayName : "Custom";

        NameBox.Text = config.Name;
        NameBox.IsEnabled = isNew;
        WidthBox.Text = config.Width.ToString();
        HeightBox.Text = config.Height.ToString();
        DensityBox.Text = config.Density.ToString();
        HzBox.Text = config.RefreshRate.ToString();
        CoresBox.Text = config.Cores.ToString();
        RamBox.Text = config.RamMb.ToString();
        DataBox.Text = config.DataPartitionGb.ToString();
        var h = _state.Host;
        CoresHint.Text = $"host: {h.PhysicalCores} cores ({h.PerformanceCores} performance), recommended {Defaults.Cores(h)}, emulator max {Defaults.MaxCores}";
        RamHint.Text = $"host: {h.TotalRamBytes / 1073741824.0:F0} GB, recommended {Defaults.RamMb(h)}";

        GpuCombo.ItemsSource = new[] { "Host OpenGL (fastest)", "ANGLE on Direct3D 11", "SwiftShader (software)" };
        GpuCombo.SelectedIndex = (int)config.Gpu;
        VulkanBox.IsChecked = config.Vulkan;
        PinGpuBox.IsChecked = config.PinDiscreteGpu;

        AccelCombo.ItemsSource = new[] { $"Automatic ({h.Recommended})", "AEHD", "WHPX" };
        AccelCombo.SelectedIndex = config.AcceleratorOverride switch { Accelerator.Aehd => 1, Accelerator.Whpx => 2, _ => 0 };

        QuickbootBox.IsChecked = config.Quickboot;
        AudioBox.IsChecked = config.Audio;
        GpsBox.IsChecked = config.Gps;
        CameraBox.IsChecked = config.Camera;
        ExtraBox.Text = string.Join(' ', config.ExtraArgs);
        Hint.Text = isNew
            ? "Screen size and density can be changed later, but the data partition size is fixed once the instance has booted."
            : "Changes apply on the next launch. Resolution changes keep installed games and accounts.";
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is string name && DeviceProfile.All.FirstOrDefault(p => p.DisplayName == name) is { } p)
        {
            WidthBox.Text = p.Width.ToString();
            HeightBox.Text = p.Height.ToString();
            DensityBox.Text = p.Density.ToString();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameBox.Text.Trim();
            if (_isNew && !AvdManager.IsValidName(name)) throw new ArgumentException("Names may contain letters, digits, '_', '-' and '.' only.");
            Config.Name = name;
            Config.SystemImage = ImageCombo.SelectedItem as string ?? throw new ArgumentException("Pick a system image.");
            int w = Int(WidthBox, "width", 320, 7680), hgt = Int(HeightBox, "height", 320, 7680), d = Int(DensityBox, "density", 120, 640);
            var profile = DeviceProfile.All.FirstOrDefault(p => p.Width == w && p.Height == hgt && p.Density == d) ?? DeviceProfile.Phone1080p2400.WithSize(w, hgt, d);
            Config.ApplyProfile(profile);
            Config.RefreshRate = Int(HzBox, "refresh rate", 30, 240);
            Config.Cores = Int(CoresBox, "cores", 1, Math.Min(Defaults.MaxCores, Math.Max(1, _state.Host.LogicalProcessors)));
            Config.RamMb = Int(RamBox, "RAM", 1024, (int)Math.Max(1024, _state.Host.TotalRamBytes / 1048576 - 2048));
            Config.DataPartitionGb = Int(DataBox, "data partition", 4, 2048);
            Config.Gpu = (GpuMode)GpuCombo.SelectedIndex;
            Config.Vulkan = VulkanBox.IsChecked == true;
            Config.PinDiscreteGpu = PinGpuBox.IsChecked == true;
            Config.AcceleratorOverride = AccelCombo.SelectedIndex switch { 1 => Accelerator.Aehd, 2 => Accelerator.Whpx, _ => null };
            Config.Quickboot = QuickbootBox.IsChecked == true;
            Config.Audio = AudioBox.IsChecked == true;
            Config.Gps = GpsBox.IsChecked == true;
            Config.Camera = CameraBox.IsChecked == true;
            Config.ExtraArgs = ExtraBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Instance settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static int Int(TextBox box, string what, int min, int max)
    {
        if (!int.TryParse(box.Text.Trim(), out var v)) throw new ArgumentException($"{what} must be a number.");
        if (v < min || v > max) throw new ArgumentException($"{what} must be between {min} and {max}.");
        return v;
    }
}
