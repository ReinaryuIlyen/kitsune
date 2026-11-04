using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kitsune.Core.Reroll;

/// <summary>Thin wrapper over the qemu-img.exe that ships with the emulator, plus the hard-link helper the overlay layout needs.</summary>
public sealed class QemuImg
{
    private readonly string _exe;
    public QemuImg(KitsunePaths paths) => _exe = paths.QemuImgExe;

    public bool Available => File.Exists(_exe);

    /// <summary>Flattens an overlay chain into one standalone image of the given format ("qcow2" or "raw"); internal snapshots are dropped.</summary>
    public Task ConvertAsync(string source, string dest, string format, CancellationToken ct = default) =>
        RunAsync(["convert", "-O", format, source, dest], ct);

    /// <summary>
    /// Creates an empty copy-on-write overlay. <paramref name="backingName"/> is resolved by QEMU relative
    /// to the overlay's own folder; the emulator only keeps an overlay whose backing name is the plain disk
    /// file it expects beside it, so callers pass that exact name.
    /// </summary>
    public Task CreateOverlayAsync(string dest, string backingName, string backingFormat, CancellationToken ct = default) =>
        RunAsync(["create", "-f", "qcow2", "-o", $"backing_file={backingName},backing_fmt={backingFormat}", dest], ct);

    public async Task<string> InfoAsync(string image, CancellationToken ct = default) =>
        await RunAsync(["info", image], ct).ConfigureAwait(false);

    /// <summary>NTFS hard link so a clone's disk name resolves to the shared base without copying it. Falls back to a copy across volumes.</summary>
    public static void LinkOrCopy(string existing, string link)
    {
        if (File.Exists(link)) File.Delete(link);
        if (!CreateHardLinkW(link, existing, IntPtr.Zero))
            File.Copy(existing, link);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!Available) throw new FileNotFoundException("qemu-img.exe is missing; install the emulator component first.", _exe);
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start qemu-img.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"qemu-img {args[0]} failed ({p.ExitCode}): {(await stderr).Trim()}");
        return await stdout;
    }
}
