namespace Kitsune.App.Native;

/// <summary>
/// Locates the emulator's Qt main window by its title and keeps the on-screen rectangle of the guest
/// display current. The window has no device frame, so the client area is the display, aspect-fitted.
/// </summary>
public sealed class WindowTracker
{
    private readonly string _title;
    private readonly uint? _pid;

    public IntPtr Hwnd { get; private set; }
    public RECT ClientRect { get; private set; }
    /// <summary>Screen rectangle (physical pixels) where guest pixels are drawn, after aspect fitting.</summary>
    public RECT DisplayRect { get; private set; }
    public bool Minimized { get; private set; }
    public bool Found => Hwnd != IntPtr.Zero;

    public WindowTracker(string title, uint? pid = null)
    {
        _title = title;
        _pid = pid;
    }

    /// <summary>Refreshes state. Returns true when the window or its rectangle changed.</summary>
    public bool Update(int guestWidth, int guestHeight)
    {
        if (Hwnd != IntPtr.Zero && !Win32.IsWindow(Hwnd)) Hwnd = IntPtr.Zero;
        if (Hwnd == IntPtr.Zero)
        {
            Hwnd = Win32.FindWindowByTitle(_title, _pid);
            if (Hwnd == IntPtr.Zero) Hwnd = Win32.FindWindowByTitle(_title);
            if (Hwnd == IntPtr.Zero) return false;
        }

        bool minimized = Win32.IsIconic(Hwnd);
        var client = Win32.ClientRectOnScreen(Hwnd);
        var display = AspectFit(client, guestWidth, guestHeight);
        bool changed = minimized != Minimized || !Same(client, ClientRect) || !Same(display, DisplayRect);
        Minimized = minimized;
        ClientRect = client;
        DisplayRect = display;
        return changed;
    }

    public bool IsForeground()
    {
        if (Hwnd == IntPtr.Zero) return false;
        var fg = Win32.GetForegroundWindow();
        return fg == Hwnd || Win32.GetAncestor(fg, 3) == Hwnd;
    }

    public static RECT AspectFit(RECT client, int guestWidth, int guestHeight)
    {
        if (client.Width <= 0 || client.Height <= 0 || guestWidth <= 0 || guestHeight <= 0) return client;
        double scale = Math.Min(client.Width / (double)guestWidth, client.Height / (double)guestHeight);
        int w = (int)Math.Round(guestWidth * scale), h = (int)Math.Round(guestHeight * scale);
        int x = client.Left + (client.Width - w) / 2, y = client.Top + (client.Height - h) / 2;
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
}
