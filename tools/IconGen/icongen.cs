#:package System.Drawing.Common@10.*
// Generates Assets/app.ico: a minimal coin on a flat badge, sized so the rim gap survives 16px.
// Run from the repo root:  dotnet run tools/IconGen/icongen.cs src/TokenMeter/Assets/app.ico
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outPath = args.Length > 0 ? args[0] : "app.ico";
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

// The same warm near-black and cream the rest of the app uses.
var plate = Color.FromArgb(255, 20, 20, 19);
var body = Color.FromArgb(255, 250, 249, 245);

var frames = sizes.Select(s => Render(s, plate, body)).ToList();
WriteIco(outPath, frames);
Console.WriteLine($"wrote {outPath} ({frames.Count} frames)");

static byte[] Render(int size, Color plate, Color body)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // A flat square badge, no rounding: the app icon should look like the UI it opens.
        using (var plateBrush = new SolidBrush(plate))
            g.FillRectangle(plateBrush, 0, 0, size, size);

        var inset = size * 0.16f;
        var state = g.Save();
        g.TranslateTransform(inset, inset);
        CoinGlyph.Draw(g, size - inset * 2, body, body, plate);
        g.Restore(state);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static void WriteIco(string path, List<byte[]> frames)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0);              // reserved
    w.Write((short)1);              // type: icon
    w.Write((short)frames.Count);

    var offset = 6 + 16 * frames.Count;
    foreach (var data in frames)
    {
        var size = SizeOf(data);
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);           // palette
        w.Write((byte)0);           // reserved
        w.Write((short)1);          // colour planes
        w.Write((short)32);         // bits per pixel
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var data in frames) w.Write(data);
}

/// <summary>PNG stores the image width big-endian at byte 16, inside IHDR.</summary>
static int SizeOf(byte[] png) => (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

/// <summary>Kept in sync with src/TokenMeter/UI/CoinGlyph.cs, which draws the live tray icon.</summary>
static class CoinGlyph
{
    public static void Draw(Graphics g, float s, Color body, Color face, Color? gapFill = null)
    {
        float X(float v) => v * s;
        const float outer = 0.46f;
        const float rimRadius = 0.33f;
        var rimWidth = Math.Max(1.4f, s * 0.075f);

        using var bodyBrush = new SolidBrush(body);
        g.FillEllipse(bodyBrush, X(0.5f - outer), X(0.5f - outer), X(outer * 2), X(outer * 2));

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
