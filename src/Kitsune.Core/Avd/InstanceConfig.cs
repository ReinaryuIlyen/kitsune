using System.Text.Json;
using System.Text.Json.Serialization;
using Kitsune.Core.Host;

namespace Kitsune.Core.Avd;

public enum GpuMode
{
    /// <summary>Host OpenGL via the native driver. Fastest on a working desktop driver.</summary>
    Host,
    /// <summary>ANGLE over Direct3D 11. Use when the native OpenGL driver misbehaves.</summary>
    Angle,
    /// <summary>SwiftShader software rendering. Diagnostics only.</summary>
    SwiftShader,
}

/// <summary>
/// Everything Kitsune decides about one emulator instance. Stored as kitsune.json inside the AVD
/// directory so an instance is self-describing and can be copied as a folder.
/// </summary>
public sealed class InstanceConfig
{
    public string Name { get; set; } = "";
    public string SystemImage { get; set; } = "";
    public string ProfileId { get; set; } = DeviceProfile.Phone1080p2400.Id;
    public int Width { get; set; } = DeviceProfile.Phone1080p2400.Width;
    public int Height { get; set; } = DeviceProfile.Phone1080p2400.Height;
    public int Density { get; set; } = DeviceProfile.Phone1080p2400.Density;
    public int Cores { get; set; } = 4;
    public int RamMb { get; set; } = 6144;
    public int VmHeapMb { get; set; } = 576;
    public int DataPartitionGb { get; set; } = 16;
    public int RefreshRate { get; set; } = 60;
    public GpuMode Gpu { get; set; } = GpuMode.Host;
    public bool Vulkan { get; set; } = true;
    public Accelerator? AcceleratorOverride { get; set; }
    public bool PinDiscreteGpu { get; set; } = true;
    public bool Audio { get; set; } = true;
    public bool Quickboot { get; set; } = true;
    public bool Camera { get; set; } = false;
    public bool Gps { get; set; } = true;
    public bool ShowToolbar { get; set; } = false;
    public string? KeymapProfile { get; set; }
    /// <summary>Extra arguments appended verbatim to the emulator command line.</summary>
    public List<string> ExtraArgs { get; set; } = [];

    public SystemImageRef Image => SystemImageRef.Parse(SystemImage);

    public DeviceProfile Profile =>
        DeviceProfile.Find(ProfileId) is { } p && p.Width == Width && p.Height == Height && p.Density == Density
            ? p
            : DeviceProfile.Phone1080p2400.WithSize(Width, Height, Density);

    public void ApplyProfile(DeviceProfile p)
    {
        ProfileId = p.Id; Width = p.Width; Height = p.Height; Density = p.Density;
    }

    public static InstanceConfig Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), InstanceJsonContext.Default.InstanceConfig)
        ?? throw new InvalidDataException($"Could not read {path}");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, InstanceJsonContext.Default.InstanceConfig));
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstanceConfig))]
internal partial class InstanceJsonContext : JsonSerializerContext { }
