using System.Management;

namespace Kitsune.Core.Emulator;

/// <summary>An emulator process on this machine, found by its command line rather than started by us.</summary>
public sealed record DiscoveredEmulator(string Avd, int Pid, int ConsolePort, int GrpcPort, string CommandLine);

/// <summary>
/// Finds running emulator.exe processes and reads the AVD name and ports off their command lines, so the
/// launcher can show and re-attach to instances that outlived it (or were started from the CLI).
/// </summary>
public static class EmulatorDiscovery
{
    public static IReadOnlyList<DiscoveredEmulator> Discover()
    {
        var list = new List<DiscoveredEmulator>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'emulator.exe'");
            foreach (var mo in searcher.Get())
            {
                var cmd = mo["CommandLine"]?.ToString();
                if (string.IsNullOrEmpty(cmd)) continue;
                int pid = Convert.ToInt32(mo["ProcessId"]);
                var tokens = Tokenize(cmd);
                string? avd = Arg(tokens, "-avd");
                if (avd is null) continue;
                int console = int.TryParse(Arg(tokens, "-port"), out var c) ? c : 5554;
                int grpc = int.TryParse(Arg(tokens, "-grpc"), out var g) ? g : 0;
                list.Add(new DiscoveredEmulator(avd, pid, console, grpc, cmd));
            }
        }
        catch { }
        return list;
    }

    public static DiscoveredEmulator? Find(string avdName) =>
        Discover().FirstOrDefault(d => string.Equals(d.Avd, avdName, StringComparison.OrdinalIgnoreCase));

    private static string? Arg(List<string> tokens, string name)
    {
        int i = tokens.IndexOf(name);
        return i >= 0 && i + 1 < tokens.Count ? tokens[i + 1] : null;
    }

    private static List<string> Tokenize(string cmd)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var ch in cmd)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted) { if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
