using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenMeter.Core;

public sealed class Settings
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenMeter");
    static string File => Path.Combine(Dir, "settings.json");

    /// <summary>Anthropic publishes no token numbers for plan limits, so these are targets you set.</summary>
    public string ClaudePlan { get; set; } = "max5";
    public long ClaudeFiveHourBudget { get; set; } = 88_000_000;
    public long ClaudeWeeklyBudget { get; set; } = 880_000_000;

    public long CodexFiveHourBudget { get; set; } = 12_000_000;
    public long CodexWeeklyBudget { get; set; } = 120_000_000;

    /// <summary>USD/week you are comfortable spending through OpenCode's own API keys.</summary>
    public double OpenCodeWeeklyBudget { get; set; } = 50;
    public double AntigravityWeeklyBudget { get; set; } = 50;

    public int RefreshSeconds { get; set; } = 60;
    public bool CountCacheReads { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public static readonly Dictionary<string, (long FiveHour, long Weekly)> ClaudePlans = new()
    {
        ["pro"] = (19_000_000, 190_000_000),
        ["max5"] = (88_000_000, 880_000_000),
        ["max20"] = (220_000_000, 2_200_000_000),
        ["custom"] = (0, 0),
    };

    public static Settings Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new Settings();
        }
        catch { /* a corrupt settings file should never stop the tray from starting */ }
        return new Settings();
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
