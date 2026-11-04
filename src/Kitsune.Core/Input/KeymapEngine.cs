using System.Diagnostics;

namespace Kitsune.Core.Input;

/// <summary>Where mapped input goes. Implemented over the emulator's gRPC input stream.</summary>
public interface IInputSink
{
    void Touch(int id, int x, int y, bool down);
    void Touches(IReadOnlyList<(int Id, int X, int Y, bool Down)> touches);
    void AndroidKey(int evdevCode, bool down);
}

/// <summary>
/// Translates host keyboard and mouse events into guest touches according to a <see cref="KeymapProfile"/>.
/// Thread-safe: hooks call the On* methods from the message loop while a 250 Hz clock drives gestures.
/// Every On* method returns true when the engine consumed the event, meaning the hook must swallow it so
/// the emulator window does not also see it.
/// </summary>
public sealed class KeymapEngine : IDisposable
{
    private readonly IInputSink _sink;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool[] _slots = new bool[10];
    private readonly List<Gesture> _gestures = [];
    private readonly Dictionary<int, Gesture> _heldByKey = [];
    private readonly HashSet<int> _keysDown = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _ticker;

    private KeymapProfile _profile = new();
    private int _guestW = 1080, _guestH = 2400;
    private (int Left, int Top, int Width, int Height) _viewport = (0, 0, 1080, 2400);
    private (int X, int Y) _cursor;
    private bool _enabled = true;
    private AimGesture? _aim;

    public event Action<bool>? AimModeChanged;
    public event Action<bool>? EnabledChanged;

    public KeymapEngine(IInputSink sink)
    {
        _sink = sink;
        _ticker = Task.Factory.StartNew(TickLoop, TaskCreationOptions.LongRunning);
    }

    public KeymapProfile Profile
    {
        get => _profile;
        set { lock (_gate) { ReleaseAllLocked(); _profile = value; } }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            bool changed;
            lock (_gate) { changed = _enabled != value; _enabled = value; if (!value) ReleaseAllLocked(); }
            if (changed) EnabledChanged?.Invoke(value);
        }
    }

    public bool AimMode => _aim is not null;
    public int GuestWidth => _guestW;
    public int GuestHeight => _guestH;

    public void SetGuestDisplay(int width, int height)
    {
        lock (_gate) { _guestW = Math.Max(1, width); _guestH = Math.Max(1, height); }
    }

    /// <summary>Screen rectangle where the guest display is drawn, used to map the host cursor into guest pixels.</summary>
    public void SetViewport(int left, int top, int width, int height)
    {
        lock (_gate) _viewport = (left, top, Math.Max(1, width), Math.Max(1, height));
    }

    public (int X, int Y) ToGuest(double nx, double ny) =>
        ((int)Math.Round(Math.Clamp(nx, 0, 1) * (_guestW - 1)), (int)Math.Round(Math.Clamp(ny, 0, 1) * (_guestH - 1)));

    public (int X, int Y) CursorToGuest(int sx, int sy)
    {
        var v = _viewport;
        double nx = (sx - v.Left) / (double)v.Width;
        double ny = (sy - v.Top) / (double)v.Height;
        return ToGuest(nx, ny);
    }

    private int ShortSide => Math.Min(_guestW, _guestH);

    // ---- host events -------------------------------------------------------------------------

    public bool OnKey(int vk, bool down)
    {
        lock (_gate)
        {
            if (down && !_keysDown.Add(vk)) return _heldByKey.ContainsKey(vk) || _aim is not null && IsBound(vk); // key repeat
            if (!down) _keysDown.Remove(vk);

            // Toggles work even while disabled so the user can always get back.
            foreach (var b in _profile.Bindings)
            {
                if (b.Kind != BindingKind.ToggleKeymap || !HostKeys.TryParse(b.Key, out var tk) || !HostKeys.Matches(tk, vk)) continue;
                if (down) { var next = !_enabled; _ = Task.Run(() => Enabled = next); }
                return true;
            }
            if (!_enabled) return false;

            bool consumed = false;
            foreach (var b in _profile.Bindings)
            {
                switch (b.Kind)
                {
                    case BindingKind.Joystick:
                        if (JoystickKey(b, vk, down)) consumed = true;
                        break;
                    case BindingKind.ToggleKeymap:
                        break;
                    default:
                        if (HostKeys.TryParse(b.Key, out var code) && HostKeys.Matches(code, vk))
                        {
                            Trigger(b, vk, down);
                            consumed = true;
                        }
                        break;
                }
            }
            return consumed;
        }
    }

    public bool OnMouseButton(HostMouseButton button, bool down, int sx, int sy)
    {
        _cursor = (sx, sy);
        int code = HostKeys.CodeOf(button);
        bool consumed = OnKey(code, down);
        // In aim mode the hook owns the mouse entirely; an unbound left click fires at the aim anchor.
        if (!consumed && _aim is not null)
        {
            lock (_gate)
            {
                if (button == HostMouseButton.Left)
                {
                    if (down) { var g = new TapGesture(this, _aim.Binding.X, _aim.Binding.Y); _heldByKey[code] = g; Start(g); }
                    else if (_heldByKey.Remove(code, out var g)) g.Release();
                }
            }
            return true;
        }
        return consumed;
    }

    public bool OnMouseMove(int dx, int dy, int sx, int sy)
    {
        _cursor = (sx, sy);
        var aim = _aim;
        if (aim is null || !_enabled) return false;
        lock (_gate) aim.Move(dx, dy);
        return true;
    }

    public bool OnWheel(int delta)
    {
        int code = delta > 0 ? HostKeys.WheelUp : HostKeys.WheelDown;
        bool consumed;
        lock (_gate)
        {
            consumed = false;
            if (!_enabled) return false;
            foreach (var b in _profile.Bindings)
            {
                if (b.Kind is BindingKind.Joystick or BindingKind.ToggleKeymap) continue;
                if (!HostKeys.TryParse(b.Key, out var c) || c != code) continue;
                Trigger(b, code, true);
                Trigger(b, code, false);
                consumed = true;
            }
        }
        return consumed || _aim is not null;
    }

    public void ReleaseAll()
    {
        lock (_gate) ReleaseAllLocked();
    }

    private bool IsBound(int vk)
    {
        foreach (var b in _profile.Bindings)
        {
            if (b.Kind == BindingKind.Joystick)
            {
                foreach (var k in new[] { b.Up, b.Down, b.Left, b.Right })
                    if (HostKeys.TryParse(k, out var c) && HostKeys.Matches(c, vk)) return true;
            }
            else if (HostKeys.TryParse(b.Key, out var c) && HostKeys.Matches(c, vk)) return true;
        }
        return false;
    }

    // ---- binding dispatch --------------------------------------------------------------------

    private void Trigger(Binding b, int vk, bool down)
    {
        switch (b.Kind)
        {
            case BindingKind.Tap:
                if (down) { var g = new TapGesture(this, b.X, b.Y); _heldByKey[vk] = g; Start(g); }
                else Release(vk);
                break;
            case BindingKind.TapAtCursor:
                if (down)
                {
                    var (gx, gy) = CursorToGuest(_cursor.X, _cursor.Y);
                    var g = new TapGesture(this, gx / (double)(_guestW - 1), gy / (double)(_guestH - 1));
                    _heldByKey[vk] = g; Start(g);
                }
                else Release(vk);
                break;
            case BindingKind.Repeat:
                if (down) { var g = new RepeatGesture(this, b); _heldByKey[vk] = g; Start(g); }
                else Release(vk);
                break;
            case BindingKind.Swipe:
                if (down) Start(new SwipeGesture(this, b.X, b.Y, b.ToX, b.ToY, b.DurationMs));
                break;
            case BindingKind.Pinch:
                if (down) Start(new PinchGesture(this, b));
                break;
            case BindingKind.AimToggle:
                if (down)
                {
                    if (_aim is null) { _aim = new AimGesture(this, b); Start(_aim); AimModeChanged?.Invoke(true); }
                    else { _aim.Release(); _aim = null; AimModeChanged?.Invoke(false); }
                }
                break;
            case BindingKind.AndroidKey:
                if (AndroidKeys.TryGet(b.AndroidKey, out var ev)) _sink.AndroidKey(ev, down);
                break;
        }
    }

    private bool JoystickKey(Binding b, int vk, bool down)
    {
        int dir = -1;
        var keys = new[] { b.Up, b.Right, b.Down, b.Left };
        for (int i = 0; i < 4; i++)
            if (HostKeys.TryParse(keys[i], out var c) && HostKeys.Matches(c, vk)) { dir = i; break; }
        if (dir < 0) return false;

        var existing = _gestures.OfType<JoystickGesture>().FirstOrDefault(j => j.Binding == b);
        if (down)
        {
            if (existing is null) { existing = new JoystickGesture(this, b); Start(existing); }
            existing.SetDirection(dir, true);
        }
        else existing?.SetDirection(dir, false);
        return true;
    }

    private void Release(int vk)
    {
        if (_heldByKey.Remove(vk, out var g)) g.Release();
    }

    private void ReleaseAllLocked()
    {
        foreach (var g in _gestures.ToArray()) g.Release();
        _heldByKey.Clear();
        _keysDown.Clear();
        if (_aim is not null) { _aim = null; AimModeChanged?.Invoke(false); }
    }

    private void Start(Gesture g)
    {
        _gestures.Add(g);
        g.Begin(_clock.ElapsedMilliseconds);
    }

    private int AllocSlot()
    {
        for (int i = 1; i < _slots.Length; i++)
            if (!_slots[i]) { _slots[i] = true; return i; }
        return 9; // all busy: reuse the last slot rather than dropping input
    }

    private void FreeSlot(int id)
    {
        if (id > 0 && id < _slots.Length) _slots[id] = false;
    }

    private void TickLoop()
    {
        var ct = _cts.Token;
        var period = TimeSpan.FromMilliseconds(4);
        var next = _clock.Elapsed;
        while (!ct.IsCancellationRequested)
        {
            next += period;
            var wait = next - _clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            lock (_gate)
            {
                long now = _clock.ElapsedMilliseconds;
                for (int i = _gestures.Count - 1; i >= 0; i--)
                {
                    var g = _gestures[i];
                    if (g.Update(now)) { _gestures.RemoveAt(i); }
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _ticker.Wait(500); } catch { }
        ReleaseAll();
        _cts.Dispose();
    }

    // ---- gestures ----------------------------------------------------------------------------

    private abstract class Gesture
    {
        protected readonly KeymapEngine E;
        protected Gesture(KeymapEngine e) => E = e;
        public abstract void Begin(long now);
        /// <summary>Returns true when finished and the gesture should be dropped.</summary>
        public abstract bool Update(long now);
        public abstract void Release();
    }

    private sealed class TapGesture : Gesture
    {
        private readonly int _x, _y;
        private int _slot = -1;
        private bool _done;
        public TapGesture(KeymapEngine e, double nx, double ny) : base(e) => (_x, _y) = e.ToGuest(nx, ny);
        public override void Begin(long now) { _slot = E.AllocSlot(); E._sink.Touch(_slot, _x, _y, true); }
        public override bool Update(long now) => _done;
        public override void Release()
        {
            if (_done) return;
            _done = true;
            E._sink.Touch(_slot, _x, _y, false);
            E.FreeSlot(_slot);
        }
    }

    private sealed class RepeatGesture : Gesture
    {
        private readonly int _x, _y, _interval;
        private int _slot = -1;
        private bool _down, _released;
        private long _next;
        public RepeatGesture(KeymapEngine e, Binding b) : base(e)
        {
            (_x, _y) = e.ToGuest(b.X, b.Y);
            _interval = Math.Max(16, b.IntervalMs);
        }
        public override void Begin(long now) { _slot = E.AllocSlot(); Press(now); }
        private void Press(long now) { _down = true; E._sink.Touch(_slot, _x, _y, true); _next = now + Math.Max(8, _interval / 2); }
        public override bool Update(long now)
        {
            if (_released) { if (_down) { E._sink.Touch(_slot, _x, _y, false); _down = false; } E.FreeSlot(_slot); return true; }
            if (now < _next) return false;
            if (_down) { E._sink.Touch(_slot, _x, _y, false); _down = false; _next = now + _interval / 2; }
            else Press(now);
            return false;
        }
        public override void Release() => _released = true;
    }

    private sealed class SwipeGesture : Gesture
    {
        private readonly int _x0, _y0, _x1, _y1, _duration;
        private int _slot = -1;
        private long _start;
        private bool _done;
        public SwipeGesture(KeymapEngine e, double nx0, double ny0, double nx1, double ny1, int durationMs) : base(e)
        {
            (_x0, _y0) = e.ToGuest(nx0, ny0);
            (_x1, _y1) = e.ToGuest(nx1, ny1);
            _duration = Math.Max(16, durationMs);
        }
        public override void Begin(long now) { _slot = E.AllocSlot(); _start = now; E._sink.Touch(_slot, _x0, _y0, true); }
        public override bool Update(long now)
        {
            if (_done) return true;
            double t = Math.Clamp((now - _start) / (double)_duration, 0, 1);
            int x = (int)Math.Round(_x0 + (_x1 - _x0) * t), y = (int)Math.Round(_y0 + (_y1 - _y0) * t);
            E._sink.Touch(_slot, x, y, true);
            if (t >= 1) { Release(); return true; }
            return false;
        }
        public override void Release()
        {
            if (_done) return;
            _done = true;
            E._sink.Touch(_slot, _x1, _y1, false);
            E.FreeSlot(_slot);
        }
    }

    private sealed class PinchGesture : Gesture
    {
        private readonly int _cx, _cy, _duration;
        private readonly double _from, _to;
        private int _a = -1, _b = -1;
        private long _start;
        private bool _done;
        public PinchGesture(KeymapEngine e, Binding b) : base(e)
        {
            (_cx, _cy) = e.ToGuest(b.X, b.Y);
            _duration = Math.Max(32, b.DurationMs);
            double span = Math.Abs(b.Delta) * e.ShortSide;
            double baseGap = 0.12 * e.ShortSide;
            // Zoom in: fingers move apart. Zoom out: fingers move together.
            (_from, _to) = b.Delta >= 0 ? (baseGap, baseGap + span) : (baseGap + span, baseGap);
        }
        public override void Begin(long now) { _a = E.AllocSlot(); _b = E.AllocSlot(); _start = now; Emit(_from, true); }
        private void Emit(double gap, bool down)
        {
            int dx = (int)Math.Round(gap / 2);
            E._sink.Touches([(_a, _cx - dx, _cy, down), (_b, _cx + dx, _cy, down)]);
        }
        public override bool Update(long now)
        {
            if (_done) return true;
            double t = Math.Clamp((now - _start) / (double)_duration, 0, 1);
            Emit(_from + (_to - _from) * t, true);
            if (t >= 1) { Release(); return true; }
            return false;
        }
        public override void Release()
        {
            if (_done) return;
            _done = true;
            Emit(_to, false);
            E.FreeSlot(_a); E.FreeSlot(_b);
        }
    }

    private sealed class JoystickGesture : Gesture
    {
        public readonly Binding Binding;
        private readonly int _cx, _cy;
        private readonly double _radius;
        private readonly bool[] _dirs = new bool[4]; // up, right, down, left
        private int _slot = -1;
        private double _curX, _curY;
        private bool _done;
        private long _last;
        public JoystickGesture(KeymapEngine e, Binding b) : base(e)
        {
            Binding = b;
            (_cx, _cy) = e.ToGuest(b.X, b.Y);
            _radius = Math.Max(4, b.Radius * e.ShortSide);
        }
        public void SetDirection(int dir, bool down) => _dirs[dir] = down;
        public override void Begin(long now) { _slot = E.AllocSlot(); _last = now; _curX = _cx; _curY = _cy; E._sink.Touch(_slot, _cx, _cy, true); }
        public override bool Update(long now)
        {
            if (_done) return true;
            if (!_dirs.Any(d => d)) { Release(); return true; }
            double vx = (_dirs[1] ? 1 : 0) - (_dirs[3] ? 1 : 0);
            double vy = (_dirs[2] ? 1 : 0) - (_dirs[0] ? 1 : 0);
            double len = Math.Sqrt(vx * vx + vy * vy);
            if (len > 0) { vx /= len; vy /= len; }
            double tx = _cx + vx * _radius, ty = _cy + vy * _radius;
            if (Binding.RampMs > 0)
            {
                double step = _radius * (now - _last) / Binding.RampMs;
                _curX = Approach(_curX, tx, step);
                _curY = Approach(_curY, ty, step);
            }
            else { _curX = tx; _curY = ty; }
            _last = now;
            E._sink.Touch(_slot, (int)Math.Round(_curX), (int)Math.Round(_curY), true);
            return false;
        }
        private static double Approach(double cur, double target, double step) =>
            Math.Abs(target - cur) <= step ? target : cur + Math.Sign(target - cur) * step;
        public override void Release()
        {
            if (_done) return;
            _done = true;
            E._sink.Touch(_slot, (int)Math.Round(_curX), (int)Math.Round(_curY), false);
            E.FreeSlot(_slot);
        }
    }

    /// <summary>
    /// Mouse-look. A touch is held at the anchor and dragged by relative mouse motion. When it drifts past
    /// the reset radius it is lifted and re-pressed at the anchor on the next tick, which games read as a
    /// brief pause rather than a snap because the lift and press land within one frame.
    /// </summary>
    private sealed class AimGesture : Gesture
    {
        public readonly Binding Binding;
        private readonly int _ax, _ay;
        private readonly double _reset, _sens;
        private int _slot = -1;
        private double _x, _y, _pendDx, _pendDy;
        private bool _down, _done, _needReanchor;
        public AimGesture(KeymapEngine e, Binding b) : base(e)
        {
            Binding = b;
            (_ax, _ay) = e.ToGuest(b.X, b.Y);
            _reset = Math.Max(16, b.ResetRadius * e.ShortSide);
            _sens = b.Sensitivity * (e.ShortSide / 1080.0) * 1.6;
        }
        public void Move(int dx, int dy) { _pendDx += dx; _pendDy += Binding.InvertY ? -dy : dy; }
        public override void Begin(long now) { _slot = E.AllocSlot(); _x = _ax; _y = _ay; }
        public override bool Update(long now)
        {
            if (_done) return true;
            if (_needReanchor)
            {
                _needReanchor = false;
                _x = _ax; _y = _ay;
                E._sink.Touch(_slot, _ax, _ay, true);
                _down = true;
                return false;
            }
            if (_pendDx == 0 && _pendDy == 0) return false;
            if (!_down) { E._sink.Touch(_slot, (int)_x, (int)_y, true); _down = true; }
            _x += _pendDx * _sens; _y += _pendDy * _sens;
            _pendDx = _pendDy = 0;
            double ddx = _x - _ax, ddy = _y - _ay;
            if (Math.Sqrt(ddx * ddx + ddy * ddy) > _reset)
            {
                E._sink.Touch(_slot, (int)_x, (int)_y, false);
                _down = false;
                _needReanchor = true;
                return false;
            }
            E._sink.Touch(_slot, (int)Math.Round(_x), (int)Math.Round(_y), true);
            return false;
        }
        public override void Release()
        {
            if (_done) return;
            _done = true;
            if (_down) E._sink.Touch(_slot, (int)_x, (int)_y, false);
            E.FreeSlot(_slot);
        }
    }
}
