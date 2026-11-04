using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitsune.Core.Sdk;

public sealed record DownloadProgress(string Package, long BytesDone, long BytesTotal, string Phase)
{
    public double Fraction => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;
}

/// <summary>
/// Downloads (with HTTP range resume), verifies SHA-1 and size, and installs SDK packages into the
/// Kitsune-private SDK root. Installs are atomic: extraction happens into a staging directory which is
/// swapped into place only after the archive verified and extracted cleanly.
/// </summary>
public sealed class SdkInstaller
{
    private readonly KitsunePaths _paths;
    private readonly HttpClient _http;

    public SdkInstaller(KitsunePaths paths, HttpClient http)
    {
        _paths = paths;
        _http = http;
    }

    public IReadOnlyDictionary<string, InstalledPackage> ReadInstalled()
    {
        if (!File.Exists(_paths.InstalledFile)) return new Dictionary<string, InstalledPackage>();
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(_paths.InstalledFile), SdkJsonContext.Default.ListInstalledPackage) ?? [];
            return list.ToDictionary(p => p.Path, p => p);
        }
        catch { return new Dictionary<string, InstalledPackage>(); }
    }

    private void WriteInstalled(IReadOnlyDictionary<string, InstalledPackage> map)
    {
        Directory.CreateDirectory(_paths.Sdk);
        var json = JsonSerializer.Serialize(map.Values.OrderBy(p => p.Path).ToList(), SdkJsonContext.Default.ListInstalledPackage);
        File.WriteAllText(_paths.InstalledFile, json);
    }

    public bool IsInstalled(SdkPackage pkg)
    {
        var installed = ReadInstalled();
        return installed.TryGetValue(pkg.Path, out var i)
            && i.Sha1 == pkg.WindowsArchive?.Sha1
            && Directory.Exists(Path.Combine(_paths.Sdk, pkg.InstallRelativePath));
    }

    public bool IsInstalled(string packagePath)
    {
        var installed = ReadInstalled();
        return installed.ContainsKey(packagePath)
            && Directory.Exists(Path.Combine(_paths.Sdk, SdkPackage.InstallPathFor(packagePath)));
    }

    public bool NeedsUpdate(SdkPackage pkg)
    {
        var installed = ReadInstalled();
        return installed.TryGetValue(pkg.Path, out var i) && i.Sha1 != pkg.WindowsArchive?.Sha1;
    }

    public async Task InstallAsync(SdkPackage pkg, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var archive = pkg.WindowsArchive ?? throw new InvalidOperationException($"{pkg.Path} has no Windows archive.");
        _paths.EnsureDirectories();

        var fileName = Path.GetFileName(new Uri(archive.Url).LocalPath);
        var zipPath = Path.Combine(_paths.Downloads, fileName);

        await DownloadAsync(pkg.Path, archive, zipPath, progress, ct).ConfigureAwait(false);
        progress?.Report(new DownloadProgress(pkg.Path, archive.Size, archive.Size, "verifying"));
        await VerifyAsync(archive, zipPath, ct).ConfigureAwait(false);

        progress?.Report(new DownloadProgress(pkg.Path, 0, 1, "extracting"));
        var target = Path.Combine(_paths.Sdk, pkg.InstallRelativePath);
        await Task.Run(() => ExtractAtomically(zipPath, target, ct), ct).ConfigureAwait(false);

        var map = new Dictionary<string, InstalledPackage>(ReadInstalled())
        {
            [pkg.Path] = new InstalledPackage(pkg.Path, pkg.Version.ToString(), archive.Sha1, DateTimeOffset.UtcNow),
        };
        WriteInstalled(map);
        File.Delete(zipPath);
        progress?.Report(new DownloadProgress(pkg.Path, 1, 1, "done"));
    }

    private async Task DownloadAsync(string name, SdkArchive archive, string zipPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long existing = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;
        if (existing == archive.Size)
        {
            progress?.Report(new DownloadProgress(name, existing, archive.Size, "cached"));
            return;
        }
        if (existing > archive.Size) { File.Delete(zipPath); existing = 0; }

        using var req = new HttpRequestMessage(HttpMethod.Get, archive.Url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (existing > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server ignored the range request: start over.
            existing = 0;
        }
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(zipPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        long done = existing;
        var lastReport = DateTime.UtcNow;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress?.Report(new DownloadProgress(name, done, archive.Size, "downloading"));
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report(new DownloadProgress(name, done, archive.Size, "downloading"));
    }

    private static async Task VerifyAsync(SdkArchive archive, string zipPath, CancellationToken ct)
    {
        var info = new FileInfo(zipPath);
        if (info.Length != archive.Size)
            throw new InvalidDataException($"Size mismatch for {info.Name}: expected {archive.Size}, got {info.Length}.");

        await using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
        var hex = Convert.ToHexStringLower(hash);
        if (hex != archive.Sha1)
        {
            fs.Close();
            File.Delete(zipPath);
            throw new InvalidDataException($"SHA-1 mismatch for {info.Name}: expected {archive.Sha1}, got {hex}. The download was discarded.");
        }
    }

    /// <summary>
    /// Extracts the zip into a staging directory beside the target, stripping a single top-level
    /// folder when the archive has one (the SDK zips wrap contents in emulator/, platform-tools/, x86_64/ ...),
    /// then swaps it into place.
    /// </summary>
    private static void ExtractAtomically(string zipPath, string target, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = target + ".staging";
        var old = target + ".old";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        if (Directory.Exists(old)) Directory.Delete(old, true);
        Directory.CreateDirectory(staging);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var prefix = SingleTopLevelFolder(zip);
            var fullStaging = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (prefix is not null)
                {
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    name = name[prefix.Length..];
                }
                if (name.Length == 0) continue;
                var dest = Path.GetFullPath(Path.Combine(staging, name));
                if (!dest.StartsWith(fullStaging, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Zip entry escapes target directory: {entry.FullName}");
                if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }

        if (Directory.Exists(target)) Directory.Move(target, old);
        Directory.Move(staging, target);
        if (Directory.Exists(old)) Directory.Delete(old, true);
    }

    private static string? SingleTopLevelFolder(ZipArchive zip)
    {
        string? top = null;
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash < 0) return null; // a file at the root: no wrapper folder
            var first = name[..(slash + 1)];
            if (top is null) top = first;
            else if (top != first) return null;
        }
        return top;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<InstalledPackage>))]
internal partial class SdkJsonContext : JsonSerializerContext { }
