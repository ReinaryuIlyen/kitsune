using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace Kitsune.Core.Host;

public enum Accelerator { None, Aehd, Whpx }

public sealed record GpuInfo(string Name, string DriverVersion, string Vendor, bool IsDiscrete);

public sealed record VulkanIcd(string Path, string Vendor);

public sealed record HostInfo(
    string CpuName,
    string CpuVendor,
    int PhysicalCores,
    int PerformanceCores,
    int LogicalProcessors,
    bool HardwareVirtualization,
    bool HypervisorPresent,
    string? HypervisorVendor,
    bool WhpxAvailable,
    AehdState Aehd,
    long TotalRamBytes,
    long AvailableRamBytes,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<VulkanIcd> VulkanIcds)
{
    /// <summary>
    /// Best accelerator for this machine. AEHD is a kernel driver that owns VT-x directly, which beats
    /// WHPX (an API layered on Hyper-V) in raw guest performance. AEHD cannot load while Hyper-V, VBS,
    /// Credential Guard or WSL2 hold the hypervisor, in which case WHPX is the only option.
    /// </summary>
    public Accelerator Recommended =>
        !HardwareVirtualization ? Accelerator.None
        : !HypervisorPresent ? Accelerator.Aehd
        : WhpxAvailable ? Accelerator.Whpx
        : Accelerator.None;

    public GpuInfo? PreferredGpu =>
        Gpus.FirstOrDefault(g => g.IsDiscrete && g.Vendor == "NVIDIA")
        ?? Gpus.FirstOrDefault(g => g.IsDiscrete && g.Vendor == "AMD")
        ?? Gpus.FirstOrDefault(g => g.IsDiscrete)
        ?? Gpus.FirstOrDefault();

    public VulkanIcd? PreferredVulkanIcd =>
        PreferredGpu is null ? null : VulkanIcds.FirstOrDefault(i => i.Vendor == PreferredGpu.Vendor);
}

public enum AehdState { NotInstalled, Stopped, Running }

/// <summary>Collects everything the launcher needs to know about the host without touching admin-only APIs.</summary>
public static class HostProbe
{
    public static HostInfo Probe()
    {
        var (cpuName, vendor) = CpuIdentity();
        var (physical, perf, logical) = CoreTopology();
        bool hvPresent = HypervisorPresent(out var hvVendor);
        bool hwVirt = HardwareVirtualizationSupported(vendor);
        bool whpx = File.Exists(Path.Combine(Environment.SystemDirectory, "WinHvPlatform.dll")) && hvPresent;

        return new HostInfo(
            cpuName, vendor, physical, perf, logical,
            hwVirt, hvPresent, hvVendor, whpx,
            AehdServiceState(),
            TotalRam(),
            AvailableRam(),
            Gpus(),
            VulkanIcds());
    }

    private static long AvailableRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : 0;
    }

    private static (string Name, string Vendor) CpuIdentity()
    {
        if (!X86Base.IsSupported) return ("Unknown", "Unknown");
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        var vendorBytes = new byte[12];
        BitConverter.GetBytes(ebx).CopyTo(vendorBytes, 0);
        BitConverter.GetBytes(edx).CopyTo(vendorBytes, 4);
        BitConverter.GetBytes(ecx).CopyTo(vendorBytes, 8);
        var vendorId = Encoding.ASCII.GetString(vendorBytes);
        var vendor = vendorId switch
        {
            "GenuineIntel" => "Intel",
            "AuthenticAMD" => "AMD",
            _ => vendorId,
        };

        var sb = new StringBuilder();
        var (maxExt, _, _, _) = X86Base.CpuId(unchecked((int)0x80000000), 0);
        if ((uint)maxExt >= 0x80000004)
        {
            for (int leaf = 0; leaf < 3; leaf++)
            {
                var (a, b, c, d) = X86Base.CpuId(unchecked((int)(0x80000002 + leaf)), 0);
                foreach (var r in new[] { a, b, c, d })
                    sb.Append(Encoding.ASCII.GetString(BitConverter.GetBytes(r)));
            }
        }
        var name = sb.ToString().Replace("\0", "").Trim();
        return (name.Length == 0 ? vendorId : name, vendor);
    }

    private static bool HypervisorPresent(out string? hvVendor)
    {
        hvVendor = null;
        if (!X86Base.IsSupported) return false;
        var (_, _, ecx, _) = X86Base.CpuId(1, 0);
        bool present = (ecx & (1 << 31)) != 0;
        if (present)
        {
            var (_, b, c, d) = X86Base.CpuId(0x40000000, 0);
            var bytes = new byte[12];
            BitConverter.GetBytes(b).CopyTo(bytes, 0);
            BitConverter.GetBytes(c).CopyTo(bytes, 4);
            BitConverter.GetBytes(d).CopyTo(bytes, 8);
            hvVendor = Encoding.ASCII.GetString(bytes).Replace("\0", "").Trim();
        }
        return present;
    }

    private static bool HardwareVirtualizationSupported(string vendor)
    {
        if (!X86Base.IsSupported) return false;
        if (vendor == "Intel")
        {
            var (_, _, ecx, _) = X86Base.CpuId(1, 0);
            return (ecx & (1 << 5)) != 0; // VMX
        }
        if (vendor == "AMD")
        {
            var (_, _, ecx, _) = X86Base.CpuId(unchecked((int)0x80000001), 0);
            return (ecx & (1 << 2)) != 0; // SVM
        }
        return false;
    }

    public static AehdState AehdServiceState()
    {
        try
        {
            using var sc = new ServiceController("aehd");
            return sc.Status == ServiceControllerStatus.Running ? AehdState.Running : AehdState.Stopped;
        }
        catch (InvalidOperationException)
        {
            return AehdState.NotInstalled;
        }
    }

    private static long TotalRam()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullTotalPhys : 0;
    }

    private static IReadOnlyList<GpuInfo> Gpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString() ?? "Unknown GPU";
                var driver = mo["DriverVersion"]?.ToString() ?? "";
                var compat = mo["AdapterCompatibility"]?.ToString() ?? "";
                var vendor = VendorOf(name + " " + compat);
                bool discrete = vendor is "NVIDIA" || (vendor == "AMD" && !name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon(TM)", StringComparison.OrdinalIgnoreCase))
                                || (vendor == "AMD" && name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase))
                                || (vendor == "Intel" && name.Contains("Arc", StringComparison.OrdinalIgnoreCase));
                list.Add(new GpuInfo(name, driver, vendor, discrete));
            }
        }
        catch { /* WMI unavailable: leave the list empty, the emulator will still pick a default adapter */ }
        return list;
    }

    private static string VendorOf(string s)
    {
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (s.Contains("AMD", StringComparison.OrdinalIgnoreCase) || s.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || s.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "Unknown";
    }

    /// <summary>
    /// Vulkan installable client drivers registered with the loader. The loader honours VK_DRIVER_FILES,
    /// which lets Kitsune pin the emulator's Vulkan device to the discrete GPU on hybrid systems.
    /// </summary>
    private static IReadOnlyList<VulkanIcd> VulkanIcds()
    {
        var list = new List<VulkanIcd>();
        void Add(string path)
        {
            if (!File.Exists(path) || list.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            var file = Path.GetFileName(path).ToLowerInvariant();
            var vendor = file.StartsWith("nv") ? "NVIDIA"
                : file.StartsWith("amd") ? "AMD"
                : file.StartsWith("igvk") || file.Contains("intel") ? "Intel"
                : "Unknown";
            list.Add(new VulkanIcd(path, vendor));
        }

        // Modern drivers register per display adapter (the loader reads VulkanDriverName from each adapter's software key).
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is not null)
            {
                foreach (var sub in cls.GetSubKeyNames())
                {
                    using var adapter = cls.OpenSubKey(sub);
                    var value = adapter?.GetValue("VulkanDriverName");
                    if (value is string s) Add(s);
                    else if (value is string[] arr) foreach (var s2 in arr) Add(s2);
                }
            }
        }
        catch { }

        // Legacy registration path, still used by some drivers.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\Vulkan\Drivers");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is int enabled && enabled != 0) continue; // non-zero = disabled
                    Add(valueName);
                }
            }
        }
        catch { }
        return list;
    }

    private static (int Physical, int Performance, int Logical) CoreTopology()
    {
        int logical = Environment.ProcessorCount;
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            var buffer = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len))
                    return (logical, logical, logical);

                int physical = 0;
                var efficiencyClasses = new List<byte>();
                int offset = 0;
                while (offset < len)
                {
                    var ptr = buffer + offset;
                    int relationship = Marshal.ReadInt32(ptr);
                    int size = Marshal.ReadInt32(ptr + 4);
                    if (relationship == RelationProcessorCore)
                    {
                        physical++;
                        // PROCESSOR_RELATIONSHIP: BYTE Flags; BYTE EfficiencyClass; BYTE Reserved[20]; WORD GroupCount; GROUP_AFFINITY GroupMask[]
                        byte efficiencyClass = Marshal.ReadByte(ptr + 8 + 1);
                        efficiencyClasses.Add(efficiencyClass);
                    }
                    offset += size;
                }
                byte maxClass = efficiencyClasses.Count > 0 ? efficiencyClasses.Max() : (byte)0;
                int perf = efficiencyClasses.Count(c => c == maxClass);
                return (physical, perf, logical);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch
        {
            return (logical, logical, logical);
        }
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
