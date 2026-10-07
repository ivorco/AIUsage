using AIUsage.Providers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIUsage.UI;

/// <summary>
/// Sign-in window for claude.ai in an embedded Edge browser. Once claude.ai sets its session cookie the window
/// captures it, clears the browser's cookies and closes; the caller stores the key in Credential Manager.
/// </summary>
sealed class ClaudeLoginForm : Form
{
    readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer cookiePoll = new() { Interval = 1000 };
    string? sessionKey;
    bool checking;

    public static string? Acquire(IWin32Window owner)
    {
        using var form = new ClaudeLoginForm();
        return form.ShowDialog(owner) == DialogResult.OK ? form.sessionKey : null;
    }

    ClaudeLoginForm()
    {
        Text = "Sign in to Claude";
        Icon = TrayIconRenderer.AppIcon();
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(520, 760);
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        Controls.Add(browser);
        cookiePoll.Tick += async (_, _) => await CheckSignedInAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.UseDarkChrome(Handle);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, ClaudeWeb.UserDataFolder);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.CookieManager.DeleteAllCookies();
            browser.CoreWebView2.Navigate(ClaudeWeb.Origin + "/login");
            cookiePoll.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the claude.ai sign-in window: " + ex.Message, "AI Usage", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    async Task CheckSignedInAsync()
    {
        if (checking || sessionKey is not null || browser.CoreWebView2 is null)
            return;
        checking = true;
        try
        {
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync(ClaudeWeb.Origin);
            var value = cookies.FirstOrDefault(c => c.Name == "sessionKey")?.Value;
            if (string.IsNullOrEmpty(value))
                return;

            sessionKey = value;
            cookiePoll.Stop();
            browser.CoreWebView2.CookieManager.DeleteAllCookies();
            DialogResult = DialogResult.OK;
        }
        finally
        {
            checking = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            cookiePoll.Dispose();
        base.Dispose(disposing);
    }
}
