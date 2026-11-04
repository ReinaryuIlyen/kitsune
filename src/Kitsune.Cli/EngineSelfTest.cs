using Kitsune.Core.Input;

namespace Kitsune.Cli;

/// <summary>
/// Drives the keymap engine with synthetic host events and checks what reaches the sink: touch
/// coordinates, slot allocation, that every down has a matching up, and that timed gestures
/// (joystick, swipe, autofire, mouse-look) behave as documented.
/// </summary>
internal static class EngineSelfTest
{
    private sealed class RecordingSink : IInputSink
    {
        public readonly List<(long T, int Id, int X, int Y, bool Down)> Events = [];
        public readonly List<(int Code, bool Down)> Keys = [];
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public void Touch(int id, int x, int y, bool down) { lock (Events) Events.Add((_sw.ElapsedMilliseconds, id, x, y, down)); }
        public void Touches(IReadOnlyList<(int Id, int X, int Y, bool Down)> touches) { foreach (var t in touches) Touch(t.Id, t.X, t.Y, t.Down); }
        public void AndroidKey(int evdevCode, bool down) { lock (Keys) Keys.Add((evdevCode, down)); }
        public void Clear() { lock (Events) Events.Clear(); lock (Keys) Keys.Clear(); }
        public List<(long T, int Id, int X, int Y, bool Down)> Snapshot() { lock (Events) return Events.ToList(); }
    }

    private static int _failures;

    private static void Check(bool condition, string what)
    {
        Console.WriteLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
        if (!condition) _failures++;
    }

    public static int Run()
    {
        var sink = new RecordingSink();
        using var engine = new KeymapEngine(sink);
        engine.SetGuestDisplay(1080, 2400);
        engine.SetViewport(100, 200, 540, 1200); // host window at half scale
        engine.Profile = new KeymapProfile
        {
            Name = "selftest",
            Bindings =
            [
                new Binding { Kind = BindingKind.Tap, Key = "Space", X = 0.5, Y = 0.5 },
                new Binding { Kind = BindingKind.TapAtCursor, Key = "E", X = 0, Y = 0 },
                new Binding { Kind = BindingKind.Joystick, Up = "W", Down = "S", Left = "A", Right = "D", X = 0.2, Y = 0.8, Radius = 0.1 },
                new Binding { Kind = BindingKind.Swipe, Key = "WheelUp", X = 0.5, Y = 0.4, ToX = 0.5, ToY = 0.6, DurationMs = 60 },
                new Binding { Kind = BindingKind.Repeat, Key = "F", X = 0.3, Y = 0.3, IntervalMs = 40 },
                new Binding { Kind = BindingKind.Pinch, Key = "WheelDown", X = 0.5, Y = 0.5, Delta = 0.1, DurationMs = 60 },
                new Binding { Kind = BindingKind.AimToggle, Key = "F1", X = 0.6, Y = 0.5, Sensitivity = 1, ResetRadius = 0.05 },
                new Binding { Kind = BindingKind.Tap, Key = "LeftButton", X = 0.9, Y = 0.6 },
                new Binding { Kind = BindingKind.AndroidKey, Key = "Escape", AndroidKey = "Back" },
                new Binding { Kind = BindingKind.ToggleKeymap, Key = "F12" },
            ],
        };

        int VK(string name) => HostKeys.TryParse(name, out var c) ? c : throw new Exception(name);

        Console.WriteLine("Tap");
        sink.Clear();
        Check(engine.OnKey(VK("Space"), true), "Space down is consumed");
        Check(engine.OnKey(VK("Space"), false), "Space up is consumed");
        var ev = sink.Snapshot();
        Check(ev.Count == 2 && ev[0].Down && !ev[1].Down && ev[0].Id == ev[1].Id, "one down and one up on the same slot");
        Check(ev.Count == 2 && ev[0].X == 540 && ev[0].Y == 1200, $"tap lands at (540,1200), got ({ev.ElementAtOrDefault(0).X},{ev.ElementAtOrDefault(0).Y})");
        Check(!engine.OnKey(VK("Z"), true), "unbound key is not consumed");
        engine.OnKey(VK("Z"), false);

        Console.WriteLine("Tap at cursor");
        sink.Clear();
        engine.OnMouseMove(0, 0, 100 + 270, 200 + 300); // host (370,500) -> guest (540,600)
        engine.OnKey(VK("E"), true); engine.OnKey(VK("E"), false);
        ev = sink.Snapshot();
        Check(ev.Count == 2 && Math.Abs(ev[0].X - 540) <= 1 && Math.Abs(ev[0].Y - 600) <= 1, $"cursor maps through the viewport to (540,600), got ({ev.ElementAtOrDefault(0).X},{ev.ElementAtOrDefault(0).Y})");

        Console.WriteLine("Joystick");
        sink.Clear();
        Check(engine.OnKey(VK("W"), true), "W down is consumed");
        Thread.Sleep(30);
        engine.OnKey(VK("D"), true);
        Thread.Sleep(30);
        engine.OnKey(VK("W"), false);
        engine.OnKey(VK("D"), false);
        Thread.Sleep(30);
        ev = sink.Snapshot();
        int cx = 216, cy = 1919, r = 108;
        Check(ev.Count >= 3 && ev[0].Down && ev[0].X == cx && ev[0].Y == cy, "stick starts at its centre");
        Check(ev.Any(e => e.Down && e.X == cx && e.Y == cy - r), "W alone deflects straight up by the radius");
        Check(ev.Any(e => e.Down && Math.Abs(e.X - (cx + r * 0.7071)) <= 1 && Math.Abs(e.Y - (cy - r * 0.7071)) <= 1), "W+D deflects diagonally at unit length");
        Check(!ev[^1].Down && ev.Count(e => e.Down) >= 2 && ev.Count(e => !e.Down) == 1, "stick lifts once when all keys are released");

        Console.WriteLine("Swipe");
        sink.Clear();
        Check(engine.OnWheel(120), "wheel up is consumed");
        Thread.Sleep(120);
        ev = sink.Snapshot();
        Check(ev.Count >= 4 && ev[0].Down && ev[0].Y == 960 && !ev[^1].Down && ev[^1].Y == 1439, $"swipe runs from y=960 to y=1439 and lifts, got {ev.Count} events ending {(ev.Count > 0 ? ev[^1].Y : -1)}");
        Check(ev.Select(e => e.Y).Zip(ev.Skip(1).Select(e => e.Y)).All(p => p.Second >= p.First), "swipe is monotonic");

        Console.WriteLine("Autofire");
        sink.Clear();
        engine.OnKey(VK("F"), true);
        Thread.Sleep(200);
        engine.OnKey(VK("F"), false);
        Thread.Sleep(20);
        ev = sink.Snapshot();
        int downs = ev.Count(e => e.Down), ups = ev.Count(e => !e.Down);
        Check(downs >= 3 && downs == ups, $"autofire produced {downs} taps with matching ups ({ups})");

        Console.WriteLine("Pinch");
        sink.Clear();
        engine.OnWheel(-120);
        Thread.Sleep(120);
        ev = sink.Snapshot();
        var ids = ev.Select(e => e.Id).Distinct().ToList();
        Check(ids.Count == 2, $"pinch uses two slots ({string.Join(',', ids)})");
        var first = ev.Take(2).ToList(); var last = ev.TakeLast(2).ToList();
        Check(first.Count == 2 && last.Count == 2 && Math.Abs(last[1].X - last[0].X) > Math.Abs(first[1].X - first[0].X), "fingers end further apart than they started (zoom in)");
        Check(last.All(e => !e.Down), "both fingers lift at the end");

        Console.WriteLine("Mouse look");
        sink.Clear();
        bool aimOn = false;
        engine.AimModeChanged += on => aimOn = on;
        Check(engine.OnKey(VK("F1"), true), "F1 toggles aim");
        engine.OnKey(VK("F1"), false);
        Check(aimOn && engine.AimMode, "aim mode is on");
        Check(engine.OnMouseMove(10, 0, 0, 0), "mouse motion is consumed in aim mode");
        Thread.Sleep(20);
        ev = sink.Snapshot();
        var (ax, ay) = engine.ToGuest(0.6, 0.5);
        Check(ev.Count >= 2 && ev[0].Down && ev[0].X == ax && ev[0].Y == ay && ev[1].Down && ev[1].X > ax, $"first motion presses at the anchor ({ax},{ay}) then drags right (x={ev.ElementAtOrDefault(1).X})");
        for (int i = 0; i < 30; i++) { engine.OnMouseMove(8, 0, 0, 0); Thread.Sleep(6); }
        ev = sink.Snapshot();
        Check(ev.Any(e => !e.Down) && ev.Count(e => e.Down && e.X == ax && e.Y == ay) >= 2, "drifting past the reset radius lifts and re-anchors");
        Check(engine.OnMouseButton(HostMouseButton.Left, true, 0, 0), "left click is consumed in aim mode");
        engine.OnMouseButton(HostMouseButton.Left, false, 0, 0);
        engine.OnKey(VK("F1"), true); engine.OnKey(VK("F1"), false);
        Thread.Sleep(20);
        Check(!engine.AimMode, "F1 again leaves aim mode");
        ev = sink.Snapshot();
        Check(!ev[^1].Down, "the aim touch is lifted on exit");

        Console.WriteLine("Android key");
        sink.Clear();
        engine.OnKey(VK("Escape"), true); engine.OnKey(VK("Escape"), false);
        Check(sink.Keys.Count == 2 && sink.Keys[0] == (158, true) && sink.Keys[1] == (158, false), "Escape sends KEY_BACK down and up");

        Console.WriteLine("Toggle");
        engine.OnKey(VK("F12"), true); engine.OnKey(VK("F12"), false);
        Thread.Sleep(50);
        Check(!engine.Enabled, "F12 disables the engine");
        Check(!engine.OnKey(VK("Space"), true), "bound keys pass through while disabled");
        engine.OnKey(VK("Space"), false);
        engine.OnKey(VK("F12"), true); engine.OnKey(VK("F12"), false);
        Thread.Sleep(50);
        Check(engine.Enabled, "F12 re-enables the engine");

        Console.WriteLine("Slot hygiene");
        sink.Clear();
        engine.OnKey(VK("Space"), true);
        engine.OnKey(VK("W"), true);
        engine.ReleaseAll();
        Thread.Sleep(20);
        ev = sink.Snapshot();
        var open = ev.GroupBy(e => e.Id).Where(g => g.Last().Down).Select(g => g.Key).ToList();
        Check(open.Count == 0, $"ReleaseAll leaves no slot down ({string.Join(',', open)})");

        Console.WriteLine(_failures == 0 ? "All engine checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 5;
    }
}
