using TokenMeter.Core;

namespace TokenMeter.Collectors;

/// <summary>
/// Transcript files are append-only and there can be a lot of them, so parsed results are kept
/// per file and only recomputed when the file size or write time changes.
/// </summary>
public sealed class JsonlCache
{
    readonly record struct Stamp(long Length, long Ticks);
    readonly Dictionary<string, (Stamp Stamp, List<UsageRecord> Records)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public List<UsageRecord> Get(FileInfo file, Func<FileInfo, List<UsageRecord>> parse)
    {
        var stamp = new Stamp(file.Length, file.LastWriteTimeUtc.Ticks);
        if (_cache.TryGetValue(file.FullName, out var hit) && hit.Stamp == stamp)
            return hit.Records;

        List<UsageRecord> records;
        try { records = parse(file); }
        catch { records = []; }
        _cache[file.FullName] = (stamp, records);
        return records;
    }

    /// <summary>Drops entries for files that no longer exist so the cache cannot grow without bound.</summary>
    public void Forget(IEnumerable<string> keepPaths)
    {
        var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
            _cache.Remove(gone);
    }
}
