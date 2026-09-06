using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenMeter.Core;

public sealed class Settings
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMeter");
    static string File => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// Budgets are held as equivalent API spend, not as a token count. Providers do not charge a
    /// flat rate per token — cached input is roughly a tenth of fresh input, output several times
    /// more, and Opus several times Sonnet — and their limits are consumed the same way. Weighting
    /// by price is the closest a local estimate gets to how a window is actually eaten.
    /// You are not being billed these amounts on a subscription; it is the unit, not an invoice.
    /// </summary>
    public string ClaudePlan { get; set; } = "max5";
    public double ClaudeFiveHourBudget { get; set; } = 88;
    public double ClaudeWeeklyBudget { get; set; } = 700;

    public double CodexFiveHourBudget { get; set; } = 25;
    public double CodexWeeklyBudget { get; set; } = 200;

    /// <summary>USD/week you are comfortable spending through OpenCode's own API keys.</summary>
    public double OpenCodeWeeklyBudget { get; set; } = 50;
    public double AntigravityWeeklyBudget { get; set; } = 50;

    public int RefreshSeconds { get; set; } = 60;
    public bool CountCacheReads { get; set; } = true;
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Starting points only. Anthropic publishes no numbers, so these are rough and the honest way
    /// to get an accurate gauge is to calibrate against a percentage Claude Code actually showed you.
    /// </summary>
    public static readonly Dictionary<string, (double FiveHour, double Weekly)> ClaudePlans = new()
    {
        ["pro"] = (18, 140),
        ["max5"] = (88, 700),
        ["max20"] = (350, 2800),
        ["custom"] = (0, 0),
    };

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File));
                if (loaded is not null) return loaded.Migrated();
            }
        }
        catch { /* a corrupt settings file should never stop the tray from starting */ }
        return new Settings();
    }

    /// <summary>
    /// Budgets used to be token counts and are now equivalent spend. A file written by the older
    /// build would leave a budget of "88000000 dollars", pinning every gauge at 0% forever, so
    /// anything that large is treated as stale and replaced with this plan's starting point.
    /// </summary>
    Settings Migrated()
    {
        const double implausible = 100_000;
        var fallback = ClaudePlans.TryGetValue(ClaudePlan, out var plan) && plan.FiveHour > 0
            ? plan
            : ClaudePlans["max5"];

        if (ClaudeFiveHourBudget >= implausible) ClaudeFiveHourBudget = fallback.FiveHour;
        if (ClaudeWeeklyBudget >= implausible) ClaudeWeeklyBudget = fallback.Weekly;
        if (CodexFiveHourBudget >= implausible) CodexFiveHourBudget = new Settings().CodexFiveHourBudget;
        if (CodexWeeklyBudget >= implausible) CodexWeeklyBudget = new Settings().CodexWeeklyBudget;
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
