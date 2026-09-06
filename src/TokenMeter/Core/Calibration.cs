namespace TokenMeter.Core;

/// <summary>
/// Turning an observed percentage into a budget.
///
/// Claude Code shows you the real limit percentage but does not write it anywhere Token Meter can
/// read, so the only accurate route offline is to take that number as an observation and solve for
/// the budget that would have produced it.
/// </summary>
public static class Calibration
{
    /// <summary>
    /// Returns the budget implied by <paramref name="observedPercent"/>, or null when the reading
    /// cannot be trusted: out of range, nothing measured yet, or a gauge whose local usage is only
    /// part of what the provider counted.
    /// </summary>
    public static double? Solve(Gauge gauge, double observedPercent)
    {
        if (observedPercent is <= 0 or > 100) return null;
        if (gauge.Raw <= 0 || !gauge.Calibratable) return null;
        return Math.Round(gauge.Raw / (observedPercent / 100d), 2);
    }
}
