namespace TokenMeter.Core;

/// <summary>One billable model call, normalised across every provider.</summary>
public sealed record UsageRecord
{
    public required DateTime TsUtc { get; init; }
    public required string Provider { get; init; }
    public string Model { get; init; } = "unknown";
    public string SessionId { get; init; } = "";
    public string Project { get; init; } = "";
    public long Input { get; init; }
    public long Output { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public long Reasoning { get; init; }
    /// <summary>Cost in USD. Reported by the provider when it knows it, otherwise estimated.</summary>
    public double Cost { get; init; }
    public bool CostIsReported { get; init; }

    /// <summary>Everything the model read or wrote. Cache reads count — they are billed, just cheaply.</summary>
    public long Total => Input + Output + CacheRead + CacheWrite;
}

public enum ProviderState { Ok, NoData, NotInstalled, Error }

/// <summary>A labelled progress gauge, e.g. "5-hour window", 62% used, resets in 1h 40m.</summary>
public sealed class Gauge
{
    public string Label { get; set; } = "";
    public string? Sub { get; set; }
    public double Percent { get; set; }
    /// <summary>True when the provider itself told us the percentage; false when it is measured against a budget the user set.</summary>
    public bool Authoritative { get; set; }
    public string? Value { get; set; }
    public DateTime? ResetsAtUtc { get; set; }

    /// <summary>
    /// When the window opened. Together with <see cref="ResetsAtUtc"/> this gives how far through
    /// the window you are, which is what makes a pace reading possible. Null for a rolling total,
    /// where "elapsed" has no meaning.
    /// </summary>
    public DateTime? WindowStartUtc { get; set; }

    /// <summary>
    /// Usage in the same unit as the budget, unclamped. <see cref="Percent"/> stops at 100, so this
    /// is what calibration needs in order to solve for a budget when you have overshot the current one.
    /// </summary>
    public double Raw { get; set; }

    /// <summary>
    /// False when solving a budget from an observed percentage would produce a wrong answer —
    /// chiefly when local history is too short to cover the window, so <see cref="Raw"/> is only
    /// part of the usage the provider actually counted.
    /// </summary>
    public bool Calibratable { get; set; } = true;
}

public sealed class Stat
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Sub { get; set; }
}

public sealed class ModelSlice
{
    public string Model { get; set; } = "";
    public long Tokens { get; set; }
    public double Cost { get; set; }
    public int Calls { get; set; }
}

public sealed class Bucket
{
    /// <summary>Bucket start, local time, ISO 8601.</summary>
    public string T { get; set; } = "";
    public string Label { get; set; } = "";
    public long Tokens { get; set; }
    public double Cost { get; set; }
}

/// <summary>Everything the UI needs to draw one provider's tab.</summary>
public sealed class ProviderSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "#8b8b8b";
    public ProviderState State { get; set; } = ProviderState.NoData;
    public string? Note { get; set; }
    public string? Account { get; set; }
    public DateTime? LastActivityUtc { get; set; }

    public List<Gauge> Gauges { get; } = [];
    public List<Stat> Stats { get; } = [];
    public List<ModelSlice> Models { get; } = [];
    public List<Bucket> Daily { get; } = [];
    public List<Bucket> Hourly { get; } = [];

    /// <summary>Free-form rows shown in the provider's own section, e.g. per-project or per-thread lines.</summary>
    public string? TableTitle { get; set; }
    public List<TableRow> Table { get; } = [];

    public long TokensToday { get; set; }
    public long Tokens7d { get; set; }
    public long TokensTotal { get; set; }
    public double CostToday { get; set; }
    public double Cost7d { get; set; }
    public double CostTotal { get; set; }
    public bool CostEstimated { get; set; }
}

public sealed class TableRow
{
    public string Name { get; set; } = "";
    public string? Sub { get; set; }
    public long Tokens { get; set; }
    public double Cost { get; set; }
    public string? When { get; set; }
}

/// <summary>The full payload handed to the web UI on every refresh.</summary>
public sealed class Snapshot
{
    public string GeneratedAt { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public List<ProviderSnapshot> Providers { get; } = [];

    public long TokensToday { get; set; }
    public long Tokens7d { get; set; }
    public long TokensTotal { get; set; }
    public double CostToday { get; set; }
    public double Cost7d { get; set; }
    public double CostTotal { get; set; }

    public List<Bucket> Daily { get; } = [];
    public List<ModelSlice> Models { get; } = [];
    /// <summary>Highest gauge across all providers — drives the tray icon colour.</summary>
    public double Pressure { get; set; }
    public string? PressureLabel { get; set; }
}
