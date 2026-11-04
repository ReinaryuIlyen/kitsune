using System.Runtime.InteropServices;
using Kitsune.Core.Input;

namespace Kitsune.App.Native;

/// <summary>
/// Global keyboard and mouse hooks. They are installed on the UI thread (hooks need a message loop) and
/// forward to a filter which decides whether the event is consumed. Injected events are always passed
/// through so synthetic input from other tools keeps working.
/// </summary>
public sealed class LowLevelHooks : IDisposable
{
    private readonly Win32.HookProc _kbProc;
    private readonly Win32.HookProc _mouseProc;
    private IntPtr _kbHook, _mouseHook;
    private POINT _lastMouse;
    private bool _haveLast;

    public Func<bool>? IsActive { get; set; }
    public Func<int, bool, bool>? OnKey { get; set; }
    public Func<HostMouseButton, bool, int, int, bool>? OnMouseButton { get; set; }
    public Func<int, int, int, int, bool>? OnMouseMove { get; set; }
    public Func<int, bool>? OnWheel { get; set; }
    /// <summary>When set, mouse moves are measured relative to this point and the cursor is pinned back to it.</summary>
    public POINT? PinCursorTo { get; set; }

    public LowLevelHooks()
    {
        _kbProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Install()
    {
        if (_kbHook != IntPtr.Zero) return;
        var module = Win32.GetModuleHandle(null);
        _kbHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc, module, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            throw new InvalidOperationException($"Could not install input hooks (error {Marshal.GetLastWin32Error()}).");
    }

    public void Uninstall()
    {
        if (_kbHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsActive?.Invoke() == true)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Win32.LLKHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
                if ((down || up) && OnKey?.Invoke((int)info.vkCode, down) == true)
                    return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsActive?.Invoke() == true)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Win32.LLMHF_INJECTED) == 0)
            {
                int msg = (int)wParam;
                bool consumed = false;
                switch (msg)
                {
                    case Win32.WM_MOUSEMOVE:
                    {
                        int dx, dy;
                        if (PinCursorTo is { } pin)
                        {
                            dx = info.pt.X - pin.X; dy = info.pt.Y - pin.Y;
                            if (dx != 0 || dy != 0) Win32.SetCursorPos(pin.X, pin.Y);
                            _lastMouse = pin; _haveLast = true;
                        }
                        else
                        {
                            dx = _haveLast ? info.pt.X - _lastMouse.X : 0;
                            dy = _haveLast ? info.pt.Y - _lastMouse.Y : 0;
                            _lastMouse = info.pt; _haveLast = true;
                        }
                        consumed = OnMouseMove?.Invoke(dx, dy, info.pt.X, info.pt.Y) == true;
                        break;
                    }
                    case Win32.WM_LBUTTONDOWN: consumed = Button(HostMouseButton.Left, true, info); break;
                    case Win32.WM_LBUTTONUP: consumed = Button(HostMouseButton.Left, false, info); break;
                    case Win32.WM_RBUTTONDOWN: consumed = Button(HostMouseButton.Right, true, info); break;
                    case Win32.WM_RBUTTONUP: consumed = Button(HostMouseButton.Right, false, info); break;
                    case Win32.WM_MBUTTONDOWN: consumed = Button(HostMouseButton.Middle, true, info); break;
                    case Win32.WM_MBUTTONUP: consumed = Button(HostMouseButton.Middle, false, info); break;
                    case Win32.WM_XBUTTONDOWN:
                    case Win32.WM_XBUTTONUP:
                    {
                        var which = (info.mouseData >> 16) == 1 ? HostMouseButton.X1 : HostMouseButton.X2;
                        consumed = Button(which, msg == Win32.WM_XBUTTONDOWN, info);
                        break;
                    }
                    case Win32.WM_MOUSEWHEEL:
                    {
                        short delta = (short)(info.mouseData >> 16);
                        consumed = OnWheel?.Invoke(delta) == true;
                        break;
                    }
                }
                if (consumed) return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool Button(HostMouseButton b, bool down, Win32.MSLLHOOKSTRUCT info) =>
        OnMouseButton?.Invoke(b, down, info.pt.X, info.pt.Y) == true;

    public void Dispose() => Uninstall();
}
