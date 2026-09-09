using System.Runtime.InteropServices;

namespace TokenMeter.UI;

/// <summary>
/// Keeps the tray icon present.
///
/// Windows broadcasts "TaskbarCreated" when Explorer starts, and every shell notification area icon
/// is wiped when Explorer restarts or crashes. An app that sets NotifyIcon.Visible once and forgets
/// disappears from the tray for the rest of the session. The same window is also why a launch that
/// beats the shell at logon can come up with no icon at all: the icon is simply re-added when the
/// taskbar announces itself.
/// </summary>
sealed partial class TrayIconHost : NativeWindow, IDisposable
{
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly Action _restore;

    public TrayIconHost(Action restore)
    {
        _restore = restore;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreated)
        {
            try { _restore(); } catch { /* never let a shell restart take the app down */ }
        }
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);
}
