using System.Diagnostics;

namespace Kitsune.Core.Host;

/// <summary>
/// Installs or removes the Android Emulator Hypervisor Driver using the scripts Google ships in the
/// SDK package. Both require elevation; Windows shows the UAC prompt for the batch file.
/// </summary>
public static class AehdInstaller
{
    public static bool PackagePresent(KitsunePaths paths) => File.Exists(Path.Combine(paths.AehdDir, "silent_install.bat"));

    public static async Task<(bool Ok, string Output)> InstallAsync(KitsunePaths paths, CancellationToken ct = default)
    {
        var script = Path.Combine(paths.AehdDir, "silent_install.bat");
        if (!File.Exists(script)) return (false, "AEHD package is not installed. Run the component install first.");
        return await RunElevatedAsync(script, paths.AehdDir, ct).ConfigureAwait(false);
    }

    public static async Task<(bool Ok, string Output)> UninstallAsync(KitsunePaths paths, CancellationToken ct = default)
    {
        var script = Path.Combine(paths.AehdDir, "silent_uninstall.bat");
        if (!File.Exists(script)) return (false, "AEHD package is not installed.");
        return await RunElevatedAsync(script, paths.AehdDir, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string Output)> RunElevatedAsync(string script, string workDir, CancellationToken ct)
    {
        // Output of an elevated process cannot be captured directly; the script's own log is read back instead.
        var log = Path.Combine(Path.GetTempPath(), $"kitsune-aehd-{Guid.NewGuid():N}.log");
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{script}\" > \"{log}\" 2>&1\"",
            WorkingDirectory = workDir,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the installer.");
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = File.Exists(log) ? await File.ReadAllTextAsync(log, ct).ConfigureAwait(false) : "";
            var state = HostProbe.AehdServiceState();
            return (state == AehdState.Running || (script.Contains("uninstall") && state == AehdState.NotInstalled), output.Trim());
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Elevation was declined.");
        }
        finally
        {
            try { if (File.Exists(log)) File.Delete(log); } catch { }
        }
    }
}
