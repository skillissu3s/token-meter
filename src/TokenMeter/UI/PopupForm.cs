namespace TokenMeter.UI;

/// <summary>The tray panel: borderless, always on top, and dismissed the moment it loses focus.</summary>
public sealed class PopupForm : Form
{
    public WebPanel Panel { get; }

    public PopupForm(WebPanel panel)
    {
        Panel = panel;
        Text = "Token Meter";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 20, 19);
        ClientSize = new Size(410, 560);
        MinimumSize = new Size(410, 240);
        KeyPreview = true;
        Controls.Add(panel.Control);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SquareCorners(Handle);
        Native.BorderColor(Handle, Color.FromArgb(48, 47, 44));
    }

    /// <summary>
    /// When the tray icon is clicked while the panel is open, Windows deactivates the panel before
    /// the click arrives. Without this the panel would hide and immediately reopen, so a click that
    /// lands right after a hide is treated as a dismissal.
    /// </summary>
    public DateTime LastHiddenUtc { get; private set; } = DateTime.MinValue;

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) LastHiddenUtc = DateTime.UtcNow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    /// <summary>Anchors the panel to the corner of the screen the tray lives in.</summary>
    public void ShowNearTray()
    {
        // The cursor is over the tray at this moment, which is the only reliable way to know which
        // screen the tray is on. A later resize must reuse that screen, not wherever the mouse went.
        _screen = Screen.FromPoint(Cursor.Position);
        AnchorToTray();
        Show();
        Activate();
        BringToFront();
    }

    const int Gap = 12;
    const int PreferredHeight = 560;
    Screen? _screen;

    void AnchorToTray()
    {
        var screen = _screen ??= Screen.FromPoint(Cursor.Position);
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;

        // The panel keeps one height whatever tool is selected; it only shrinks for a short screen.
        ClientSize = new Size(ClientSize.Width, Math.Min(PreferredHeight, work.Height - Gap * 2));

        // Whichever edge the taskbar occupies is the edge to hug.
        var x = work.Right - Width - Gap;
        var y = work.Bottom - Height - Gap;

        if (work.Bottom == bounds.Bottom && work.Top > bounds.Top) y = work.Top + Gap;   // taskbar on top
        if (work.Right == bounds.Right && work.Left > bounds.Left) x = work.Left + Gap;  // taskbar on left

        x = Math.Clamp(x, work.Left + Gap, Math.Max(work.Left + Gap, work.Right - Width - Gap));
        y = Math.Clamp(y, work.Top + Gap, Math.Max(work.Top + Gap, work.Bottom - Height - Gap));

        Location = new Point(x, y);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the panel should never end the app; only the tray menu does that.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
