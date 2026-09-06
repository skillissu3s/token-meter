using Microsoft.Data.Sqlite;

namespace TokenMeter.Collectors;

/// <summary>
/// Opens a provider database read-only. If the owning app holds it in a way we cannot read,
/// the database and its WAL sidecars are copied to temp and read from there, so a running
/// Codex or OpenCode never blocks a refresh and is never written to.
/// </summary>
public static class SqliteReader
{
    public static T? Read<T>(string path, Func<SqliteConnection, T> body) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            c.Open();
            return body(c);
        }
        catch
        {
            var copy = CopyAside(path);
            if (copy is null) return null;
            try
            {
                using var c = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
                c.Open();
                return body(c);
            }
            catch { return null; }
        }
    }

    static string? CopyAside(string path)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "TokenMeter");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, Path.GetFileName(path));
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var src = path + suffix;
                if (File.Exists(src)) File.Copy(src, target + suffix, overwrite: true);
            }
            return target;
        }
        catch { return null; }
    }
}
