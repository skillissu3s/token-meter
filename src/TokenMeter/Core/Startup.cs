using Microsoft.Win32;

namespace TokenMeter.Core;

/// <summary>Start-with-Windows via the per-user Run key, which needs no elevation.</summary>
public static class Startup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TokenMeter";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string s && s.Contains("TokenMeter", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key, writable: true);
            if (k is null) return;
            if (enabled) k.SetValue(Name, "\"" + ExePath + "\"");
            else k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { }
    }
}
