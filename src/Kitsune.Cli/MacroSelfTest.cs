using Kitsune.Core.Input;

namespace Kitsune.Cli;

/// <summary>Recorder, matcher and player checks against in-memory fakes: no emulator needed.</summary>
internal static class MacroSelfTest
{
    private sealed class NullSink : IInputSink
    {
        public int Calls;
        public void Touch(int id, int x, int y, bool down) => Calls++;
        public void Touches(IReadOnlyList<(int Id, int X, int Y, bool Down)> touches) => Calls += touches.Count;
        public void AndroidKey(int evdevCode, bool down) => Calls++;
    }

    private sealed class FakeTarget : IMacroTarget
    {
        public int GuestWidth => 1080;
        public int GuestHeight => 2400;
        public readonly List<(long T, string What)> Log = [];
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public byte[] Frame = [];
        public int FrameW, FrameH;
        public int FramesServed;
        public Func<int, byte[]>? FrameAt;
        public void Touch(int id, int x, int y, bool down) => Log.Add((_sw.ElapsedMilliseconds, $"touch {id} {x},{y} {(down ? "down" : "up")}"));
        public void AndroidKey(int evdevCode, bool down) => Log.Add((_sw.ElapsedMilliseconds, $"key {evdevCode} {(down ? "down" : "up")}"));
        public Task<(byte[] Rgb, int Width, int Height)> GetFrameAsync(int width, int height, CancellationToken ct)
        {
            FramesServed++;
            var f = FrameAt is null ? Frame : FrameAt(FramesServed);
            return Task.FromResult((f, FrameW, FrameH));
        }
    }

    private static int _failures;
    private static void Check(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) _failures++; }

    private static byte[] SolidFrame(int w, int h, byte r, byte g, byte b, (int X, int Y, int W, int H, byte R, byte G, byte B)? patch = null)
    {
        var f = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { f[i * 3] = r; f[i * 3 + 1] = g; f[i * 3 + 2] = b; }
        if (patch is { } p)
            for (int y = p.Y; y < p.Y + p.H; y++)
                for (int x = p.X; x < p.X + p.W; x++) { int o = (y * w + x) * 3; f[o] = p.R; f[o + 1] = p.G; f[o + 2] = p.B; }
        return f;
    }

    public static int Run()
    {
        Console.WriteLine("Recorder");
        var sink = new NullSink();
        var rec = new MacroRecorder(sink);
        rec.SetGuestDisplay(1080, 2400);
        rec.Touch(1, 540, 1200, true);                       // not recording yet: forwarded, not stored
        rec.Start("test");
        rec.Touch(1, 540, 1200, true);
        Thread.Sleep(40);
        rec.Touch(1, 540, 1300, true);
        rec.Touch(1, 540, 1300, false);
        rec.RecordMouse(100, 200, true);
        rec.RecordMouse(110, 210, null);                     // within 16 ms of the press: throttled away
        Thread.Sleep(20);
        rec.RecordMouse(120, 220, null);
        rec.RecordMouse(120, 220, false);
        rec.AndroidKey(158, true); rec.AndroidKey(158, false);
        rec.AddWait(250);
        rec.Touch(2, 10, 10, true);                          // left down on purpose
        var m = rec.Stop()!;
        Check(sink.Calls == 7, $"every engine call reached the inner sink, mouse ones did not ({sink.Calls})");
        Check(!rec.Recording, "recorder stopped");
        Check(m.Steps.Count == 11, $"steps recorded: {m.Steps.Count} (3 engine touches, 3 mouse, 2 keys, 1 wait, 1 touch, 1 auto-release)");
        Check(m.Steps[0].Kind == MacroStepKind.Touch && Math.Abs(m.Steps[0].X - 540 / 1079.0) < 1e-9 && Math.Abs(m.Steps[0].Y - 1200 / 2399.0) < 1e-9, "coordinates are normalized to the display");
        Check(m.Steps[1].T >= 40, $"timestamps advance ({m.Steps[1].T} ms)");
        Check(m.Steps.Count(s => s.Kind == MacroStepKind.Touch && s.Id == 0) == 3, "mouse: press, one throttled move, release");
        Check(m.Steps.Any(s => s.Kind == MacroStepKind.AndroidKey && s.AndroidKey == "Back" && s.Down), "evdev 158 recorded as Back");
        Check(m.Steps[^1].Kind == MacroStepKind.Touch && m.Steps[^1].Id == 2 && !m.Steps[^1].Down, "a contact left down gets an auto-release at stop");
        Check(m.Validate().Count == 0, "recording validates clean");

        Console.WriteLine("Matcher");
        int fw = 192, fh = 427;
        var frameA = SolidFrame(fw, fh, 20, 20, 20, (60, 100, 40, 40, 255, 255, 255));
        var frameB = SolidFrame(fw, fh, 20, 20, 20);
        var cp = ScreenMatcher.MakeCheckpoint(frameA, fw, fh, 0.3, 0.22, 0.25, 0.12, 14, 5000, "white box");
        Check(cp.TemplateW == 48 && cp.TemplateH == 51, $"region crops to {cp.TemplateW}x{cp.TemplateH} px");
        Check(ScreenMatcher.Compare(cp, frameA, fw, fh) < 0.01, "identical frame compares as 0");
        double diffB = ScreenMatcher.Compare(cp, frameB, fw, fh);
        Check(diffB > 100, $"frame without the box differs strongly ({diffB:F0})");
        Check(ScreenMatcher.Compare(cp, frameA, fw + 1, fh) == 255, "a frame of another size never matches");
        var noisy = (byte[])frameA.Clone();
        var rnd = new Random(1);
        for (int i = 0; i < noisy.Length; i++) noisy[i] = (byte)Math.Clamp(noisy[i] + rnd.Next(-10, 11), 0, 255);
        double diffNoisy = ScreenMatcher.Compare(cp, noisy, fw, fh);
        Check(diffNoisy <= 14, $"mild compression noise stays within tolerance ({diffNoisy:F1})");

        Console.WriteLine("Player");
        var target = new FakeTarget { FrameW = fw, FrameH = fh };
        var macro = new Macro
        {
            Name = "play", GuestWidth = 1080, GuestHeight = 2400,
            Steps =
            [
                new MacroStep { Kind = MacroStepKind.Touch, T = 0, Id = 1, X = 0.5, Y = 0.5, Down = true },
                new MacroStep { Kind = MacroStepKind.Touch, T = 60, Id = 1, X = 0.5, Y = 0.5, Down = false },
                WithT(cp, 100),
                new MacroStep { Kind = MacroStepKind.Touch, T = 150, Id = 1, X = 0.1, Y = 0.1, Down = true },
                new MacroStep { Kind = MacroStepKind.AndroidKey, T = 200, AndroidKey = "Back", Down = true },
                new MacroStep { Kind = MacroStepKind.AndroidKey, T = 220, AndroidKey = "Back", Down = false },
            ],
        };
        // The checkpoint's frame only appears on the 3rd poll: playback must stall there, then shift everything after it.
        target.FrameAt = n => n >= 3 ? frameA : frameB;
        var player = new MacroPlayer(target) { PollIntervalMs = 100 };
        int done = player.RunAsync(macro, 1, CancellationToken.None).GetAwaiter().GetResult();
        Check(done == 1, "one iteration completed");
        Check(target.FramesServed == 3, $"checkpoint polled until it matched ({target.FramesServed} frames)");
        var up = target.Log.First(l => l.What.StartsWith("touch 1") && l.What.EndsWith("up"));
        var after = target.Log.First(l => l.What == "touch 1 108,240 down");
        Check(after.T - up.T >= 200 + 90 - 20, $"steps after the checkpoint were delayed by the wait ({after.T - up.T} ms gap, recorded 90)");
        Check(target.Log.Any(l => l.What == "key 158 down") && target.Log.Any(l => l.What == "key 158 up"), "Android key replayed");
        Check(target.Log[^1].What == "touch 1 0,0 up", "a contact left down at the end is released by the player");
        Check(target.Log[0].What == "touch 1 540,1200 down", $"coordinates scaled to the target ({target.Log[0].What})");

        Console.WriteLine("Player: checkpoint timeout");
        var target2 = new FakeTarget { FrameW = fw, FrameH = fh, Frame = frameB };
        var cpShort = ScreenMatcher.MakeCheckpoint(frameA, fw, fh, 0.3, 0.22, 0.25, 0.12, 14, 250, "never");
        cpShort.T = 0;
        var macro2 = new Macro { Steps = [cpShort, new MacroStep { Kind = MacroStepKind.Touch, T = 10, Id = 1, X = 0, Y = 0, Down = true }] };
        int done2 = new MacroPlayer(target2) { PollIntervalMs = 50 }.RunAsync(macro2, 3, CancellationToken.None).GetAwaiter().GetResult();
        Check(done2 == 0 && target2.Log.Count == 0, "StopOnTimeout stops the run before the following steps");

        Console.WriteLine(_failures == 0 ? "All macro checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 5;
    }

    private static MacroStep WithT(MacroStep s, int t) { s.T = t; return s; }
}
