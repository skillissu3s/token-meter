using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TokenMeter.UI;

/// <summary>
/// A WebView2 wired to the local UI folder, with a tiny JSON bridge. Both windows share one
/// environment so the second one costs a render process rather than a whole browser.
/// </summary>
public sealed class WebPanel : IDisposable
{
    static CoreWebView2Environment? _shared;
    static string? _webRoot;

    readonly WebView2 _view = new() { DefaultBackgroundColor = Color.FromArgb(255, 20, 20, 19) };
    readonly string _route;
    TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Control Control => _view;
    public event Func<string, JsonElement, Task>? Command;

    public WebPanel(string route)
    {
        _route = route;
        _view.Dock = DockStyle.Fill;
    }

    public async Task InitAsync()
    {
        _webRoot ??= WebAssets.Unpack();
        _shared ??= await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TokenMeter", "webview"),
            options: new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=msWebOOUI,msPdfOOUI --autoplay-policy=no-user-gesture-required",
            });

        await _view.EnsureCoreWebView2Async(_shared);
        var core = _view.CoreWebView2;

        core.SetVirtualHostNameToFolderMapping(
            WebAssets.VirtualHost, _webRoot, CoreWebView2HostResourceAccessKind.Allow);

        var s = core.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreDevToolsEnabled = false;

        core.WebMessageReceived += OnMessage;
        // Anything that is not our own page opens in the real browser instead.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{WebAssets.VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenExternally(e.Uri);
            }
        };

        core.Navigate($"https://{WebAssets.VirtualHost}/index.html?v={WebAssets.Build}#{_route}");
    }

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(e.WebMessageAsJson).RootElement; }
        catch { return; }

        var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        var payload = root.TryGetProperty("payload", out var p) ? p : default;

        if (cmd == "ready") _ready.TrySetResult();
        if (Command is not null) await Command.Invoke(cmd, payload);
    }

    public async Task PostAsync(string type, string dataJson)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
        if (_view.CoreWebView2 is null) return;
        var message = $"{{\"type\":\"{type}\",\"data\":{dataJson}}}";
        try { _view.CoreWebView2.PostWebMessageAsJson(message); } catch { }
    }

    static void OpenExternally(string uri)
    {
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    public void Dispose() => _view.Dispose();
}
