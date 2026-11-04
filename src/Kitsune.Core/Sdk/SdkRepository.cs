using System.Globalization;
using System.Xml.Linq;

namespace Kitsune.Core.Sdk;

/// <summary>
/// Reads the public Android SDK repository manifests. These are the same XML files that
/// sdkmanager consumes; every archive carries a size and SHA-1 that is verified after download.
/// </summary>
public sealed class SdkRepository
{
    public const string RepositoryBase = "https://dl.google.com/android/repository/";

    /// <summary>Manifest URL paired with the base URL its archive paths are relative to.</summary>
    public static readonly IReadOnlyList<(string Manifest, string Base)> Manifests =
    [
        (RepositoryBase + "repository2-3.xml", RepositoryBase),
        (RepositoryBase + "addon2-3.xml", RepositoryBase),
        (RepositoryBase + "sys-img/google_apis_playstore/sys-img2-3.xml", RepositoryBase + "sys-img/google_apis_playstore/"),
        (RepositoryBase + "sys-img/google_apis/sys-img2-3.xml", RepositoryBase + "sys-img/google_apis/"),
    ];

    private static readonly Dictionary<string, string> ChannelNames = new()
    {
        ["channel-0"] = "stable", ["channel-1"] = "beta", ["channel-2"] = "dev", ["channel-3"] = "canary",
    };

    private readonly HttpClient _http;
    public SdkRepository(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<SdkPackage>> FetchAllAsync(CancellationToken ct = default)
    {
        var tasks = Manifests.Select(m => FetchManifestAsync(m.Manifest, m.Base, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    public async Task<IReadOnlyList<SdkPackage>> FetchManifestAsync(string manifestUrl, string baseUrl, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        return Parse(doc, baseUrl);
    }

    public static IReadOnlyList<SdkPackage> Parse(XDocument doc, string baseUrl)
    {
        var list = new List<SdkPackage>();
        foreach (var pkg in doc.Root!.Elements().Where(e => e.Name.LocalName == "remotePackage"))
        {
            var path = pkg.Attribute("path")?.Value;
            if (string.IsNullOrEmpty(path)) continue;

            var rev = Child(pkg, "revision");
            var version = new Version(Int(rev, "major"), Int(rev, "minor"), Int(rev, "micro"));

            var chanRef = Child(pkg, "channelRef")?.Attribute("ref")?.Value ?? "channel-0";
            var channel = ChannelNames.TryGetValue(chanRef, out var cn) ? cn : chanRef;
            var display = Child(pkg, "display-name")?.Value ?? path;

            var archives = new List<SdkArchive>();
            foreach (var arch in pkg.Descendants().Where(e => e.Name.LocalName == "archive"))
            {
                var complete = Child(arch, "complete");
                if (complete is null) continue;
                var url = Child(complete, "url")?.Value;
                var sizeText = Child(complete, "size")?.Value;
                var checksum = Child(complete, "checksum");
                if (url is null || checksum is null || !long.TryParse(sizeText, out var size)) continue;
                var type = checksum.Attribute("type")?.Value ?? "sha1";
                if (!string.Equals(type, "sha1", StringComparison.OrdinalIgnoreCase)) continue;
                var hostOs = Child(arch, "host-os")?.Value;
                int? bits = int.TryParse(Child(arch, "host-bits")?.Value, out var b) ? b : null;
                var absolute = Uri.IsWellFormedUriString(url, UriKind.Absolute) ? url : baseUrl + url;
                archives.Add(new SdkArchive(absolute, size, checksum.Value.Trim().ToLowerInvariant(), hostOs, bits));
            }
            if (archives.Count == 0) continue;

            list.Add(new SdkPackage(path, version, channel, display, archives, SdkPackage.InstallPathFor(path)));
        }
        return list;

        static XElement? Child(XElement? parent, string name) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

        static int Int(XElement? parent, string name) =>
            int.TryParse(Child(parent, name)?.Value, out var v) ? v : 0;
    }
}

/// <summary>Chooses concrete packages out of the repository for what Kitsune needs.</summary>
public static class PackageSelector
{
    public const string EmulatorPath = "emulator";
    public const string PlatformToolsPath = "platform-tools";
    public const string AehdPath = "extras;google;Android_Emulator_Hypervisor_Driver";

    public static SdkPackage? LatestStable(IEnumerable<SdkPackage> all, string path) =>
        all.Where(p => p.Path == path && p.IsStable && p.WindowsArchive is not null)
           .OrderByDescending(p => p.Version).FirstOrDefault();

    public static SdkPackage? Emulator(IEnumerable<SdkPackage> all) => LatestStable(all, EmulatorPath);
    public static SdkPackage? PlatformTools(IEnumerable<SdkPackage> all) => LatestStable(all, PlatformToolsPath);
    public static SdkPackage? Aehd(IEnumerable<SdkPackage> all) => LatestStable(all, AehdPath);
    public static SdkPackage? SystemImage(IEnumerable<SdkPackage> all, SystemImageRef image) => LatestStable(all, image.PackagePath);

    /// <summary>
    /// All x86_64 Google Play and Google APIs images, newest API first. SDK extension ("ext"),
    /// beta and 16 KB page-size variants are excluded: games are built against the mainline images.
    /// </summary>
    public static IReadOnlyList<SdkPackage> GamingSystemImages(IEnumerable<SdkPackage> all) =>
        all.Where(p => p.IsStable && p.Path.StartsWith("system-images;android-", StringComparison.Ordinal))
           .Where(p =>
           {
               var parts = p.Path.Split(';');
               if (parts.Length != 4) return false;
               var api = parts[1]["android-".Length..];
               if (api.Contains("ext", StringComparison.Ordinal) || api.Contains("beta", StringComparison.Ordinal)) return false;
               return parts[2] is "google_apis_playstore" or "google_apis" && parts[3] == "x86_64";
           })
           .GroupBy(p => p.Path).Select(g => g.OrderByDescending(p => p.Version).First())
           .OrderByDescending(p => ApiSortKey(p.Path)).ThenBy(p => p.Path)
           .ToList();

    public static double ApiSortKey(string packagePath)
    {
        var parts = packagePath.Split(';');
        var api = parts[1]["android-".Length..];
        return double.TryParse(api, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
