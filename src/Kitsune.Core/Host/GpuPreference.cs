using Microsoft.Win32;

namespace Kitsune.Core.Host;

/// <summary>
/// Pins the emulator's QEMU process to the high-performance GPU through the same per-application
/// preference Windows exposes in Settings > Display > Graphics. This is a per-user registry value, no
/// elevation required, and it is what makes hybrid laptops render on the discrete GPU.
/// </summary>
public static class GpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static void SetHighPerformance(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key?.SetValue(Path.GetFullPath(exePath), "GpuPreference=2;", RegistryValueKind.String);
    }

    public static void Clear(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(Path.GetFullPath(exePath), throwOnMissingValue: false);
    }

    public static bool IsHighPerformance(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(Path.GetFullPath(exePath)) is string s && s.Contains("GpuPreference=2");
    }
}
