using System.Text.Json;
using System.Text.Json.Serialization;
using TokenMeter.Collectors;

namespace TokenMeter.Core;

/// <summary>
/// Runs every collector and rolls their snapshots up into the master view.
/// Collection happens off the UI thread; the result is cached so opening the popup is instant.
/// </summary>
public sealed class UsageService
{
    readonly IUsageCollector[] _collectors =
    [
        new ClaudeCodeCollector(),
        new CodexCollector(),
        new OpenCodeCollector(),
        new AntigravityCollector(),
    ];

    readonly SemaphoreSlim _gate = new(1, 1);
    Snapshot _last = new() { GeneratedAt = DateTime.UtcNow.ToString("o") };
    string _lastJson = "{}";

    public Snapshot Latest => _last;
    public string LatestJson => _lastJson;
    public event Action<Snapshot>? Updated;

    public async Task<Snapshot> RefreshAsync(Settings settings)
    {
        await _gate.WaitAsync();
        try
        {
            var snap = await Task.Run(() => Build(settings));
            _last = snap;
            _lastJson = JsonSerializer.Serialize(snap, JsonOpts);
            Updated?.Invoke(snap);
            return snap;
        }
        finally { _gate.Release(); }
    }

    Snapshot Build(Settings settings)
    {
        var snap = new Snapshot { GeneratedAt = DateTime.UtcNow.ToString("o") };

        foreach (var collector in _collectors)
        {
            ProviderSnapshot p;
            try { p = collector.Collect(settings); }
            catch (Exception ex)
            {
                p = new ProviderSnapshot
                {
                    Id = collector.Id,
                    Name = collector.Name,
                    Accent = collector.Accent,
                    State = ProviderState.Error,
                    Note = ex.Message,
                };
            }
            snap.Providers.Add(p);
        }

        var live = snap.Providers.Where(p => p.State == ProviderState.Ok).ToList();
        snap.TokensToday = live.Sum(p => p.TokensToday);
        snap.Tokens7d = live.Sum(p => p.Tokens7d);
        snap.TokensTotal = live.Sum(p => p.TokensTotal);
        snap.CostToday = live.Sum(p => p.CostToday);
        snap.Cost7d = live.Sum(p => p.Cost7d);
        snap.CostTotal = live.Sum(p => p.CostTotal);

        // Master daily chart: the per-provider series all cover the same 14 local days, so index-align them.
        var days = live.FirstOrDefault()?.Daily.Count ?? 0;
        for (var i = 0; i < days; i++)
        {
            var first = live[0].Daily[i];
            snap.Daily.Add(new Bucket
            {
                T = first.T,
                Label = first.Label,
                Tokens = live.Sum(p => i < p.Daily.Count ? p.Daily[i].Tokens : 0),
                Cost = live.Sum(p => i < p.Daily.Count ? p.Daily[i].Cost : 0),
            });
        }

        snap.Models.AddRange(live
            .SelectMany(p => p.Models.Select(m => (p.Name, m)))
            .GroupBy(x => x.Name + " · " + x.m.Model)
            .Select(g => new ModelSlice
            {
                Model = g.Key,
                Tokens = g.Sum(x => x.m.Tokens),
                Cost = g.Sum(x => x.m.Cost),
                Calls = g.Sum(x => x.m.Calls),
            })
            .OrderByDescending(m => m.Tokens)
            .Take(12));

        var top = live.SelectMany(p => p.Gauges.Select(g => (p.Name, g)))
                      .OrderByDescending(x => x.g.Percent)
                      .FirstOrDefault();
        if (top.g is not null)
        {
            snap.Pressure = top.g.Percent;
            snap.PressureLabel = top.Name + " · " + top.g.Label;
        }

        return snap;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
