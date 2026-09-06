using System.Globalization;
using System.Text.Json;
using TokenMeter.Core;

namespace TokenMeter.Collectors;

/// <summary>
/// Reads Google Antigravity. Antigravity is a VS Code fork, so it keeps per-extension state in the
/// usual globalStorage locations plus its own ~/.antigravity folder. It publishes no documented
/// usage file, so this collector probes the known locations and parses any record that carries a
/// recognisable token shape. When nothing is found the tab says so plainly rather than showing zeros
/// as if they were real.
/// </summary>
public sealed class AntigravityCollector : IUsageCollector
{
    public string Id => "antigravity";
    public string Name => "Antigravity";
    public string Accent => "#a06bf0";

    readonly JsonlCache _cache = new();

    static string[] Roots => Paths.AntigravityRoots;

    public ProviderSnapshot Collect(Settings s)
    {
        var snap = new ProviderSnapshot { Id = Id, Name = Name, Accent = Accent };

        var found = Roots.Where(Directory.Exists).ToList();
        if (found.Count == 0)
        {
            snap.State = ProviderState.NotInstalled;
            snap.Note = "Antigravity is not installed on this machine. Install it and Token Meter will "
                      + "pick up its usage logs automatically on the next refresh.";
            return snap;
        }

        var records = new List<UsageRecord>();
        foreach (var root in found) records.AddRange(Scan(root));

        records = records
            .GroupBy(r => r.TsUtc.Ticks + "|" + r.Model + "|" + r.Total)
            .Select(g => g.First())
            .OrderBy(r => r.TsUtc)
            .ToList();

        if (records.Count == 0)
        {
            snap.State = ProviderState.NoData;
            snap.Note = "Antigravity is installed at " + found[0] + ", but no usage records were found there yet. "
                      + "Antigravity does not publish a documented usage file, so this may stay empty.";
            return snap;
        }

        snap.State = ProviderState.Ok;
        Aggregate.FillCommon(snap, records, s);

        var cache = s.CountCacheReads;
        var today = Aggregate.Today(records).ToList();

        snap.Gauges.Add(new Gauge
        {
            Label = "Spend this week",
            Sub = Fmt.Money(snap.Cost7d) + " of " + Fmt.Money(s.AntigravityWeeklyBudget) + " budget",
            Percent = Fmt.Pct(snap.Cost7d, s.AntigravityWeeklyBudget),
            Value = Fmt.Money(snap.Cost7d),
        });

        snap.Stats.Add(new Stat { Label = "Today", Value = Fmt.Tokens(snap.TokensToday), Sub = Fmt.Count(today.Count, "call") });
        snap.Stats.Add(new Stat { Label = "This week", Value = Fmt.Tokens(snap.Tokens7d), Sub = "7-day rolling" });
        snap.Stats.Add(new Stat { Label = "Models", Value = snap.Models.Count.ToString(), Sub = "seen this week" });
        snap.Stats.Add(new Stat { Label = "Cost today", Value = Fmt.Money(snap.CostToday), Sub = "estimated" });

        return snap;
    }

    IEnumerable<UsageRecord> Scan(string root)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root)
                .EnumerateFiles("*.json*", SearchOption.AllDirectories)
                .Where(f => f.Length is > 0 and < 40_000_000)
                .Where(f => f.Name.Contains("usage", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("telemetry", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("session", StringComparison.OrdinalIgnoreCase)
                         || f.Name.Contains("conversation", StringComparison.OrdinalIgnoreCase))
                .Take(400)
                .ToList();
        }
        catch { yield break; }

        foreach (var f in files)
            foreach (var r in _cache.Get(f, ParseLoose))
                yield return r;
    }

    /// <summary>
    /// Accepts either JSON Lines or a single JSON document, and pulls out any object that carries a
    /// token-count shape under one of the names these tools commonly use.
    /// </summary>
    static List<UsageRecord> ParseLoose(FileInfo f)
    {
        var list = new List<UsageRecord>();
        using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var whole = reader.ReadToEnd();
        foreach (var chunk in whole.Split('\n'))
        {
            var line = chunk.Trim();
            if (line.Length < 20 || line[0] is not ('{' or '[')) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc) Walk(doc.RootElement, f.LastWriteTimeUtc, null, list, 0);
        }

        if (list.Count == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(whole);
                Walk(doc.RootElement, f.LastWriteTimeUtc, null, list, 0);
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Token counts are often nested a level below the timestamp and model that describe them,
    /// so both are carried down the tree and the innermost value wins.
    /// </summary>
    static void Walk(JsonElement e, DateTime ts, string? model, List<UsageRecord> into, int depth)
    {
        if (depth > 8) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, ts, model, into, depth + 1);
                return;

            case JsonValueKind.Object:
                ts = Time(e) ?? ts;
                model = Str(e, "model") ?? Str(e, "modelName") ?? Str(e, "modelId") ?? model;
                if (TryRecord(e, ts, model, out var rec)) into.Add(rec);
                foreach (var p in e.EnumerateObject()) Walk(p.Value, ts, model, into, depth + 1);
                return;
        }
    }

    static readonly string[] InputNames = ["input_tokens", "inputTokens", "promptTokens", "prompt_tokens"];
    static readonly string[] OutputNames = ["output_tokens", "outputTokens", "completionTokens", "completion_tokens"];
    static readonly string[] CacheNames = ["cache_read_input_tokens", "cachedTokens", "cached_input_tokens"];

    static bool TryRecord(JsonElement e, DateTime ts, string? inheritedModel, out UsageRecord rec)
    {
        rec = null!;
        var input = First(e, InputNames);
        var output = First(e, OutputNames);
        if (input + output == 0) return false;

        var cacheRead = First(e, CacheNames);
        var model = inheritedModel ?? "antigravity";

        rec = new UsageRecord
        {
            TsUtc = ts,
            Provider = "antigravity",
            Model = model,
            Input = input,
            Output = output,
            CacheRead = cacheRead,
            Cost = Pricing.Estimate(model, input, output, cacheRead, 0),
        };
        return true;
    }

    static long First(JsonElement e, string[] names)
    {
        foreach (var n in names)
            if (e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number)
                return (long)v.GetDouble();
        return 0;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static DateTime? Time(JsonElement e)
    {
        foreach (var n in new[] { "timestamp", "createdAt", "created_at", "time", "ts" })
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number)
            {
                var raw = v.GetDouble();
                // Heuristic: values past year 2001 in seconds are still 10 digits, ms are 13.
                return raw > 1e12
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)raw).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds((long)raw).UtcDateTime;
            }
            if (v.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
        }
        return null;
    }
}
