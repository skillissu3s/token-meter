using System.Runtime.InteropServices;

namespace TokenMeter.UI;

/// <summary>Just enough DWM to square the corners and darken the title bar on Windows 11.</summary>
internal static partial class Native
{
    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwaBorderColor = 34;
    const int CornerSquare = 1;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>The UI has no rounding anywhere, so the window frame should not add any either.</summary>
    public static void SquareCorners(IntPtr hwnd)
    {
        var pref = CornerSquare;
        TrySet(hwnd, DwmwaWindowCornerPreference, ref pref);
    }

    public static void DarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        TrySet(hwnd, DwmwaUseImmersiveDarkMode, ref on);
    }

    public static void BorderColor(IntPtr hwnd, Color color)
    {
        var bgr = color.R | (color.G << 8) | (color.B << 16);
        TrySet(hwnd, DwmwaBorderColor, ref bgr);
    }

    static void TrySet(IntPtr hwnd, int attr, ref int value)
    {
        // These attributes simply do nothing on older builds, so a failure is never worth surfacing.
        try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch { }
    }
}
