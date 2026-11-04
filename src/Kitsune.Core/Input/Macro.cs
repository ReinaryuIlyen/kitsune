using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitsune.Core.Input;

[JsonConverter(typeof(JsonStringEnumConverter<MacroStepKind>))]
public enum MacroStepKind
{
    /// <summary>One multitouch contact changing state: down, move (Down=true again) or up.</summary>
    Touch,
    /// <summary>An Android key (Back, Home, ...) going down or up.</summary>
    AndroidKey,
    /// <summary>An explicit pause of Ms before the next step.</summary>
    Wait,
    /// <summary>Block until a region of the screen looks like the stored reference, then continue; the rest of the timeline shifts by however long that took.</summary>
    WaitForScreen,
}

public sealed class MacroStep
{
    public MacroStepKind Kind { get; set; }
    /// <summary>Milliseconds since the macro started, as recorded.</summary>
    public int T { get; set; }

    // Touch
    public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool Down { get; set; }

    // AndroidKey
    public string? AndroidKey { get; set; }

    // Wait
    public int Ms { get; set; }

    // WaitForScreen: region (normalized) and a reference crop taken from a frame scaled to FrameW x FrameH
    public double W { get; set; }
    public double H { get; set; }
    public int FrameW { get; set; }
    public int FrameH { get; set; }
    public int TemplateW { get; set; }
    public int TemplateH { get; set; }
    /// <summary>Base64 of the reference crop, 8-bit grayscale, TemplateW x TemplateH.</summary>
    public string? Template { get; set; }
    /// <summary>Mean absolute grayscale difference (0..255) at or below which the screen counts as matching.</summary>
    public int Tolerance { get; set; } = 14;
    public int TimeoutMs { get; set; } = 30000;
    public bool StopOnTimeout { get; set; } = true;
    public string? Label { get; set; }
}

/// <summary>
/// A recorded input script. Every coordinate is a fraction of the guest display, so a macro recorded
/// on one resolution replays on another, and on every reroll clone at once.
/// </summary>
public sealed class Macro
{
    public string Name { get; set; } = "Untitled";
    public string? Description { get; set; }
    public string Orientation { get; set; } = "portrait";
    public int GuestWidth { get; set; }
    public int GuestHeight { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    /// <summary>How many times Play runs the script by default; 0 means until stopped.</summary>
    public int Repeat { get; set; } = 1;
    public List<MacroStep> Steps { get; set; } = [];

    public int DurationMs => Steps.Count == 0 ? 0 : Steps.Max(s => s.T + (s.Kind == MacroStepKind.Wait ? s.Ms : 0));

    public static Macro Load(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), MacroJsonContext.Default.Macro)
        ?? throw new InvalidDataException($"Could not read macro {path}");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, MacroJsonContext.Default.Macro));

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var down = new HashSet<int>();
        int lastT = 0;
        for (int i = 0; i < Steps.Count; i++)
        {
            var s = Steps[i];
            string at = $"step {i + 1} ({s.Kind} at {s.T} ms)";
            if (s.T < lastT) problems.Add($"{at}: timestamps must not go backwards.");
            lastT = s.T;
            switch (s.Kind)
            {
                case MacroStepKind.Touch:
                    if (s.X is < 0 or > 1 || s.Y is < 0 or > 1) problems.Add($"{at}: X/Y must be within 0..1.");
                    if (s.Down) down.Add(s.Id); else if (!down.Remove(s.Id)) problems.Add($"{at}: touch {s.Id} released without a press.");
                    break;
                case MacroStepKind.AndroidKey:
                    if (!AndroidKeys.TryGet(s.AndroidKey, out _)) problems.Add($"{at}: unknown Android key '{s.AndroidKey}'.");
                    break;
                case MacroStepKind.Wait:
                    if (s.Ms < 0) problems.Add($"{at}: negative wait.");
                    break;
                case MacroStepKind.WaitForScreen:
                    if (string.IsNullOrEmpty(s.Template) || s.TemplateW <= 0 || s.TemplateH <= 0 || s.FrameW <= 0 || s.FrameH <= 0)
                        problems.Add($"{at}: missing reference image.");
                    else if (TemplateLength(s.Template) != s.TemplateW * s.TemplateH)
                        problems.Add($"{at}: reference image is not valid base64 of TemplateW x TemplateH bytes.");
                    if (s.W <= 0 || s.H <= 0 || s.X < 0 || s.Y < 0 || s.X + s.W > 1.0001 || s.Y + s.H > 1.0001) problems.Add($"{at}: region must lie within the screen.");
                    break;
            }
        }
        if (down.Count > 0) problems.Add($"touch(es) {string.Join(',', down)} are still down at the end; the player will release them, but the recording is probably truncated.");
        return problems;

        static int TemplateLength(string base64)
        {
            try { return Convert.FromBase64String(base64).Length; }
            catch (FormatException) { return -1; }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(Macro))]
[JsonSerializable(typeof(MacroStep))]
public partial class MacroJsonContext : JsonSerializerContext { }
