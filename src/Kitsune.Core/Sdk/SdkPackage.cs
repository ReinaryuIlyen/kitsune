namespace Kitsune.Core.Sdk;

public sealed record SdkArchive(string Url, long Size, string Sha1, string? HostOs, int? HostBits);

/// <param name="InstallRelativePath">Directory under the SDK root this package installs to, e.g. system-images/android-35/google_apis_playstore/x86_64.</param>
public sealed record SdkPackage(
    string Path,
    Version Version,
    string Channel,
    string DisplayName,
    IReadOnlyList<SdkArchive> Archives,
    string InstallRelativePath)
{
    public bool IsStable => Channel == "stable";

    /// <summary>Picks the archive for 64-bit Windows, or the platform-neutral archive.</summary>
    public SdkArchive? WindowsArchive =>
        Archives.FirstOrDefault(a => a.HostOs == "windows" && (a.HostBits is null or 64))
        ?? Archives.FirstOrDefault(a => a.HostOs is null);

    public static string InstallPathFor(string packagePath) => packagePath.Replace(';', '/');
}

public sealed record InstalledPackage(string Path, string Version, string Sha1, DateTimeOffset InstalledAt);
