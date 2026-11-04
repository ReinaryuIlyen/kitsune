using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitsune.Core.Reroll;

public sealed class RerollClone
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset LastReset { get; set; }
}

/// <summary>
/// A reroll set: one frozen copy of a template instance's disks plus N copy-on-write clones.
/// Each clone is a complete AVD the emulator can run on its own; its writable layer is a qcow2
/// overlay a few megabytes in size until the game writes to it. Resetting a clone recreates that
/// overlay; keeping one flattens it into a normal, independent instance.
/// </summary>
public sealed class RerollSet
{
    public string Name { get; set; } = "";
    public string Template { get; set; } = "";
    public string SystemImage { get; set; } = "";
    public int RamMb { get; set; } = 3072;
    public int Cores { get; set; } = 2;
    public bool SaveSnapshots { get; set; } = false;
    public DateTimeOffset CreatedAt { get; set; }
    public List<RerollClone> Clones { get; set; } = [];

    [JsonIgnore] public string Dir { get; set; } = "";
    [JsonIgnore] public string BaseDir => Path.Combine(Dir, "base");
    [JsonIgnore] public string ManifestFile => Path.Combine(Dir, "reroll.json");

    public string CloneDir(RerollClone c) => Path.Combine(Dir, c.Name + ".avd");

    public static RerollSet Load(string dir)
    {
        var set = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir, "reroll.json")), RerollJsonContext.Default.RerollSet)
            ?? throw new InvalidDataException("Unreadable reroll manifest.");
        set.Dir = dir;
        return set;
    }

    public void Save() => File.WriteAllText(ManifestFile, JsonSerializer.Serialize(this, RerollJsonContext.Default.RerollSet));
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RerollSet))]
internal partial class RerollJsonContext : JsonSerializerContext { }
