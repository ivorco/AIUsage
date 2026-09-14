using AIUsage.Core;

namespace AIUsage.UI;

/// <summary>Borderless flyout shown when the tray icon is clicked; hides when it loses focus.</summary>
sealed class UsagePopup : Form
{
    readonly UsageService service;
    PopupHitAreas hits = new();
    Point? hover;
    Point anchor;
    bool refreshing;
    DateTime hiddenAt;

    public event Action? SettingsRequested;
    public event Action? RefreshRequested;

    public UsagePopup(UsageService service)
    {
        this.service = service;
        Text = "AI Usage";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>True right after the popup closed because the tray icon was clicked, so that click doesn't reopen it.</summary>
    public bool RecentlyHidden => (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 400;

    float UiScale => DeviceDpi / 96f;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkChrome(Handle, rounded: true, border: Theme.Divider);
    }

    public void ShowNear(Point point)
    {
        anchor = point;
        Location = point; // create the window on the monitor that holds the tray
        if (!IsHandleCreated)
            CreateHandle();
        Size = PopupRenderer.Measure(service.Latest, UiScale);
        Place();
        Show();
        Activate();
    }

    public void UpdateData(bool isRefreshing)
    {
        refreshing = isRefreshing;
        if (!Visible)
            return;
        var size = PopupRenderer.Measure(service.Latest, UiScale);
        if (size != Size)
        {
            Size = size;
            Place();
        }
        Invalidate();
    }

    void Place()
    {
        var screen = Screen.FromPoint(anchor);
        var area = screen.WorkingArea;
        int margin = (int)(12 * UiScale);
        int y = area.Bottom < screen.Bounds.Bottom ? area.Bottom - Height - margin // taskbar at the bottom
            : area.Top > screen.Bounds.Top ? area.Top + margin                      // taskbar at the top
            : anchor.Y - Height - margin;
        int x = anchor.X - Width / 2;
        Location = new Point(
            Math.Clamp(x, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin)),
            Math.Clamp(y, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - Height - margin)));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnPaint(PaintEventArgs e) =>
        hits = PopupRenderer.Paint(e.Graphics, service.Latest, DateTimeOffset.Now, UiScale, new PopupState(refreshing, service.LastUpdated, hover));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool wasOverButton = hover is { } old && OverButton(old);
        hover = e.Location;
        bool isOverButton = OverButton(e.Location);
        Cursor = isOverButton ? Cursors.Hand : Cursors.Default;
        if (wasOverButton || isOverButton)
            Invalidate();
    }

    bool OverButton(Point p) => hits.Gear.Contains(p) || hits.Refresh.Contains(p);

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = null;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;
        if (hits.Gear.Contains(e.Location))
            SettingsRequested?.Invoke();
        else if (hits.Refresh.Contains(e.Location))
            RefreshRequested?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            HidePopup();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HidePopup();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Size = PopupRenderer.Measure(service.Latest, UiScale);
        Place();
        Invalidate();
    }

    void HidePopup()
    {
        hiddenAt = DateTime.UtcNow;
        Hide();
    }
}
