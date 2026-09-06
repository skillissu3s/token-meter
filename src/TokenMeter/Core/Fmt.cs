namespace TokenMeter.Core;

/// <summary>Small shared formatters. Kept in one place so every provider tab reads the same.</summary>
public static class Fmt
{
    public static string Tokens(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000d).ToString("0.##") + "B",
        >= 1_000_000 => (n / 1_000_000d).ToString("0.##") + "M",
        >= 1_000 => (n / 1_000d).ToString("0.#") + "K",
        _ => n.ToString(),
    };

    /// <summary>"1 turn", "2 turns" — small thing, but "1 turns" reads like a bug.</summary>
    public static string Count(int n, string noun) => n + " " + noun + (n == 1 ? "" : "s");

    public static string Money(double usd) => usd >= 100 ? "$" + usd.ToString("0") : "$" + usd.ToString("0.00");

    public static double Pct(double used, double budget) =>
        budget <= 0 ? 0 : Math.Min(100, used * 100d / budget);

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalSeconds < 90) return "just now";
        if (d.TotalMinutes < 60) return (int)d.TotalMinutes + "m ago";
        if (d.TotalHours < 24) return (int)d.TotalHours + "h ago";
        return (int)d.TotalDays + "d ago";
    }
}
