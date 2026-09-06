namespace TokenMeter.Core;

/// <summary>
/// Where each tool keeps its data. Every location can be pointed somewhere else with an
/// environment variable, which is how the collectors are tested against fixtures and how you
/// can aim Token Meter at a data folder that lives off the default path.
/// </summary>
public static class Paths
{
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    public static string ClaudeRoot => Env("TOKENMETER_CLAUDE_DIR", Path.Combine(Home, ".claude"));
    public static string CodexRoot => Env("TOKENMETER_CODEX_DIR", Path.Combine(Home, ".codex"));

    public static string OpenCodeDb => Env("TOKENMETER_OPENCODE_DB",
        Path.Combine(Home, ".local", "share", "opencode", "opencode.db"));

    public static string[] AntigravityRoots
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("TOKENMETER_ANTIGRAVITY_DIR");
            if (!string.IsNullOrWhiteSpace(custom)) return [custom];
            return
            [
                Path.Combine(Home, ".antigravity"),
                Path.Combine(AppData, "Antigravity"),
                Path.Combine(LocalAppData, "Antigravity"),
                Path.Combine(LocalAppData, "Programs", "Antigravity"),
                Path.Combine(Home, ".codeium", "antigravity"),
            ];
        }
    }
}
