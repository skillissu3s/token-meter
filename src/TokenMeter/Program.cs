using TokenMeter.Core;
using TokenMeter.UI;

namespace TokenMeter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // --dump writes the collected snapshot as JSON and exits. Handy for checking what a
        // provider is actually reporting without opening a window.
        var dumpIndex = Array.IndexOf(args, "--dump");
        if (dumpIndex >= 0)
        {
            var service = new UsageService();
            service.RefreshAsync(Settings.Load()).GetAwaiter().GetResult();
            var target = dumpIndex + 1 < args.Length ? args[dumpIndex + 1] : null;
            if (target is null) Console.WriteLine(service.LatestJson);
            else File.WriteAllText(target, service.LatestJson);
            return;
        }

        // One tray icon is enough; a second launch just exits.
        using var single = new Mutex(true, "TokenMeter.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Startup.RecordLaunch("duplicate, already running");
            return;
        }

        Startup.RecordLaunch(args.Contains("--autostart") ? "autostart" : "manual");

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new TrayApp(
            openDashboard: args.Contains("--dashboard"),
            openPanel: args.Contains("--panel")));
    }
}
