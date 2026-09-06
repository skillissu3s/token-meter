using System.Reflection;

namespace TokenMeter.UI;

/// <summary>
/// The UI ships as embedded resources. WebView2 serves a folder, so they are unpacked to
/// LocalAppData on startup. They are rewritten every launch rather than skipped when present:
/// a rebuild that keeps the same assembly version must still replace the old interface.
/// </summary>
public static class WebAssets
{
    public const string VirtualHost = "tokenmeter.local";

    /// <summary>
    /// Changes with every build, so it can bust WebView2's HTTP cache without disabling caching
    /// within a build.
    /// </summary>
    public static string Build { get; } =
        typeof(WebAssets).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    public static string Unpack()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenMeter", "web");
        Directory.CreateDirectory(dir);

        var asm = Assembly.GetExecutingAssembly();
        const string prefix = "TokenMeter.web.";

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

            // Resource names flatten the folder separator, so only the final extension is a real dot.
            var rest = name[prefix.Length..];
            var lastDot = rest.LastIndexOf('.');
            var file = lastDot < 0 ? rest : rest[..lastDot].Replace('.', Path.DirectorySeparatorChar) + rest[lastDot..];
            var target = Path.Combine(dir, file);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var src = asm.GetManifestResourceStream(name);
            if (src is null) continue;
            try
            {
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            catch (IOException)
            {
                // Another instance is writing the same file; whatever it wrote is the same content.
            }
        }

        StampAssetLinks(Path.Combine(dir, "index.html"));
        return dir;
    }

    /// <summary>
    /// The stylesheet and script are referenced by plain name, so WebView2 would happily serve the
    /// previous build's copies from its HTTP cache. Tagging them with the build id makes each
    /// build a distinct URL.
    /// </summary>
    static void StampAssetLinks(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return;
            var html = File.ReadAllText(indexPath)
                .Replace("\"styles.css\"", $"\"styles.css?v={Build}\"")
                .Replace("\"app.js\"", $"\"app.js?v={Build}\"");
            File.WriteAllText(indexPath, html);
        }
        catch (IOException) { }
    }
}
