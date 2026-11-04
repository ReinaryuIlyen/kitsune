using System.Net.Http.Headers;

namespace Kitsune.Core.Sdk;

/// <summary>
/// High-level view of what Kitsune needs installed: the emulator, adb, the AEHD driver package and
/// at least one x86_64 system image with ARM translation. Wraps the repository and installer.
/// </summary>
public sealed class ComponentManager
{
    private readonly KitsunePaths _paths;
    private readonly SdkRepository _repo;
    private readonly SdkInstaller _installer;
    private IReadOnlyList<SdkPackage>? _catalog;

    public ComponentManager(KitsunePaths paths, HttpClient http)
    {
        _paths = paths;
        _repo = new SdkRepository(http);
        _installer = new SdkInstaller(paths, http);
    }

    public KitsunePaths Paths => _paths;
    public SdkInstaller Installer => _installer;

    public async Task<IReadOnlyList<SdkPackage>> CatalogAsync(CancellationToken ct = default)
    {
        _catalog ??= await _repo.FetchAllAsync(ct).ConfigureAwait(false);
        return _catalog;
    }

    public void InvalidateCatalog() => _catalog = null;

    public bool EmulatorInstalled => File.Exists(_paths.EmulatorExe);
    public bool AdbInstalled => File.Exists(_paths.AdbExe);
    public bool AehdPackageInstalled => File.Exists(Path.Combine(_paths.AehdDir, "silent_install.bat"));

    /// <summary>System images present on disk, discovered by their source.properties, newest API first.</summary>
    public IReadOnlyList<SystemImageRef> InstalledImages()
    {
        var list = new List<SystemImageRef>();
        if (!Directory.Exists(_paths.SystemImagesDir)) return list;
        foreach (var apiDir in Directory.EnumerateDirectories(_paths.SystemImagesDir, "android-*"))
        {
            var api = Path.GetFileName(apiDir)["android-".Length..];
            foreach (var tagDir in Directory.EnumerateDirectories(apiDir))
            foreach (var abiDir in Directory.EnumerateDirectories(tagDir))
            {
                if (!File.Exists(Path.Combine(abiDir, "system.img")) && !File.Exists(Path.Combine(abiDir, "source.properties"))) continue;
                list.Add(new SystemImageRef(api, Path.GetFileName(tagDir), Path.GetFileName(abiDir)));
            }
        }
        return list.OrderByDescending(i => PackageSelector.ApiSortKey(i.PackagePath)).ThenBy(i => i.Tag).ToList();
    }

    public async Task<IReadOnlyList<SdkPackage>> AvailableImagesAsync(CancellationToken ct = default) =>
        PackageSelector.GamingSystemImages(await CatalogAsync(ct).ConfigureAwait(false));

    /// <summary>Installs or updates the emulator, platform-tools and the AEHD driver package (driver installation itself is a separate, elevated step).</summary>
    public async Task InstallCoreAsync(IProgress<DownloadProgress>? progress, CancellationToken ct, bool update = true)
    {
        var catalog = await CatalogAsync(ct).ConfigureAwait(false);
        foreach (var pkg in new[] { PackageSelector.Emulator(catalog), PackageSelector.PlatformTools(catalog), PackageSelector.Aehd(catalog) })
        {
            if (pkg is null) throw new InvalidOperationException("The SDK repository is missing a required package.");
            if (_installer.IsInstalled(pkg)) { progress?.Report(new DownloadProgress(pkg.Path, 1, 1, "up to date")); continue; }
            if (!update && _installer.IsInstalled(pkg.Path)) continue;
            await _installer.InstallAsync(pkg, progress, ct).ConfigureAwait(false);
        }
        WriteEmulatorHomeDefaults();
    }

    public async Task InstallImageAsync(SystemImageRef image, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var catalog = await CatalogAsync(ct).ConfigureAwait(false);
        var pkg = PackageSelector.SystemImage(catalog, image)
            ?? throw new InvalidOperationException($"System image {image} is not in the repository.");
        if (_installer.IsInstalled(pkg)) { progress?.Report(new DownloadProgress(pkg.Path, 1, 1, "up to date")); return; }
        await _installer.InstallAsync(pkg, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-seeds the private emulator home so the emulator never asks about, or sends, usage metrics,
    /// and never performs its daily update check against Google's servers.
    /// </summary>
    public void WriteEmulatorHomeDefaults()
    {
        Directory.CreateDirectory(_paths.EmulatorHome);
        // The emulator stores its opt-in metrics state in analytics.settings (JSON) under $ANDROID_EMULATOR_HOME.
        var analytics = Path.Combine(_paths.EmulatorHome, "analytics.settings");
        if (!File.Exists(analytics))
            File.WriteAllText(analytics, "{\"hasOptedIn\":false,\"userId\":\"00000000-0000-0000-0000-000000000000\",\"saltValue\":\"0\",\"saltSkew\":0}\n");
        // The emulator's crash reporter consent lives in the same place; "no" is the only answer we give.
        var crash = Path.Combine(_paths.EmulatorHome, "crash-reporter.ini");
        if (!File.Exists(crash)) File.WriteAllText(crash, "crash-reporter.consent = no\n");
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kitsune", "0.1"));
        return http;
    }
}
