using AIUsage.Core;

namespace AIUsage.UI;

sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    readonly UsageService service = new(ProviderRegistry.Create());
    readonly NotifyIcon tray;
    readonly UsagePopup popup;
    readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = (int)RefreshInterval.TotalMilliseconds };
    readonly System.Windows.Forms.Timer paceTimer = new() { Interval = 60_000 }; // expected pace moves with the clock
    SettingsForm? settings;
    Icon? currentIcon;
    bool refreshing;

    public TrayApp()
    {
        popup = new UsagePopup(service);
        popup.SettingsRequested += OpenSettings;
        popup.RefreshRequested += () => _ = RefreshAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show usage", null, (_, _) => ShowPopup());
        menu.Items.Add("Refresh now", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon { ContextMenuStrip = menu, Text = "AI Usage" };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                TogglePopup();
        };
        UpdateIcon();
        tray.Visible = true;

        refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        refreshTimer.Start();
        paceTimer.Tick += (_, _) =>
        {
            UpdateIcon();
            popup.UpdateData(refreshing);
        };
        paceTimer.Start();

        _ = RefreshAsync();
    }

    void TogglePopup()
    {
        if (popup.Visible)
            popup.Hide();
        else if (!popup.RecentlyHidden)
            ShowPopup();
    }

    void ShowPopup()
    {
        popup.ShowNear(Cursor.Position);
        if (service.LastUpdated is { } last && DateTimeOffset.Now - last > TimeSpan.FromMinutes(2))
            _ = RefreshAsync();
    }

    async Task RefreshAsync()
    {
        if (refreshing)
            return;
        refreshing = true;
        popup.UpdateData(true);
        try
        {
            await service.RefreshAsync(CancellationToken.None);
        }
        finally
        {
            refreshing = false;
            UpdateIcon();
            popup.UpdateData(false);
        }
    }

    void UpdateIcon()
    {
        var now = DateTimeOffset.Now;
        using var bitmap = TrayIconRenderer.Render(service.Latest.Select(s => s.TrayFill(now)).ToList(), SystemInformation.SmallIconSize.Width);
        var icon = TrayIconRenderer.ToIcon(bitmap);
        tray.Icon = icon;
        currentIcon?.Dispose();
        currentIcon = icon;

        var lines = service.Latest.Select(s =>
            s.IsLoading ? $"{s.DisplayName}: loading"
            : s.Meters.Count == 0 && s.Error is not null ? $"{s.DisplayName}: error"
            : s.PaceRatio(now) is double ratio ? $"{s.DisplayName}: {ratio:0.0}× pace"
            : $"{s.DisplayName}: —");
        var text = string.Join("\n", lines);
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    void OpenSettings()
    {
        if (settings is { IsDisposed: false })
        {
            settings.Activate();
            return;
        }
        settings = new SettingsForm(service.Providers);
        settings.FormClosed += (sender, e) =>
        {
            if (((Form)sender!).DialogResult == DialogResult.OK)
                _ = RefreshAsync();
            settings = null;
        };
        settings.Show();
        settings.Activate();
    }

    protected override void ExitThreadCore()
    {
        refreshTimer.Dispose();
        paceTimer.Dispose();
        tray.Visible = false;
        tray.Dispose();
        settings?.Close();
        popup.Dispose();
        currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
