using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitsune.Core.Input;

[JsonConverter(typeof(JsonStringEnumConverter<BindingKind>))]
public enum BindingKind
{
    /// <summary>Touch down at (X,Y) while the key is held, up on release.</summary>
    Tap,
    /// <summary>Tap at the current mouse cursor position while the key is held.</summary>
    TapAtCursor,
    /// <summary>Repeated taps at (X,Y) every IntervalMs while the key is held.</summary>
    Repeat,
    /// <summary>Four keys drive a virtual stick centred at (X,Y) with the given Radius.</summary>
    Joystick,
    /// <summary>Scripted swipe from (X,Y) to (ToX,ToY) over DurationMs.</summary>
    Swipe,
    /// <summary>Two-finger pinch around (X,Y): positive Delta zooms in, negative zooms out.</summary>
    Pinch,
    /// <summary>Toggles mouse-look: relative mouse motion drags a held touch anchored at (X,Y).</summary>
    AimToggle,
    /// <summary>Sends an Android key (Back, Home, ...).</summary>
    AndroidKey,
    /// <summary>Toggles the keymap overlay/engine on and off.</summary>
    ToggleKeymap,
}

public sealed class Binding
{
    public BindingKind Kind { get; set; } = BindingKind.Tap;
    /// <summary>Host control name, see <see cref="HostKeys"/>. Unused by Joystick, which has four keys of its own.</summary>
    public string? Key { get; set; }
    public string? Label { get; set; }
    /// <summary>Normalized guest coordinates, 0..1 of the display width/height.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    // Joystick
    public string? Up { get; set; }
    public string? Down { get; set; }
    public string? Left { get; set; }
    public string? Right { get; set; }
    /// <summary>Stick travel as a fraction of the display's shorter side.</summary>
    public double Radius { get; set; } = 0.12;
    /// <summary>Time to travel from centre to full deflection. 0 = instant.</summary>
    public int RampMs { get; set; } = 0;

    // Swipe
    public double ToX { get; set; }
    public double ToY { get; set; }
    public int DurationMs { get; set; } = 150;

    // Repeat
    public int IntervalMs { get; set; } = 60;

    // Pinch
    public double Delta { get; set; } = 0.08;

    // AimToggle
    public double Sensitivity { get; set; } = 1.0;
    /// <summary>Distance (fraction of shorter side) the held touch may drift before it is re-anchored.</summary>
    public double ResetRadius { get; set; } = 0.22;
    public bool InvertY { get; set; } = false;

    // AndroidKey
    public string? AndroidKey { get; set; }

    public Binding Clone() => (Binding)MemberwiseClone();
}

public sealed class KeymapProfile
{
    public string Name { get; set; } = "Untitled";
    public string? Description { get; set; }
    /// <summary>Optional Android package name this profile is for; used to auto-select a profile later.</summary>
    public string? Package { get; set; }
    /// <summary>Orientation the coordinates were authored for: "portrait" or "landscape".</summary>
    public string Orientation { get; set; } = "portrait";
    public List<Binding> Bindings { get; set; } = [];

    public static KeymapProfile Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), KeymapJsonContext.Default.KeymapProfile)
        ?? throw new InvalidDataException($"Could not read keymap {path}");

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, KeymapJsonContext.Default.KeymapProfile));

    public KeymapProfile Clone()
    {
        var json = JsonSerializer.Serialize(this, KeymapJsonContext.Default.KeymapProfile);
        return JsonSerializer.Deserialize(json, KeymapJsonContext.Default.KeymapProfile)!;
    }

    /// <summary>Validation messages; an empty list means the profile is usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        for (int i = 0; i < Bindings.Count; i++)
        {
            var b = Bindings[i];
            string at = $"binding {i + 1} ({b.Kind}{(b.Label is null ? "" : " " + b.Label)})";
            if (b.Kind == BindingKind.Joystick)
            {
                foreach (var (n, v) in new[] { ("Up", b.Up), ("Down", b.Down), ("Left", b.Left), ("Right", b.Right) })
                    if (!HostKeys.TryParse(v, out _)) problems.Add($"{at}: {n} key '{v}' is not a known host key.");
            }
            else if (!HostKeys.TryParse(b.Key, out _))
                problems.Add($"{at}: key '{b.Key}' is not a known host key.");

            if (b.Kind == BindingKind.AndroidKey && !AndroidKeys.TryGet(b.AndroidKey, out _))
                problems.Add($"{at}: Android key '{b.AndroidKey}' is not known.");
            if (b.X is < 0 or > 1 || b.Y is < 0 or > 1) problems.Add($"{at}: X/Y must be within 0..1.");
        }
        return problems;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(KeymapProfile))]
[JsonSerializable(typeof(Binding))]
public partial class KeymapJsonContext : JsonSerializerContext { }
