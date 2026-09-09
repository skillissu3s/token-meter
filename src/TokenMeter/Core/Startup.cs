using System.Diagnostics;
using Microsoft.Win32;

namespace TokenMeter.Core;

/// <summary>
/// Start-with-Windows.
///
/// A scheduled logon task is the primary mechanism rather than the Run key. The Run key is skipped
/// in cases that look like a login to you but are not a fresh logon to Windows — notably resuming
/// with Fast Startup — and it races the shell, so an app that registers a tray icon before the
/// taskbar exists can come up invisible. A logon task fires in those cases, can wait a few seconds
/// for the shell to settle, and still runs on battery. Neither mechanism needs elevation.
///
/// The Run key is kept as a fallback for machines where task creation is blocked by policy. Both
/// being present is harmless: the single-instance mutex means the second launch exits immediately.
/// </summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "TokenMeter";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled() => TaskExists() || RunKeyExists();

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            // Prefer the task; only fall back to the Run key if it could not be created.
            if (CreateTask()) DeleteRunKey();
            else SetRunKey();
        }
        else
        {
            DeleteTask();
            DeleteRunKey();
        }
    }

    /// <summary>Where the launch log lives, so a startup that silently failed leaves a trace.</summary>
    public static string LogPath => Path.Combine(Settings.Dir, "startup.log");

    /// <summary>
    /// One line per launch. Without this there is no way to tell "Windows never started it" from
    /// "it started and then died", which is exactly the question you have when the tray is empty.
    /// </summary>
    public static void RecordLaunch(string how)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);

            // Keep it small: this file is a breadcrumb trail, not a journal.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64 * 1024)
            {
                var keep = File.ReadAllLines(LogPath).TakeLast(200);
                File.WriteAllLines(LogPath, keep);
            }

            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {how}  pid {Environment.ProcessId}{Environment.NewLine}");
        }
        catch { /* a log that cannot be written must never stop the app starting */ }
    }

    // ---------------------------------------------------------------- scheduled task

    const string TaskName = "TokenMeter";

    static bool TaskExists() => Schtasks("/Query /TN " + TaskName) == 0;

    static void DeleteTask() => Schtasks("/Delete /TN " + TaskName + " /F");

    static bool CreateTask()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

        var xmlPath = Path.Combine(Path.GetTempPath(), "TokenMeter-task-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        try
        {
            File.WriteAllText(xmlPath, TaskXml(exe), new System.Text.UnicodeEncoding(false, true));
            return Schtasks($"/Create /TN {TaskName} /XML \"{xmlPath}\" /F") == 0;
        }
        catch { return false; }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    static string TaskXml(string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts Token Meter, the AI token usage tray app, when you sign in.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{System.Security.SecurityElement.Escape(Environment.UserName)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.SecurityElement.Escape(Environment.UserName)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>"{exe}"</Command>
              <Arguments>--autostart</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.WaitForExit(15_000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    // ---------------------------------------------------------------- run key fallback

    static bool RunKeyExists()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string s && s.Contains("TokenMeter", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void SetRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            k?.SetValue(Name, "\"" + ExePath + "\" --autostart");
        }
        catch { }
    }

    static void DeleteRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            k?.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { }
    }
}
