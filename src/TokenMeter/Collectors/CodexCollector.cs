using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenMeter.Core;

namespace TokenMeter.Collectors;

/// <summary>
/// Reads OpenAI Codex. Rollout transcripts under ~/.codex/sessions carry token_count events, and
/// those events also carry the rate_limits block the server sends back, which gives real 5-hour and
/// weekly percentages rather than budget guesses. ~/.codex/state_5.sqlite is used as a fallback so
/// the Codex desktop app still reports totals when no rollout files exist.
/// </summary>
public sealed class CodexCollector : IUsageCollector
{
    public string Id => "codex";
    public string Name => "Codex";
    public string Accent => "#10a37f";

    readonly JsonlCache _cache = new();

    static string Root => Paths.CodexRoot;

    sealed class RateWindow
    {
        public double UsedPercent;
        public int WindowMinutes;
        public DateTime ObservedUtc;
        public DateTime? ResetsAtUtc;
    }

    RateWindow? _primary, _secondary;

    public ProviderSnapshot Collect(Settings s)
    {
        var snap = new ProviderSnapshot { Id = Id, Name = Name, Accent = Accent };

        if (!Directory.Exists(Root))
        {
            snap.State = ProviderState.NotInstalled;
            snap.Note = "No ~/.codex folder found. Install the Codex CLI or desktop app to track it here.";
            return snap;
        }

        snap.Account = ReadAccountPlan();

        _primary = _secondary = null;
        var records = ReadRollouts();
        if (records.Count == 0) records = ReadStateDb();

        if (records.Count == 0)
        {
            snap.State = ProviderState.NoData;
            snap.Note = "Codex is installed but has no recorded sessions yet.";
            return snap;
        }

        snap.State = ProviderState.Ok;
        Aggregate.FillCommon(snap, records, s);

        var cache = s.CountCacheReads;

        // Prefer the numbers the server reported. Fall back to local budgets only when absent.
        snap.Gauges.Add(_primary is not null
            ? Reported(_primary)
            : Budget(records, TimeSpan.FromHours(5), "5-hour window", s.CodexFiveHourBudget));

        snap.Gauges.Add(_secondary is not null
            ? Reported(_secondary)
            : Budget(records, TimeSpan.FromDays(7), "Weekly window", s.CodexWeeklyBudget));

        var today = Aggregate.Today(records).ToList();
        snap.Stats.Add(new Stat { Label = "Today", Value = Fmt.Tokens(snap.TokensToday), Sub = Fmt.Count(today.Count, "turn") });
        snap.Stats.Add(new Stat { Label = "Reasoning", Value = Fmt.Tokens(today.Sum(r => r.Reasoning)), Sub = "today" });
        snap.Stats.Add(new Stat { Label = "Cached input", Value = Fmt.Tokens(today.Sum(r => r.CacheRead)), Sub = "today" });
        snap.Stats.Add(new Stat { Label = "Cost today", Value = Fmt.Money(snap.CostToday), Sub = "estimated, list price" });

        snap.TableTitle = "Recent threads";
        snap.Table.AddRange(records
            .Where(r => r.TsUtc >= DateTime.UtcNow.AddDays(-7))
            .GroupBy(r => r.SessionId)
            .Select(g => new TableRow
            {
                Name = string.IsNullOrWhiteSpace(g.First().Project) ? g.Key : g.First().Project,
                Sub = g.First().Model,
                Tokens = Aggregate.Sum(g, cache),
                Cost = g.Sum(r => r.Cost),
                When = Fmt.Ago(g.Max(r => r.TsUtc)),
            })
            .OrderByDescending(r => r.Tokens)
            .Take(8));

        return snap;
    }

    /// <summary>A window OpenAI reported: the percentage is theirs, and the reset time plus the
    /// window length give us exactly when it opened.</summary>
    static Gauge Reported(RateWindow w) => new()
    {
        Label = WindowName(w.WindowMinutes),
        Sub = "reported " + Fmt.Ago(w.ObservedUtc),
        Percent = Math.Min(100, w.UsedPercent),
        Value = w.UsedPercent.ToString("0.#") + "%",
        WindowStartUtc = w.ResetsAtUtc?.AddMinutes(-w.WindowMinutes),
        ResetsAtUtc = w.ResetsAtUtc,
        Authoritative = true,
    };

    /// <summary>Only reached before Codex has reported a real rate limit; weighted like Claude's.</summary>
    static Gauge Budget(IReadOnlyList<UsageRecord> records, TimeSpan length, string label, double budget)
    {
        var (start, end) = Aggregate.CurrentBlock(records, length);
        var used = Aggregate.SumWeighted(records.Where(r => r.TsUtc >= start));
        return new Gauge
        {
            Label = label,
            Sub = Fmt.Money(used) + " of " + Fmt.Money(budget),
            Percent = Fmt.Pct(used, budget),
            Raw = used,
            Value = Fmt.Money(used),
            WindowStartUtc = start,
            ResetsAtUtc = end,
        };
    }

    static string WindowName(int minutes) => minutes switch
    {
        <= 0 => "Usage window",
        < 90 => minutes + "-minute window",
        < 1440 => (minutes / 60) + "-hour window",
        < 10080 => (minutes / 1440) + "-day window",
        _ => "Weekly window",
    };

    List<UsageRecord> ReadRollouts()
    {
        var sessions = Path.Combine(Root, "sessions");
        if (!Directory.Exists(sessions)) return [];

        var files = new DirectoryInfo(sessions).GetFiles("*.jsonl", SearchOption.AllDirectories);
        _cache.Forget(files.Select(f => f.FullName));

        var all = new List<UsageRecord>();
        foreach (var f in files) all.AddRange(_cache.Get(f, ParseRollout));

        // Rate limits are read fresh every time: only the newest observation is meaningful.
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(3))
            TryReadRateLimits(f);

        return all.OrderBy(r => r.TsUtc).ToList();
    }

    List<UsageRecord> ParseRollout(FileInfo f)
    {
        var list = new List<UsageRecord>();
        var session = Path.GetFileNameWithoutExtension(f.Name);
        var model = "codex";
        var project = "";

        using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 20) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                var kind = payload.TryGetProperty("type", out var pt) ? pt.GetString() : null;

                if (kind == "session_meta" || root.TryGetProperty("type", out var rt) && rt.GetString() == "session_meta")
                {
                    if (payload.TryGetProperty("model", out var mm)) model = mm.GetString() ?? model;
                    if (payload.TryGetProperty("cwd", out var cw))
                        project = Path.GetFileName(cw.GetString()?.TrimEnd('\\', '/') ?? "") ;
                    continue;
                }

                if (kind != "token_count") continue;
                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                // last_token_usage is the delta for this turn; total_token_usage would double count.
                if (!info.TryGetProperty("last_token_usage", out var last)) continue;

                var ts = ParseTs(root) ?? f.LastWriteTimeUtc;
                var input = Num(last, "input_tokens");
                var cached = Num(last, "cached_input_tokens");
                var output = Num(last, "output_tokens");
                var reasoning = Num(last, "reasoning_output_tokens");
                var fresh = Math.Max(0, input - cached);
                if (fresh + output + cached == 0) continue;

                list.Add(new UsageRecord
                {
                    TsUtc = ts,
                    Provider = "codex",
                    Model = model,
                    SessionId = session,
                    Project = project,
                    Input = fresh,
                    Output = output,
                    CacheRead = cached,
                    Reasoning = reasoning,
                    Cost = Pricing.Estimate(model, fresh, output, cached, 0),
                });
            }
        }
        return list;
    }

    void TryReadRateLimits(FileInfo f)
    {
        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("\"rate_limits\"")) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
                    if (!payload.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) continue;
                    var ts = ParseTs(doc.RootElement) ?? f.LastWriteTimeUtc;
                    Take(rl, "primary", ts, ref _primary);
                    Take(rl, "secondary", ts, ref _secondary);
                }
            }
        }
        catch { }

        static void Take(JsonElement rl, string name, DateTime ts, ref RateWindow? slot)
        {
            if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return;
            if (slot is not null && slot.ObservedUtc >= ts) return;
            var resets = Num(w, "resets_in_seconds");
            slot = new RateWindow
            {
                UsedPercent = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
                    ? up.GetDouble() : 0,
                WindowMinutes = (int)Num(w, "window_minutes"),
                ObservedUtc = ts,
                ResetsAtUtc = resets > 0 ? ts.AddSeconds(resets) : null,
            };
        }
    }

    /// <summary>Fallback for the Codex desktop app, which keeps thread totals in SQLite.</summary>
    static List<UsageRecord> ReadStateDb()
    {
        var db = Path.Combine(Root, "state_5.sqlite");
        if (!File.Exists(db)) return [];

        var list = new List<UsageRecord>();
        try
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Cache=Shared");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                select id, coalesce(model, 'codex'), tokens_used,
                       coalesce(updated_at_ms, updated_at * 1000), cwd
                from threads
                where tokens_used > 0
                order by updated_at desc
                limit 500
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tokens = r.GetInt64(2);
                var ms = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                if (ms <= 0) continue;
                var model = r.GetString(1);
                // Only a single total is stored; split it the way a Codex turn usually lands.
                var output = tokens / 5;
                var input = tokens - output;
                list.Add(new UsageRecord
                {
                    TsUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                    Provider = "codex",
                    Model = model,
                    SessionId = r.GetString(0),
                    Project = r.IsDBNull(4) ? "" : Path.GetFileName(r.GetString(4).TrimEnd('\\', '/')),
                    Input = input,
                    Output = output,
                    Cost = Pricing.Estimate(model, input, output, 0, 0),
                });
            }
        }
        catch { }
        return list.OrderBy(r => r.TsUtc).ToList();
    }

    static string? ReadAccountPlan()
    {
        try
        {
            var auth = Path.Combine(Root, "auth.json");
            if (!File.Exists(auth)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(auth));
            if (doc.RootElement.TryGetProperty("tokens", out var t) &&
                t.TryGetProperty("account_id", out _))
                return "signed in";
            return doc.RootElement.TryGetProperty("OPENAI_API_KEY", out var k) &&
                   k.ValueKind == JsonValueKind.String ? "API key" : null;
        }
        catch { return null; }
    }

    static DateTime? ParseTs(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var t) &&
            DateTime.TryParse(t.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;
        return null;
    }

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
}
