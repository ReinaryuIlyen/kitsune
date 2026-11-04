namespace Kitsune.Core.Input;

/// <summary>
/// Grayscale region comparison on small scaled frames. Frames come from the emulator already scaled
/// (a few hundred pixels wide), so a comparison costs microseconds and can run several times a second.
/// </summary>
public static class ScreenMatcher
{
    /// <summary>Width the emulator is asked to scale frames to for checkpoints; height follows the display aspect.</summary>
    public const int FrameWidth = 192;

    public static (int W, int H) FrameSizeFor(int guestWidth, int guestHeight)
    {
        if (guestWidth <= 0 || guestHeight <= 0) return (FrameWidth, FrameWidth);
        int h = (int)Math.Round(FrameWidth * (double)guestHeight / guestWidth);
        return (FrameWidth, Math.Max(1, h));
    }

    /// <summary>Converts an RGB888 frame to 8-bit grayscale.</summary>
    public static byte[] ToGray(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var gray = new byte[width * height];
        int n = Math.Min(gray.Length, rgb.Length / 3);
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            gray[i] = (byte)((rgb[o] * 77 + rgb[o + 1] * 150 + rgb[o + 2] * 29) >> 8);
        }
        return gray;
    }

    /// <summary>Pixel rectangle of a normalized region inside a frame of the given size.</summary>
    public static (int X, int Y, int W, int H) RegionPixels(double nx, double ny, double nw, double nh, int frameW, int frameH)
    {
        int x = (int)Math.Floor(Math.Clamp(nx, 0, 1) * frameW);
        int y = (int)Math.Floor(Math.Clamp(ny, 0, 1) * frameH);
        int w = Math.Max(1, (int)Math.Round(nw * frameW));
        int h = Math.Max(1, (int)Math.Round(nh * frameH));
        if (x + w > frameW) w = frameW - x;
        if (y + h > frameH) h = frameH - y;
        return (x, y, Math.Max(1, w), Math.Max(1, h));
    }

    public static byte[] Crop(ReadOnlySpan<byte> gray, int frameW, int x, int y, int w, int h)
    {
        var outp = new byte[w * h];
        for (int row = 0; row < h; row++)
            gray.Slice((y + row) * frameW + x, w).CopyTo(outp.AsSpan(row * w, w));
        return outp;
    }

    /// <summary>Mean absolute difference between two same-sized grayscale buffers, 0 (identical) to 255.</summary>
    public static double MeanAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        if (n == 0) return 255;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)n;
    }

    /// <summary>Builds a WaitForScreen step from a frame: the region crop becomes the reference.</summary>
    public static MacroStep MakeCheckpoint(ReadOnlySpan<byte> rgb, int frameW, int frameH, double nx, double ny, double nw, double nh, int tolerance, int timeoutMs, string? label)
    {
        var gray = ToGray(rgb, frameW, frameH);
        var (x, y, w, h) = RegionPixels(nx, ny, nw, nh, frameW, frameH);
        var crop = Crop(gray, frameW, x, y, w, h);
        return new MacroStep
        {
            Kind = MacroStepKind.WaitForScreen,
            X = nx, Y = ny, W = nw, H = nh,
            FrameW = frameW, FrameH = frameH, TemplateW = w, TemplateH = h,
            Template = Convert.ToBase64String(crop),
            Tolerance = tolerance, TimeoutMs = timeoutMs, Label = label,
        };
    }

    /// <summary>Difference between the step's reference and the same region of a live frame of the step's frame size. 255 when sizes disagree.</summary>
    public static double Compare(MacroStep step, ReadOnlySpan<byte> rgb, int frameW, int frameH)
    {
        if (step.Template is null || frameW != step.FrameW || frameH != step.FrameH) return 255;
        var gray = ToGray(rgb, frameW, frameH);
        var (x, y, w, h) = RegionPixels(step.X, step.Y, step.W, step.H, frameW, frameH);
        if (w != step.TemplateW || h != step.TemplateH) return 255;
        var crop = Crop(gray, frameW, x, y, w, h);
        return MeanAbsDiff(crop, Convert.FromBase64String(step.Template));
    }
}
