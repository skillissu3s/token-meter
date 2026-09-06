using System.Text.Json;

namespace TokenMeter.Core;

/// <summary>
/// Applies the payload the settings page posts. It lives here rather than in the tray class so it
/// can be exercised directly against the exact JSON the page sends, without driving a window.
/// </summary>
public static class SettingsPatch
{
    /// <summary>
    /// Folds <paramref name="payload"/> into <paramref name="s"/>. <paramref name="claude"/> is the
    /// most recent Claude snapshot, needed only to turn a calibration reading into a budget; pass
    /// null when there is none and calibration is skipped.
    /// </summary>
    public static void Apply(Settings s, JsonElement payload, ProviderSnapshot? claude)
    {
        if (payload.ValueKind != JsonValueKind.Object) return;

        var chosen = Str(payload, "claudePlan");
        var planChanged = chosen is not null && chosen != s.ClaudePlan;
        s.ClaudePlan = chosen ?? s.ClaudePlan;

        // Picking a plan loads its starting numbers; otherwise whatever is in the boxes wins.
        if (planChanged && Settings.ClaudePlans.TryGetValue(s.ClaudePlan, out var plan) && plan.FiveHour > 0)
        {
            s.ClaudeFiveHourBudget = plan.FiveHour;
            s.ClaudeWeeklyBudget = plan.Weekly;
        }
        else
        {
            s.ClaudeFiveHourBudget = Dbl(payload, "claudeFiveHourBudget", s.ClaudeFiveHourBudget);
            s.ClaudeWeeklyBudget = Dbl(payload, "claudeWeeklyBudget", s.ClaudeWeeklyBudget);
        }

        // A calibration reading overrides whatever was in the budget boxes, which is the point of it.
        if (claude is not null && claude.Gauges.Count >= 2)
        {
            if (Calibration.Solve(claude.Gauges[0], Dbl(payload, "calibrateFiveHour", 0)) is { } fiveHour)
                s.ClaudeFiveHourBudget = fiveHour;

            if (Calibration.Solve(claude.Gauges[1], Dbl(payload, "calibrateWeekly", 0)) is { } weekly)
                s.ClaudeWeeklyBudget = weekly;
        }

        s.CodexFiveHourBudget = Dbl(payload, "codexFiveHourBudget", s.CodexFiveHourBudget);
        s.CodexWeeklyBudget = Dbl(payload, "codexWeeklyBudget", s.CodexWeeklyBudget);
        s.OpenCodeWeeklyBudget = Dbl(payload, "openCodeWeeklyBudget", s.OpenCodeWeeklyBudget);
        s.AntigravityWeeklyBudget = Dbl(payload, "antigravityWeeklyBudget", s.AntigravityWeeklyBudget);
        s.CountCacheReads = Bool(payload, "countCacheReads", s.CountCacheReads);
        s.RefreshSeconds = Math.Clamp((int)Dbl(payload, "refreshSeconds", s.RefreshSeconds), 15, 3600);
    }

    static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Dbl(JsonElement e, string n, double fallback) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    static bool Bool(JsonElement e, string n, bool fallback) =>
        e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;
}
