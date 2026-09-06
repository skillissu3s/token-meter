namespace TokenMeter.Core;

/// <summary>Turns a flat list of usage records into the windows, buckets and slices the UI draws.</summary>
public static class Aggregate
{
    public static long Sum(IEnumerable<UsageRecord> rs, bool countCacheReads) =>
        rs.Sum(r => countCacheReads ? r.Total : r.Input + r.Output + r.CacheWrite);

    public static IEnumerable<UsageRecord> Since(IReadOnlyList<UsageRecord> rs, DateTime utc) =>
        rs.Where(r => r.TsUtc >= utc);

    public static IEnumerable<UsageRecord> Today(IReadOnlyList<UsageRecord> rs) =>
        Since(rs, DateTime.Now.Date.ToUniversalTime());

    /// <summary>
    /// Claude-style rolling window: the block starts at the first call that is not already
    /// inside a previous block, so the countdown matches what the provider actually enforces.
    /// </summary>
    public static (DateTime Start, DateTime End) CurrentBlock(IReadOnlyList<UsageRecord> rs, TimeSpan length)
    {
        var now = DateTime.UtcNow;
        var start = now;
        // Walk forward through history, opening a new block whenever the previous one has expired.
        DateTime? blockStart = null;
        foreach (var r in rs.OrderBy(r => r.TsUtc))
        {
            if (blockStart is null || r.TsUtc >= blockStart + length)
                blockStart = FloorHour(r.TsUtc);
        }
        if (blockStart is not null && now < blockStart + length) start = blockStart.Value;
        else start = FloorHour(now);
        return (start, start + length);
    }

    static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    public static List<ModelSlice> Models(IEnumerable<UsageRecord> rs, bool countCacheReads) =>
        rs.GroupBy(r => r.Model)
          .Select(g => new ModelSlice
          {
              Model = g.Key,
              Tokens = Sum(g, countCacheReads),
              Cost = g.Sum(r => r.Cost),
              Calls = g.Count(),
          })
          .Where(s => s.Tokens > 0 || s.Cost > 0)
          .OrderByDescending(s => s.Tokens)
          .ToList();

    public static List<Bucket> Daily(IReadOnlyList<UsageRecord> rs, int days, bool countCacheReads)
    {
        var today = DateTime.Now.Date;
        var byDay = rs.GroupBy(r => r.TsUtc.ToLocalTime().Date)
                      .ToDictionary(g => g.Key, g => (Tokens: Sum(g, countCacheReads), Cost: g.Sum(r => r.Cost)));
        var list = new List<Bucket>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            byDay.TryGetValue(d, out var v);
            list.Add(new Bucket
            {
                T = d.ToString("yyyy-MM-dd"),
                Label = d.ToString("ddd d"),
                Tokens = v.Tokens,
                Cost = v.Cost,
            });
        }
        return list;
    }

    public static List<Bucket> Hourly(IReadOnlyList<UsageRecord> rs, int hours, bool countCacheReads)
    {
        var now = DateTime.Now;
        var top = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        var byHour = rs.GroupBy(r => { var l = r.TsUtc.ToLocalTime(); return new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0); })
                       .ToDictionary(g => g.Key, g => (Tokens: Sum(g, countCacheReads), Cost: g.Sum(r => r.Cost)));
        var list = new List<Bucket>(hours);
        for (var i = hours - 1; i >= 0; i--)
        {
            var h = top.AddHours(-i);
            byHour.TryGetValue(h, out var v);
            list.Add(new Bucket { T = h.ToString("s"), Label = h.ToString("HH:00"), Tokens = v.Tokens, Cost = v.Cost });
        }
        return list;
    }

    /// <summary>Fills the fields every provider tab shares, so collectors only add what is specific to them.</summary>
    public static void FillCommon(ProviderSnapshot snap, IReadOnlyList<UsageRecord> rs, Settings s)
    {
        var cache = s.CountCacheReads;
        var week = DateTime.UtcNow.AddDays(-7);

        snap.TokensToday = Sum(Today(rs), cache);
        snap.Tokens7d = Sum(Since(rs, week), cache);
        snap.TokensTotal = Sum(rs, cache);
        snap.CostToday = Today(rs).Sum(r => r.Cost);
        snap.Cost7d = Since(rs, week).Sum(r => r.Cost);
        snap.CostTotal = rs.Sum(r => r.Cost);
        snap.CostEstimated = rs.Count > 0 && !rs.Any(r => r.CostIsReported);
        snap.LastActivityUtc = rs.Count > 0 ? rs.Max(r => r.TsUtc) : null;

        snap.Models.AddRange(Models(Since(rs, week), cache));
        snap.Daily.AddRange(Daily(rs, 14, cache));
        snap.Hourly.AddRange(Hourly(rs, 24, cache));
    }
}
