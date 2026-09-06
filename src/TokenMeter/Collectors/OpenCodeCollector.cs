using System.Text.Json;
using TokenMeter.Core;

namespace TokenMeter.Collectors;

/// <summary>
/// Reads OpenCode from ~/.local/share/opencode/opencode.db. OpenCode runs against your own API
/// keys across many providers and records the real dollar cost per message, so this view leads
/// with spend and with the provider/model mix rather than with plan limits, which do not exist here.
/// </summary>
public sealed class OpenCodeCollector : IUsageCollector
{
    public string Id => "opencode";
    public string Name => "OpenCode";
    public string Accent => "#5b8def";

    static string Db => Paths.OpenCodeDb;

    public ProviderSnapshot Collect(Settings s)
    {
        var snap = new ProviderSnapshot { Id = Id, Name = Name, Accent = Accent };

        if (!File.Exists(Db))
        {
            snap.State = ProviderState.NotInstalled;
            snap.Note = "No OpenCode database found at ~/.local/share/opencode/opencode.db.";
            return snap;
        }

        var records = SqliteReader.Read(Db, ReadMessages) ?? [];
        if (records.Count == 0) records = SqliteReader.Read(Db, ReadSessions) ?? [];

        if (records.Count == 0)
        {
            snap.State = ProviderState.NoData;
            snap.Note = "OpenCode is installed but has not recorded any sessions yet.";
            return snap;
        }

        snap.State = ProviderState.Ok;
        Aggregate.FillCommon(snap, records, s);

        var cache = s.CountCacheReads;
        var today = Aggregate.Today(records).ToList();

        snap.Gauges.Add(new Gauge
        {
            Label = "Spend this week",
            Sub = Fmt.Money(snap.Cost7d) + " of " + Fmt.Money(s.OpenCodeWeeklyBudget) + " budget",
            Percent = Fmt.Pct(snap.Cost7d, s.OpenCodeWeeklyBudget),
            Value = Fmt.Money(snap.Cost7d),
        });

        snap.Stats.Add(new Stat { Label = "Spend today", Value = Fmt.Money(snap.CostToday), Sub = Fmt.Count(today.Count, "message") });
        snap.Stats.Add(new Stat { Label = "Tokens today", Value = Fmt.Tokens(snap.TokensToday), Sub = "input + output + cache" });
        snap.Stats.Add(new Stat
        {
            Label = "Providers",
            Value = records.Select(r => Provider(r.Model)).Distinct().Count().ToString(),
            Sub = string.Join(", ", records
                .Where(r => r.TsUtc >= DateTime.UtcNow.AddDays(-7))
                .GroupBy(r => Provider(r.Model))
                .OrderByDescending(g => g.Sum(x => x.Cost))
                .Take(2)
                .Select(g => g.Key)),
        });
        snap.Stats.Add(new Stat
        {
            Label = "Sessions",
            Value = records.Select(r => r.SessionId).Distinct().Count().ToString(),
            Sub = "all time",
        });

        snap.TableTitle = "Projects this week";
        snap.Table.AddRange(records
            .Where(r => r.TsUtc >= DateTime.UtcNow.AddDays(-7) && !string.IsNullOrEmpty(r.Project))
            .GroupBy(r => r.Project)
            .Select(g => new TableRow
            {
                Name = g.Key,
                Sub = Fmt.Count(g.Select(r => r.SessionId).Distinct().Count(), "session"),
                Tokens = Aggregate.Sum(g, cache),
                Cost = g.Sum(r => r.Cost),
                When = Fmt.Ago(g.Max(r => r.TsUtc)),
            })
            .OrderByDescending(r => r.Cost)
            .Take(8));

        return snap;
    }

    /// <summary>Per-message rows give real timestamps, which is what the charts need.</summary>
    static List<UsageRecord> ReadMessages(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var projects = ProjectNames(c);
        var list = new List<UsageRecord>();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            select m.data, m.time_created, s.project_id
            from message m
            left join session s on s.id = m.session_id
            order by m.time_created desc
            limit 20000
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(r.GetString(0)); } catch { continue; }
            using (doc)
            {
                var d = doc.RootElement;
                if (d.ValueKind != JsonValueKind.Object) continue;
                if (!d.TryGetProperty("role", out var role) || role.GetString() != "assistant") continue;
                if (!d.TryGetProperty("tokens", out var tk) || tk.ValueKind != JsonValueKind.Object) continue;

                var provider = Str(d, "providerID");
                var modelId = Str(d, "modelID");
                var model = string.IsNullOrEmpty(provider) ? modelId : provider + "/" + modelId;
                if (string.IsNullOrEmpty(model)) model = "unknown";

                long input = Num(tk, "input"), output = Num(tk, "output"), reasoning = Num(tk, "reasoning");
                long cRead = 0, cWrite = 0;
                if (tk.TryGetProperty("cache", out var ca) && ca.ValueKind == JsonValueKind.Object)
                {
                    cRead = Num(ca, "read");
                    cWrite = Num(ca, "write");
                }
                if (input + output + cRead + cWrite == 0) continue;

                var ms = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                if (d.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Object)
                {
                    var created = Num(tm, "created");
                    if (created > 0) ms = created;
                }
                if (ms <= 0) continue;

                var reported = d.TryGetProperty("cost", out var cst) && cst.ValueKind == JsonValueKind.Number;
                var cost = reported ? cst.GetDouble() : Pricing.Estimate(modelId, input, output, cRead, cWrite);

                var projectId = r.IsDBNull(2) ? "" : r.GetString(2);
                list.Add(new UsageRecord
                {
                    TsUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                    Provider = "opencode",
                    Model = model,
                    SessionId = Str(d, "sessionID"),
                    Project = projects.GetValueOrDefault(projectId, ""),
                    Input = input,
                    Output = output,
                    CacheRead = cRead,
                    CacheWrite = cWrite,
                    Reasoning = reasoning,
                    Cost = cost,
                    CostIsReported = reported,
                });
            }
        }
        return list.OrderBy(x => x.TsUtc).ToList();
    }

    /// <summary>Fallback when message rows have been pruned but session totals survive.</summary>
    static List<UsageRecord> ReadSessions(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var projects = ProjectNames(c);
        var list = new List<UsageRecord>();

        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            select id, coalesce(model,'unknown'), cost, tokens_input, tokens_output,
                   tokens_reasoning, tokens_cache_read, tokens_cache_write,
                   coalesce(time_updated, time_created), coalesce(project_id,'')
            from session
            where tokens_input + tokens_output + tokens_cache_read + tokens_cache_write > 0
            order by time_created desc
            limit 5000
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ms = r.GetInt64(8);
            if (ms <= 0) continue;
            list.Add(new UsageRecord
            {
                TsUtc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                Provider = "opencode",
                Model = r.GetString(1),
                SessionId = r.GetString(0),
                Project = projects.GetValueOrDefault(r.GetString(9), ""),
                Input = r.GetInt64(3),
                Output = r.GetInt64(4),
                Reasoning = r.GetInt64(5),
                CacheRead = r.GetInt64(6),
                CacheWrite = r.GetInt64(7),
                Cost = r.GetDouble(2),
                CostIsReported = true,
            });
        }
        return list.OrderBy(x => x.TsUtc).ToList();
    }

    static Dictionary<string, string> ProjectNames(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "select id, coalesce(name, worktree) from project";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var raw = r.IsDBNull(1) ? "" : r.GetString(1);
                map[r.GetString(0)] = raw.Contains('/') || raw.Contains('\\')
                    ? Path.GetFileName(raw.TrimEnd('\\', '/'))
                    : raw;
            }
        }
        catch { }
        return map;
    }

    static string Provider(string model)
    {
        var i = model.IndexOf('/');
        return i > 0 ? model[..i] : model;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
}
