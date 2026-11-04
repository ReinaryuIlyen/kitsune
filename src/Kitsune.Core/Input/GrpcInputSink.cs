using Android.Emulation.Control;
using Kitsune.Core.Emulator;

namespace Kitsune.Core.Input;

/// <summary>Feeds the keymap engine's touches into the emulator's input stream.</summary>
public sealed class GrpcInputSink : IInputSink
{
    private readonly EmulatorControl _control;
    private readonly int _display;
    public GrpcInputSink(EmulatorControl control, int display = 0) { _control = control; _display = display; }

    public void Touch(int id, int x, int y, bool down) => _control.SendTouch(id, x, y, down, _display);

    public void Touches(IReadOnlyList<(int Id, int X, int Y, bool Down)> touches)
    {
        var list = new List<Android.Emulation.Control.Touch>(touches.Count);
        foreach (var t in touches)
            list.Add(new Android.Emulation.Control.Touch { Identifier = t.Id, X = t.X, Y = t.Y, Pressure = t.Down ? 1 : 0, Expiration = Android.Emulation.Control.Touch.Types.EventExpiration.NeverExpire });
        _control.SendTouches(list, _display);
    }

    public void AndroidKey(int evdevCode, bool down) => _control.SendEvdevKey(evdevCode, down);
}
