using System.Globalization;
using System.Text.Json;
using TokenMeter.Core;

namespace TokenMeter.Collectors;

/// <summary>
/// Reads Claude Code transcripts from ~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl.
/// Every assistant turn carries a message.usage block, and that is the whole source of truth here.
/// Anthropic does not expose plan limits to the machine, so the 5-hour and weekly gauges are
/// measured against budgets set in Settings and are marked non-authoritative in the UI.
/// </summary>
public sealed class ClaudeCodeCollector : IUsageCollector
{
    public string Id => "claude";
    public string Name => "Claude Code";
    public string Accent => "#d97757";

    readonly JsonlCache _cache = new();

    static string Root => Paths.ClaudeRoot;

    public ProviderSnapshot Collect(Settings s)
    {
        var snap = new ProviderSnapshot { Id = Id, Name = Name, Accent = Accent };
        var projects = Path.Combine(Root, "projects");

        if (!Directory.Exists(projects))
        {
            snap.State = ProviderState.NotInstalled;
            snap.Note = "No ~/.claude/projects folder found. Run Claude Code once and usage will show up here.";
            return snap;
        }

        var files = new DirectoryInfo(projects).GetFiles("*.jsonl", SearchOption.AllDirectories);
        _cache.Forget(files.Select(f => f.FullName));

        var records = new List<UsageRecord>();
        foreach (var f in files) records.AddRange(_cache.Get(f, ParseFile));

        // A resumed or forked session replays earlier turns into a new file, so the same API call
        // can appear several times. Collapse on the response id the transcript records.
        records = records
            .GroupBy(r => r.SessionId + "|" + r.Model + "|" + r.TsUtc.Ticks + "|" + r.Total)
            .Select(g => g.First())
            .OrderBy(r => r.TsUtc)
            .ToList();

        if (records.Count == 0)
        {
            snap.State = ProviderState.NoData;
            snap.Note = "Claude Code is present but has not recorded any token usage yet.";
            return snap;
        }

        snap.State = ProviderState.Ok;
        Aggregate.FillCommon(snap, records, s);

        var cache = s.CountCacheReads;

        // The 5-hour window is anchored to the first call after the previous one expired, which is
        // how the limit behaves and what makes elapsed time and pace meaningful.
        var (blockStart, blockEnd) = Aggregate.CurrentBlock(records, TimeSpan.FromHours(5));
        var blockUsed = Aggregate.SumWeighted(records.Where(r => r.TsUtc >= blockStart));
        snap.Gauges.Add(new Gauge
        {
            Label = "5-hour window",
            Sub = Fmt.Money(blockUsed) + " of " + Fmt.Money(s.ClaudeFiveHourBudget),
            Percent = Fmt.Pct(blockUsed, s.ClaudeFiveHourBudget),
            Raw = blockUsed,
            Value = Fmt.Money(blockUsed),
            WindowStartUtc = blockStart,
            ResetsAtUtc = blockEnd,
        });

        // The weekly figure is a rolling 7-day sum. It deliberately has no window start: we cannot
        // know where Anthropic's week began, and a rolling total has no elapsed time to pace against.
        var weekUsed = Aggregate.SumWeighted(Aggregate.Since(records, DateTime.UtcNow.AddDays(-7)));
        var historyDays = (DateTime.UtcNow - records[0].TsUtc).TotalDays;
        snap.Gauges.Add(new Gauge
        {
            Label = "Weekly, rolling 7 days",
            Sub = historyDays < 6.5
                ? Fmt.Money(weekUsed) + " of " + Fmt.Money(s.ClaudeWeeklyBudget) + " · only "
                  + Fmt.Span(historyDays) + " of history here"
                : Fmt.Money(weekUsed) + " of " + Fmt.Money(s.ClaudeWeeklyBudget),
            Percent = Fmt.Pct(weekUsed, s.ClaudeWeeklyBudget),
            Raw = weekUsed,
            Value = Fmt.Money(weekUsed),
            // Solving a weekly budget from a few hours of transcripts would bake in a badly wrong
            // number, so calibration stays shut until a full week has accumulated locally.
            Calibratable = historyDays >= 6.5,
        });

        var today = Aggregate.Today(records).ToList();
        var todayOut = today.Sum(r => r.Output);
        var todayRead = today.Sum(r => r.CacheRead);
        var todayFresh = today.Sum(r => r.Input + r.CacheWrite);
        var thinking = today.Sum(r => r.Reasoning);

        snap.Stats.Add(new Stat { Label = "Today", Value = Fmt.Tokens(snap.TokensToday), Sub = Fmt.Count(today.Count, "turn") });
        snap.Stats.Add(new Stat
        {
            Label = "Output",
            Value = Fmt.Tokens(todayOut),
            Sub = thinking > 0 ? Fmt.Tokens(thinking) + " thinking" : "today",
        });
        snap.Stats.Add(new Stat { Label = "Cache read", Value = Fmt.Tokens(todayRead), Sub = HitRate(todayRead, todayFresh) });
        snap.Stats.Add(new Stat { Label = "Cost today", Value = Fmt.Money(snap.CostToday), Sub = "estimated, list price" });

        snap.TableTitle = "Projects this week";
        snap.Table.AddRange(records
            .Where(r => r.TsUtc >= DateTime.UtcNow.AddDays(-7))
            .GroupBy(r => r.Project)
            .Select(g => new TableRow
            {
                Name = g.Key,
                Sub = Fmt.Count(g.Select(r => r.SessionId).Distinct().Count(), "session"),
                Tokens = Aggregate.Sum(g, cache),
                Cost = g.Sum(r => r.Cost),
                When = Fmt.Ago(g.Max(r => r.TsUtc)),
            })
            .OrderByDescending(r => r.Tokens)
            .Take(8));

        return snap;
    }

    static List<UsageRecord> ParseFile(FileInfo f)
    {
        var project = PrettyProject(f.Directory?.Name ?? "");
        var fallbackSession = Path.GetFileNameWithoutExtension(f.Name);
        var list = new List<UsageRecord>();

        // Claude Code may be writing to this file right now, so share every access.
        using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 40 || !line.Contains("\"usage\"")) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") continue;
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (!msg.TryGetProperty("usage", out var u)) continue;

                var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(model) || model == "<synthetic>") continue;

                var record = Build(root, msg, u, model, f.LastWriteTimeUtc, project, fallbackSession);
                if (record.Total > 0) list.Add(record);
            }
        }
        return list;
    }

    static UsageRecord Build(JsonElement root, JsonElement msg, JsonElement u, string model,
        DateTime fallbackTs, string project, string fallbackSession)
    {
        var ts = fallbackTs;
        if (root.TryGetProperty("timestamp", out var tse) &&
            DateTime.TryParse(tse.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            ts = parsed;

        var input = Num(u, "input_tokens");
        var output = Num(u, "output_tokens");
        var cacheRead = Num(u, "cache_read_input_tokens");
        var cacheWrite = Num(u, "cache_creation_input_tokens");
        long thinking = 0;
        if (u.TryGetProperty("output_tokens_details", out var d)) thinking = Num(d, "thinking_tokens");

        return new UsageRecord
        {
            TsUtc = ts,
            Provider = "claude",
            Model = model,
            SessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? fallbackSession : fallbackSession,
            Project = project,
            Input = input,
            Output = output,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            Reasoning = thinking,
            Cost = Pricing.Estimate(model, input, output, cacheRead, cacheWrite),
            CostIsReported = false,
        };
    }

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    static readonly Dictionary<string, string> ProjectNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claude Code encodes a project path as C--Users-me-code-token-meter, which is ambiguous:
    /// a dash is both the separator and a legal character in a folder name. Walking the encoded
    /// segments against the real filesystem resolves it, so "token-meter" does not read as "meter".
    /// If the folder has since been moved or deleted there is nothing to check against, and the
    /// trailing segment is the best guess available.
    /// </summary>
    static string PrettyProject(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return "unknown";
        if (ProjectNames.TryGetValue(dir, out var cached)) return cached;

        var parts = dir.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0 ? dir : parts[^1];

        if (parts.Length > 1 && parts[0].Length == 1)
        {
            var path = parts[0] + ":\\";
            var pending = "";
            var leaf = "";
            for (var i = 1; i < parts.Length; i++)
            {
                pending = pending.Length == 0 ? parts[i] : pending + "-" + parts[i];
                var candidate = Path.Combine(path, pending);
                if (!Directory.Exists(candidate)) continue;
                path = candidate;
                leaf = pending;
                pending = "";
            }
            // Only trust the walk if it consumed every segment.
            if (pending.Length == 0 && leaf.Length > 0) name = leaf;
        }

        ProjectNames[dir] = name;
        return name;
    }

    static string HitRate(long read, long fresh)
    {
        var total = read + fresh;
        return total == 0 ? "today" : (read * 100d / total).ToString("0") + "% of context cached";
    }
}
