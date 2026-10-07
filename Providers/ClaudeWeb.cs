using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsage.Core;
using Microsoft.Web.WebView2.Core;

namespace AIUsage.Providers;

/// <summary>
/// Calls claude.ai's own JSON API from an off-screen WebView2 (Edge) browser. claude.ai sits behind a
/// Cloudflare browser check that plain HTTP clients fail, while a real browser passes it. The session key is
/// injected as a non-persistent cookie for the duration of one call, and all cookies are cleared afterwards.
/// </summary>
static class ClaudeWeb
{
    public const string Origin = "https://claude.ai";
    static readonly TimeSpan BrowserCheckTimeout = TimeSpan.FromSeconds(30);

    public static string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage", "WebView2");

    public sealed record Response(int Status, JsonNode? Json);

    /// <summary>
    /// Opens a browser session on its own STA thread and runs <paramref name="work"/>, which receives a
    /// function that GETs a claude.ai URL within that session.
    /// </summary>
    public static Task<T> RunAsync<T>(string sessionKey, Func<Func<string, Task<Response>>, Task<T>> work, CancellationToken ct)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new HostForm();
            host.Load += async (_, _) =>
            {
                try
                {
                    result.TrySetResult(await RunSessionAsync(host, sessionKey, work, ct));
                }
                catch (Exception e)
                {
                    result.TrySetException(e);
                }
                finally
                {
                    host.Close();
                }
            };
            Application.Run(host);
        })
        {
            IsBackground = true,
            Name = "claude.ai browser",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    static async Task<T> RunSessionAsync<T>(Form host, string sessionKey, Func<Func<string, Task<Response>>, Task<T>> work, CancellationToken ct)
    {
        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new ProviderException("Claude usage needs the Microsoft Edge WebView2 Runtime.");
        }

        var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
        var web = controller.CoreWebView2;
        try
        {
            web.CookieManager.DeleteAllCookies();
            var cookie = web.CookieManager.CreateCookie("sessionKey", sessionKey, "claude.ai", "/");
            cookie.IsSecure = true;
            cookie.IsHttpOnly = true;
            web.CookieManager.AddOrUpdateCookie(cookie);

            await OpenOriginAsync(web, ct);
            return await work(url => FetchAsync(web, url));
        }
        finally
        {
            web.CookieManager.DeleteAllCookies();
            controller.Close();
        }
    }

    /// <summary>Navigates to a claude.ai page top-level so any Cloudflare check can run and complete.</summary>
    static async Task OpenOriginAsync(CoreWebView2 web, CancellationToken ct)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        web.NavigationCompleted += OnCompleted;
        var deadline = DateTime.UtcNow + BrowserCheckTimeout;
        try
        {
            web.Navigate(Origin + "/api/organizations");
            await loaded.Task.WaitAsync(BrowserCheckTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new ProviderException("claude.ai did not load. Check your connection.");
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }

        while (true)
        {
            var state = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.readyState + '|' + location.host + '|' + document.title")) ?? "";
            var parts = state.Split('|', 3);
            bool ready = parts is [ "complete", "claude.ai", var title ] && !title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
            if (ready)
                return;
            if (DateTime.UtcNow > deadline)
                throw new ProviderException("claude.ai's browser check did not complete. Retrying later.");
            await Task.Delay(500, ct);
        }
    }

    static async Task<Response> FetchAsync(CoreWebView2 web, string url)
    {
        var expression =
            "(async () => { const r = await fetch(" + JsonSerializer.Serialize(url) + ", { credentials: 'include', headers: { accept: 'application/json' } });" +
            " return JSON.stringify({ status: r.status, body: await r.text() }); })()";
        var parameters = new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true };
        var reply = JsonNode.Parse(await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters.ToJsonString()));
        if (reply.At("exceptionDetails") is not null || reply.At("result").At("value").Str() is not { } value)
            throw new ProviderException("claude.ai request failed inside the browser.");

        var payload = JsonNode.Parse(value);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(payload.At("body").Str() ?? "");
        }
        catch (JsonException)
        {
        }
        return new Response((int)(payload.At("status").Num() ?? 0), json);
    }

    /// <summary>Invisible, never-activated window that hosts the browser.</summary>
    sealed class HostForm : Form
    {
        public HostForm()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1024, 768);
            Opacity = 0;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }
    }
}
