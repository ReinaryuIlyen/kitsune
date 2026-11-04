namespace Kitsune.Core.Avd;

public enum Orientation { Portrait, Landscape }

/// <summary>Screen geometry presets. Games read width, height and density; everything else is cosmetic.</summary>
public sealed record DeviceProfile(string Id, string DisplayName, int Width, int Height, int Density, Orientation Orientation)
{
    public static readonly DeviceProfile Phone1080p2400 = new("phone-fhd+", "Phone 1080 x 2400 @ 420 dpi (modern flagship)", 1080, 2400, 420, Orientation.Portrait);
    public static readonly DeviceProfile Phone1080p1920 = new("phone-fhd", "Phone 1080 x 1920 @ 420 dpi (16:9, best for older gacha)", 1080, 1920, 420, Orientation.Portrait);
    public static readonly DeviceProfile Phone1440p3120 = new("phone-qhd+", "Phone 1440 x 3120 @ 560 dpi (QHD+)", 1440, 3120, 560, Orientation.Portrait);
    public static readonly DeviceProfile Tablet1920p1200 = new("tablet-wuxga", "Tablet 1920 x 1200 @ 240 dpi (landscape)", 1920, 1200, 240, Orientation.Landscape);
    public static readonly DeviceProfile Tablet2560p1600 = new("tablet-wqxga", "Tablet 2560 x 1600 @ 320 dpi (landscape)", 2560, 1600, 320, Orientation.Landscape);
    public static readonly DeviceProfile Desktop1080p = new("desktop-fhd", "Landscape 1920 x 1080 @ 240 dpi (16:9, MOBA/shooter)", 1920, 1080, 240, Orientation.Landscape);
    public static readonly DeviceProfile Desktop1440p = new("desktop-qhd", "Landscape 2560 x 1440 @ 320 dpi (16:9)", 2560, 1440, 320, Orientation.Landscape);

    public static readonly IReadOnlyList<DeviceProfile> All =
    [
        Phone1080p2400, Phone1080p1920, Phone1440p3120, Tablet1920p1200, Tablet2560p1600, Desktop1080p, Desktop1440p,
    ];

    public static DeviceProfile? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public DeviceProfile WithSize(int width, int height, int density) =>
        this with { Id = "custom", DisplayName = $"Custom {width} x {height} @ {density} dpi", Width = width, Height = height, Density = density,
                    Orientation = width > height ? Orientation.Landscape : Orientation.Portrait };
}
