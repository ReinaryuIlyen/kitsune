using Kitsune.Core.Host;

namespace Kitsune.Core.Avd;

/// <summary>Sizing rules for new instances derived from the host probe.</summary>
public static class Defaults
{
    /// <summary>The emulator itself refuses more than this many vCPUs ("Emulator does not support more than 6 cores").</summary>
    public const int MaxCores = 6;

    /// <summary>
    /// vCPUs: the performance-core count on hybrid CPUs (efficiency cores make poor vCPUs: the guest
    /// scheduler cannot tell them apart), never more than half the physical cores plus one so the host
    /// keeps headroom for the GPU translation threads, and never above the emulator's own cap.
    /// </summary>
    public static int Cores(HostInfo host)
    {
        int perf = host.PerformanceCores > 0 ? host.PerformanceCores : host.PhysicalCores;
        int cap = Math.Max(2, host.PhysicalCores / 2 + 1);
        return Math.Clamp(Math.Min(perf, cap), 2, MaxCores);
    }

    /// <summary>Guest RAM: a quarter of host memory, between 3 GB and 12 GB, rounded to 512 MB.</summary>
    public static int RamMb(HostInfo host)
    {
        long quarterMb = host.TotalRamBytes / (1024 * 1024) / 4;
        long clamped = Math.Clamp(quarterMb, 3072, 12288);
        return (int)(clamped / 512 * 512);
    }
}
