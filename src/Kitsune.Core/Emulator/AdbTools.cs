using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Kitsune.Core.Emulator;

/// <summary>
/// The few adb operations a game launcher needs: install an APK (or a split bundle: .apks, .xapk, or a
/// zip of APKs with optional OBB expansion files), push files, and open a shell. adb is the platform-tools
/// binary Kitsune installed; it is given the same private environment as the emulator so the key it
/// authenticates with is the one the emulator pre-authorised in the guest.
/// </summary>
public sealed class AdbTools
{
    private readonly KitsunePaths _paths;
    public AdbTools(KitsunePaths paths) => _paths = paths;

    public bool Available => File.Exists(_paths.AdbExe);

    public sealed record Result(int ExitCode, string Output)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Extensions <see cref="InstallAsync"/> accepts.</summary>
    public static readonly string[] InstallableExtensions = [".apk", ".apks", ".xapk", ".zip"];

    public static bool IsInstallable(string file) =>
        InstallableExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Installs a package on the device with the given serial (emulator-&lt;console port&gt;). A plain APK goes
    /// through <c>adb install -r -d</c>; a bundle is unpacked to a temporary folder, its APKs installed in one
    /// <c>install-multiple</c> transaction and any <c>Android/obb/</c> payload pushed to the guest's external storage.
    /// </summary>
    public async Task<Result> InstallAsync(string serial, string file, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Available) return new Result(-1, "adb is not installed. Run the component install first.");
        if (!File.Exists(file)) return new Result(-1, $"File not found: {file}");
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".apk")
        {
            progress?.Report($"Installing {Path.GetFileName(file)}...");
            return await RunAsync(serial, ["install", "-r", "-d", file], ct).ConfigureAwait(false);
        }
        if (!IsInstallable(file)) return new Result(-1, "Expected an .apk, .apks, .xapk or .zip file.");

        var temp = Path.Combine(Path.GetTempPath(), "kitsune-apk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            progress?.Report($"Unpacking {Path.GetFileName(file)}...");
            var apks = new List<string>();
            var obbs = new List<(string Local, string Remote)>();
            using (var zip = ZipFile.OpenRead(file))
            {
                var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith('/') || name.Length == 0) continue;
                    bool isApk = name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
                    bool isObb = name.StartsWith("Android/obb/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase);
                    if (!isApk && !isObb) continue;
                    var dest = Path.GetFullPath(Path.Combine(temp, name));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                    if (isApk) apks.Add(dest);
                    else obbs.Add((dest, "/sdcard/" + name));
                }
            }
            if (apks.Count == 0) return new Result(-1, "The bundle contains no APK.");

            progress?.Report(apks.Count == 1 ? "Installing 1 APK..." : $"Installing {apks.Count} split APKs...");
            var install = apks.Count == 1
                ? await RunAsync(serial, ["install", "-r", "-d", apks[0]], ct).ConfigureAwait(false)
                : await RunAsync(serial, ["install-multiple", "-r", "-d", .. apks], ct).ConfigureAwait(false);
            if (!install.Ok) return install;

            var log = new StringBuilder(install.Output);
            foreach (var (local, remote) in obbs)
            {
                progress?.Report($"Pushing {Path.GetFileName(local)} ({new FileInfo(local).Length / 1048576.0:F0} MB)...");
                var push = await RunAsync(serial, ["push", local, remote], ct).ConfigureAwait(false);
                log.AppendLine().Append(push.Output);
                if (!push.Ok) return new Result(push.ExitCode, log.ToString());
            }
            return new Result(0, log.ToString());
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    /// <summary>Runs adb against one device and captures its output; a non-zero exit code or an "Failure" line is an error.</summary>
    public async Task<Result> RunAsync(string? serial, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (!Available) return new Result(-1, "adb is not installed.");
        var psi = Psi(serial, args);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start adb.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } throw; }
        var output = ((await stdout).Trim() + "\n" + (await stderr).Trim()).Trim();
        // adb install reports package-manager failures on stdout with exit code 0 on some versions.
        int code = p.ExitCode == 0 && output.Contains("Failure [", StringComparison.Ordinal) ? 1 : p.ExitCode;
        return new Result(code, output);
    }

    /// <summary>
    /// Opens an interactive adb shell for the device. Started from a windowed process with nothing
    /// redirected, a console program gets a fresh console window of its own, which closes when the shell exits.
    /// </summary>
    public void OpenShell(string serial)
    {
        if (!Available) throw new FileNotFoundException("adb is not installed.", _paths.AdbExe);
        Process.Start(Psi(serial, ["shell"]));
    }

    /// <summary>
    /// Stops the adb server so platform-tools can be replaced: the server is a long-lived adb.exe that
    /// keeps its own binary open. Best effort; a server that is not running is not an error.
    /// </summary>
    public async Task KillServerAsync(CancellationToken ct = default)
    {
        if (!Available) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await RunAsync(null, ["kill-server"], timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>Starts adb with the console inherited (for a CLI pass-through) and returns its exit code.</summary>
    public async Task<int> PassThroughAsync(string? serial, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (!Available) throw new FileNotFoundException("adb is not installed; run `kitsune install` first.", _paths.AdbExe);
        using var p = Process.Start(Psi(serial, args)) ?? throw new InvalidOperationException("Could not start adb.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode;
    }

    private ProcessStartInfo Psi(string? serial, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo { FileName = _paths.AdbExe, UseShellExecute = false, WorkingDirectory = _paths.PlatformToolsDir };
        if (serial is not null) { psi.ArgumentList.Add("-s"); psi.ArgumentList.Add(serial); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi);
        return psi;
    }

    private void ApplyEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["ANDROID_SDK_ROOT"] = _paths.Sdk;
        psi.Environment["ANDROID_HOME"] = _paths.Sdk;
        psi.Environment["ANDROID_AVD_HOME"] = _paths.AvdHome;
        psi.Environment["ANDROID_EMULATOR_HOME"] = _paths.EmulatorHome;
    }
}
