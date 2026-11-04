namespace Kitsune.Core.Input;

public enum HostMouseButton { Left, Right, Middle, X1, X2 }

/// <summary>
/// Names used in keymap profiles mapped to Win32 virtual-key codes. Mouse buttons and wheel
/// directions get synthetic codes above the VK range so a binding can name any host control.
/// </summary>
public static class HostKeys
{
    public const int MouseLeft = 0x1001, MouseRight = 0x1002, MouseMiddle = 0x1003, MouseX1 = 0x1004, MouseX2 = 0x1005;
    public const int WheelUp = 0x1010, WheelDown = 0x1011;

    private static readonly Dictionary<string, int> NameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LeftButton"] = MouseLeft, ["RightButton"] = MouseRight, ["MiddleButton"] = MouseMiddle,
        ["XButton1"] = MouseX1, ["XButton2"] = MouseX2, ["WheelUp"] = WheelUp, ["WheelDown"] = WheelDown,
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Shift"] = 0x10, ["Ctrl"] = 0x11, ["Alt"] = 0x12,
        ["Pause"] = 0x13, ["CapsLock"] = 0x14, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["LeftWin"] = 0x5B, ["RightWin"] = 0x5C,
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62, ["Numpad3"] = 0x63, ["Numpad4"] = 0x64,
        ["Numpad5"] = 0x65, ["Numpad6"] = 0x66, ["Numpad7"] = 0x67, ["Numpad8"] = 0x68, ["Numpad9"] = 0x69,
        ["Multiply"] = 0x6A, ["Add"] = 0x6B, ["Subtract"] = 0x6D, ["Decimal"] = 0x6E, ["Divide"] = 0x6F,
        ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
        ["LeftShift"] = 0xA0, ["RightShift"] = 0xA1, ["LeftCtrl"] = 0xA2, ["RightCtrl"] = 0xA3, ["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
        ["Semicolon"] = 0xBA, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["Backtick"] = 0xC0, ["LeftBracket"] = 0xDB, ["Backslash"] = 0xDC, ["RightBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    private static readonly Dictionary<int, string> CodeToName;

    static HostKeys()
    {
        for (char c = 'A'; c <= 'Z'; c++) NameToCode[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++) NameToCode["D" + c] = c;
        for (int f = 1; f <= 24; f++) NameToCode["F" + f] = 0x70 + f - 1;
        CodeToName = new Dictionary<int, string>();
        foreach (var kv in NameToCode) CodeToName.TryAdd(kv.Value, kv.Key);
    }

    public static bool TryParse(string? name, out int code)
    {
        code = 0;
        return name is not null && NameToCode.TryGetValue(name.Trim(), out code);
    }

    public static string NameOf(int code) => CodeToName.TryGetValue(code, out var n) ? n : $"VK{code:X2}";

    public static IEnumerable<string> AllNames => NameToCode.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    public static int CodeOf(HostMouseButton b) => b switch
    {
        HostMouseButton.Left => MouseLeft, HostMouseButton.Right => MouseRight, HostMouseButton.Middle => MouseMiddle,
        HostMouseButton.X1 => MouseX1, _ => MouseX2,
    };

    /// <summary>Generic Shift/Ctrl/Alt names match either side so profiles need not care which modifier key was pressed.</summary>
    public static bool Matches(int boundCode, int actualCode) =>
        boundCode == actualCode
        || (boundCode == 0x10 && actualCode is 0xA0 or 0xA1)
        || (boundCode == 0x11 && actualCode is 0xA2 or 0xA3)
        || (boundCode == 0x12 && actualCode is 0xA4 or 0xA5);
}

/// <summary>Android keys reachable from a binding, with their Linux evdev codes (what the emulator's virtual keyboard speaks).</summary>
public static class AndroidKeys
{
    private static readonly Dictionary<string, int> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Back"] = 158, ["Home"] = 172, ["Menu"] = 139, ["AppSwitch"] = 580, ["Power"] = 116,
        ["VolumeUp"] = 115, ["VolumeDown"] = 114, ["Enter"] = 28, ["Escape"] = 1, ["Tab"] = 15,
        ["Up"] = 103, ["Down"] = 108, ["Left"] = 105, ["Right"] = 106, ["Space"] = 57, ["Backspace"] = 14,
        ["Camera"] = 212, ["Search"] = 217, ["PlayPause"] = 164,
    };

    public static bool TryGet(string? name, out int evdev)
    {
        evdev = 0;
        return name is not null && Codes.TryGetValue(name.Trim(), out evdev);
    }

    public static string? NameOf(int evdev) => Codes.FirstOrDefault(kv => kv.Value == evdev).Key;

    public static IEnumerable<string> AllNames => Codes.Keys.OrderBy(k => k);
}
