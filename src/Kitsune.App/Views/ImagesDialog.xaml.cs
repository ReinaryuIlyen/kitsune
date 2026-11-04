using System.Windows;
using Kitsune.App.Services;
using Kitsune.Core;
using Kitsune.Core.Sdk;

namespace Kitsune.App.Views;

public partial class ImagesDialog : Window
{
    private sealed record Row(SdkPackage Package, bool Installed)
    {
        public string Path => Package.Path;
        public string Title
        {
            get
            {
                var img = SystemImageRef.Parse(Package.Path);
                var variant = img.HasPlayStore ? "Google Play" : "Google APIs (rootable)";
                return $"Android {ApiName(img.ApiLevel)} (API {img.ApiLevel}) · {variant} · r{Package.Version.Major} · {Package.WindowsArchive!.Size / 1048576.0:F0} MB";
            }
        }
        public string Status => Installed ? "installed" : "";
        private static string ApiName(string api) => api switch
        {
            "33" => "13", "34" => "14", "35" => "15", "36" => "16", "36.1" => "16 QPR", "37.0" => "17", "37.1" => "17 QPR", _ => api,
        };
    }

    private readonly AppState _state;
    public SystemImageRef? SelectedImage { get; private set; }

    public ImagesDialog(AppState state)
    {
        _state = state;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        ImageList.SelectionChanged += (_, _) => InstallBtn.IsEnabled = ImageList.SelectedItem is Row { Installed: false };
    }

    private async Task LoadAsync()
    {
        Status.Text = "Reading repository...";
        try
        {
            var images = await _state.Components.AvailableImagesAsync();
            var rows = images.Where(p => PackageSelector.ApiSortKey(p.Path) >= 30)
                             .Select(p => new Row(p, _state.Components.Installer.IsInstalled(p))).ToList();
            ImageList.ItemsSource = rows;
            Status.Text = "";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not read the repository: " + ex.Message;
        }
    }

    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ImageList.SelectedItem is Row r)
        {
            SelectedImage = SystemImageRef.Parse(r.Package.Path);
            DialogResult = true;
            Close();
        }
    }
}
