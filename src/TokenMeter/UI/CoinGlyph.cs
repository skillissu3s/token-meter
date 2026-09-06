using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;

namespace TokenMeter.UI;

/// <summary>
/// The coin, drawn at runtime so it can follow the taskbar theme. It stays a plain two-tone disc
/// until a limit is worth noticing, at which point the face takes the warning colour — the tray
/// should be quiet unless something needs you. Kept in sync with tools/IconGen/icongen.cs.
/// </summary>
public static partial class CoinGlyph
{
    static readonly Color Amber = Color.FromArgb(255, 217, 164, 65);
    static readonly Color Red = Color.FromArgb(255, 224, 85, 95);

    /// <summary>Null means "no colour": the face is drawn in the body colour and the icon reads as neutral.</summary>
    public static Color? Pressure(double percent) => percent switch
    {
        >= 90 => Red,
        >= 75 => Amber,
        _ => null,
    };

    /// <summary>True when the taskbar is light, so the coin needs to be dark to be visible.</summary>
    public static bool LightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static Icon CreateTrayIcon(int size, double pressurePercent)
    {
        var body = LightTaskbar()
            ? Color.FromArgb(255, 20, 20, 19)
            : Color.FromArgb(255, 250, 249, 245);

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, size, body, Pressure(pressurePercent) ?? body);
        }

        var handle = bmp.GetHicon();
        try
        {
            // Clone so the icon survives DestroyIcon on the temporary handle.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// A filled disc with a ring knocked out just inside the rim. The gap is what makes it read as
    /// a coin rather than a dot, so it is kept proportionally wide enough to survive 16px.
    /// </summary>
    public static void Draw(Graphics g, float s, Color body, Color face, Color? gapFill = null)
    {
        float X(float v) => v * s;
        const float outer = 0.46f;
        const float rimRadius = 0.33f;
        var rimWidth = Math.Max(1.4f, s * 0.075f);

        using var bodyBrush = new SolidBrush(body);
        g.FillEllipse(bodyBrush, X(0.5f - outer), X(0.5f - outer), X(outer * 2), X(outer * 2));

        // Knock the rim gap out of the disc, unless we are sitting on a badge that must stay opaque.
        var prev = g.CompositingMode;
        if (gapFill is null) g.CompositingMode = CompositingMode.SourceCopy;
        using (var pen = new Pen(gapFill ?? Color.Transparent, rimWidth))
            g.DrawEllipse(pen, X(0.5f - rimRadius), X(0.5f - rimRadius), X(rimRadius * 2), X(rimRadius * 2));
        g.CompositingMode = prev;

        if (face == body) return;
        var inner = rimRadius - rimWidth / s / 2f;
        using var faceBrush = new SolidBrush(face);
        g.FillEllipse(faceBrush, X(0.5f - inner), X(0.5f - inner), X(inner * 2), X(inner * 2));
    }
}
