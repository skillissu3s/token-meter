using System.Text.Json;
using TokenMeter.Core;

namespace TokenMeter.UI;

/// <summary>
/// Owns the tray icon, the two windows, and the refresh loop. The popup and dashboard are created
/// lazily, so until you open something the app is just an icon and a timer.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    readonly UsageService _usage = new();
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer = new();
    Settings _settings = Settings.Load();

    PopupForm? _popup;
    DashboardForm? _dashboard;
    Icon? _currentTrayIcon;
    bool _refreshing;

    public TrayApp(bool openDashboard = false, bool openPanel = false)
    {
        _tray = new NotifyIcon
        {
            Icon = CoinGlyph.CreateTrayIcon(TrayIconSize(), 0),
            Text = "Token Meter",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _currentTrayIcon = _tray.Icon;

        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _ = ShowDashboardAsync();
        };

        _usage.Updated += OnUsageUpdated;

        _timer.Interval = Math.Max(15, _settings.RefreshSeconds) * 1000;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
        if (openDashboard) _ = ShowDashboardAsync();
        else if (openPanel) TogglePopup();
    }

    /// <summary>The badged coin from the executable, for the taskbar and title bar.</summary>
    static Icon AppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch { }
        return CoinGlyph.CreateTrayIcon(32, 0);
    }

    static int TrayIconSize() => SystemInformation.SmallIconSize.Width switch
    {
        <= 16 => 16,
        <= 20 => 20,
        <= 24 => 24,
        _ => 32,
    };

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.System,
            ShowImageMargin = false,
        };
        menu.Items.Add("Open dashboard", null, async (_, _) => await ShowDashboardAsync());
        menu.Items.Add("Usage panel", null, (_, _) => TogglePopup());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = Startup.IsEnabled(),
        };
        startup.CheckedChanged += (_, _) =>
        {
            Startup.Set(startup.Checked);
            _settings.StartWithWindows = startup.Checked;
            _settings.Save();
        };
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        return menu;
    }

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await _usage.RefreshAsync(_settings); }
        catch { /* a bad read in one collector must not take the tray down */ }
        finally { _refreshing = false; }
    }

    void OnUsageUpdated(Snapshot snap)
    {
        void Apply()
        {
            var icon = CoinGlyph.CreateTrayIcon(TrayIconSize(), snap.Pressure);
            _tray.Icon = icon;
            _currentTrayIcon?.Dispose();
            _currentTrayIcon = icon;

            var head = "Token Meter — " + Fmt.Tokens(snap.TokensToday) + " today";
            var tail = snap.PressureLabel is null
                ? ""
                : "\n" + snap.PressureLabel + " " + snap.Pressure.ToString("0") + "%";
            // The tray tooltip is capped at 127 characters.
            var text = head + tail;
            _tray.Text = text.Length > 127 ? text[..127] : text;

            _ = PushAsync();
        }

        if (_popup?.IsHandleCreated == true) _popup.BeginInvoke(Apply);
        else if (_dashboard?.IsHandleCreated == true) _dashboard.BeginInvoke(Apply);
        else Apply();
    }

    async Task PushAsync()
    {
        var json = _usage.LatestJson;
        if (_popup is not null) await _popup.Panel.PostAsync("snapshot", json);
        if (_dashboard is not null) await _dashboard.Panel.PostAsync("snapshot", json);
    }

    async void TogglePopup()
    {
        if (_popup is not null && _popup.Visible) { _popup.Hide(); return; }

        // The panel has just hidden itself because this very click stole its focus: leave it closed.
        if (_popup is not null && (DateTime.UtcNow - _popup.LastHiddenUtc).TotalMilliseconds < 300) return;

        if (_popup is null)
        {
            var panel = new WebPanel("popup");
            panel.Command += HandleCommandAsync;
            _popup = new PopupForm(panel);
            _popup.ShowNearTray();
            await panel.InitAsync();
        }
        else _popup.ShowNearTray();

        await PushAsync();
        await RefreshAsync();
    }

    async Task ShowDashboardAsync()
    {
        _popup?.Hide();

        if (_dashboard is null)
        {
            var panel = new WebPanel("dashboard");
            panel.Command += HandleCommandAsync;
            _dashboard = new DashboardForm(panel, AppIcon());
            _dashboard.ShowFront();
            await panel.InitAsync();
        }
        else _dashboard.ShowFront();

        await PushAsync();
        await RefreshAsync();
    }

    async Task HandleCommandAsync(string cmd, JsonElement payload)
    {
        switch (cmd)
        {
            case "ready":
                await PushAsync();
                await PushSettingsAsync();
                break;

            case "refresh":
                await RefreshAsync();
                break;

            case "open-dashboard":
                await ShowDashboardAsync();
                break;

            case "close-popup":
                _popup?.Hide();
                break;


            case "quit":
                Quit();
                break;

            case "settings:save":
                ApplySettings(payload);
                await RefreshAsync();
                await PushSettingsAsync();
                break;

            case "open-config":
                OpenFolder(Settings.Dir);
                break;
        }
    }

    void ApplySettings(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return;

        _settings.ClaudePlan = Str(p, "claudePlan") ?? _settings.ClaudePlan;
        if (Settings.ClaudePlans.TryGetValue(_settings.ClaudePlan, out var plan) && plan.FiveHour > 0)
        {
            _settings.ClaudeFiveHourBudget = plan.FiveHour;
            _settings.ClaudeWeeklyBudget = plan.Weekly;
        }
        else
        {
            _settings.ClaudeFiveHourBudget = Long(p, "claudeFiveHourBudget", _settings.ClaudeFiveHourBudget);
            _settings.ClaudeWeeklyBudget = Long(p, "claudeWeeklyBudget", _settings.ClaudeWeeklyBudget);
        }

        _settings.CodexFiveHourBudget = Long(p, "codexFiveHourBudget", _settings.CodexFiveHourBudget);
        _settings.CodexWeeklyBudget = Long(p, "codexWeeklyBudget", _settings.CodexWeeklyBudget);
        _settings.OpenCodeWeeklyBudget = Dbl(p, "openCodeWeeklyBudget", _settings.OpenCodeWeeklyBudget);
        _settings.AntigravityWeeklyBudget = Dbl(p, "antigravityWeeklyBudget", _settings.AntigravityWeeklyBudget);
        _settings.CountCacheReads = Bool(p, "countCacheReads", _settings.CountCacheReads);
        _settings.RefreshSeconds = Math.Clamp((int)Long(p, "refreshSeconds", _settings.RefreshSeconds), 15, 3600);

        _timer.Interval = _settings.RefreshSeconds * 1000;
        _settings.Save();
    }

    async Task PushSettingsAsync()
    {
        var json = JsonSerializer.Serialize(_settings, UsageService.JsonOpts);
        if (_popup is not null) await _popup.Panel.PostAsync("settings", json);
        if (_dashboard is not null) await _dashboard.Panel.PostAsync("settings", json);
    }

    static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static long Long(JsonElement e, string n, long fallback) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : fallback;

    static double Dbl(JsonElement e, string n, double fallback) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static bool Bool(JsonElement e, string n, bool fallback) =>
        e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    void Quit()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _popup?.Dispose();
        _dashboard?.Dispose();
        ExitThread();
    }
}
