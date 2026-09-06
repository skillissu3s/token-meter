namespace TokenMeter.UI;

/// <summary>The master view: every provider side by side, plus the combined charts.</summary>
public sealed class DashboardForm : Form
{
    public WebPanel Panel { get; }

    public DashboardForm(WebPanel panel, Icon icon)
    {
        Panel = panel;
        Text = "Token Meter";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 19);
        ClientSize = new Size(1180, 800);
        MinimumSize = new Size(880, 620);
        Controls.Add(panel.Control);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
        Native.SquareCorners(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void ShowFront()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }
}
