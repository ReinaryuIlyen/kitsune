using System.Diagnostics;

namespace Kitsune.Core.Input;

/// <summary>
/// Sits between the keymap engine and the emulator: everything the engine sends is forwarded unchanged
/// and, while recording, written down with a timestamp. Mouse input that goes to the emulator window
/// directly is reported by the hook layer through <see cref="RecordMouse"/>. Coordinates are stored
/// normalized so the recording is resolution-independent.
/// </summary>
public sealed class MacroRecorder : IInputSink
{
    private readonly IInputSink _inner;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private Macro? _macro;
    private int _guestW = 1, _guestH = 1;
    private bool _mouseDown;
    private long _lastMoveMs;

    public MacroRecorder(IInputSink inner) => _inner = inner;

    public bool Recording => _macro is not null;
    public int StepCount { get { lock (_gate) return _macro?.Steps.Count ?? 0; } }
    public int ElapsedMs => Recording ? (int)_clock.ElapsedMilliseconds : 0;

    public void SetGuestDisplay(int width, int height) { _guestW = Math.Max(1, width); _guestH = Math.Max(1, height); }

    public void Start(string name)
    {
        lock (_gate)
        {
            _macro = new Macro
            {
                Name = name, GuestWidth = _guestW, GuestHeight = _guestH,
                Orientation = _guestW > _guestH ? "landscape" : "portrait", RecordedAt = DateTimeOffset.UtcNow,
            };
            _mouseDown = false;
            _clock.Restart();
        }
    }

    /// <summary>Stops and returns the recording; any contact still down gets a matching release appended.</summary>
    public Macro? Stop()
    {
        lock (_gate)
        {
            var m = _macro;
            _macro = null;
            _clock.Stop();
            if (m is null) return null;
            var open = new Dictionary<int, MacroStep>();
            foreach (var s in m.Steps.Where(s => s.Kind == MacroStepKind.Touch))
            {
                if (s.Down) open[s.Id] = s; else open.Remove(s.Id);
            }
            foreach (var s in open.Values)
                m.Steps.Add(new MacroStep { Kind = MacroStepKind.Touch, T = (int)_clock.ElapsedMilliseconds, Id = s.Id, X = s.X, Y = s.Y, Down = false });
            return m;
        }
    }

    // ---- IInputSink pass-through with capture ------------------------------------------------

    public void Touch(int id, int x, int y, bool down)
    {
        _inner.Touch(id, x, y, down);
        Record(id, x, y, down);
    }

    public void Touches(IReadOnlyList<(int Id, int X, int Y, bool Down)> touches)
    {
        _inner.Touches(touches);
        foreach (var t in touches) Record(t.Id, t.X, t.Y, t.Down);
    }

    public void AndroidKey(int evdevCode, bool down)
    {
        _inner.AndroidKey(evdevCode, down);
        if (_macro is null) return;
        var name = AndroidKeys.NameOf(evdevCode);
        if (name is null) return;
        lock (_gate) _macro?.Steps.Add(new MacroStep { Kind = MacroStepKind.AndroidKey, T = (int)_clock.ElapsedMilliseconds, AndroidKey = name, Down = down });
    }

    /// <summary>Host mouse on the emulator window, already mapped to guest pixels. Uses contact 0, which the keymap engine never allocates.</summary>
    public void RecordMouse(int gx, int gy, bool? buttonDown)
    {
        if (_macro is null) return;
        long now = _clock.ElapsedMilliseconds;
        if (buttonDown is true) { _mouseDown = true; Record(0, gx, gy, true); _lastMoveMs = now; }
        else if (buttonDown is false) { if (_mouseDown) { _mouseDown = false; Record(0, gx, gy, false); } }
        else if (_mouseDown && now - _lastMoveMs >= 16) { Record(0, gx, gy, true); _lastMoveMs = now; }
    }

    public void AddWait(int ms)
    {
        lock (_gate) _macro?.Steps.Add(new MacroStep { Kind = MacroStepKind.Wait, T = (int)_clock.ElapsedMilliseconds, Ms = ms });
    }

    /// <summary>Appends a checkpoint built from a live frame (requested via <see cref="ScreenMatcher.FrameSizeFor"/>); the recording timeline is not paused, playback simply waits here.</summary>
    public void AddCheckpoint(ReadOnlySpan<byte> rgb, int frameW, int frameH, double nx, double ny, double nw, double nh, int tolerance, int timeoutMs, string? label)
    {
        var step = ScreenMatcher.MakeCheckpoint(rgb, frameW, frameH, nx, ny, nw, nh, tolerance, timeoutMs, label);
        lock (_gate)
        {
            if (_macro is null) return;
            step.T = (int)_clock.ElapsedMilliseconds;
            _macro.Steps.Add(step);
        }
    }

    private void Record(int id, int x, int y, bool down)
    {
        if (_macro is null) return;
        lock (_gate)
        {
            _macro?.Steps.Add(new MacroStep
            {
                Kind = MacroStepKind.Touch, T = (int)_clock.ElapsedMilliseconds, Id = id,
                X = Math.Clamp(x / (double)Math.Max(1, _guestW - 1), 0, 1), Y = Math.Clamp(y / (double)Math.Max(1, _guestH - 1), 0, 1), Down = down,
            });
        }
    }
}
