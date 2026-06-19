using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AiUsageCounter;

// A single hidden WebView2 that owns one provider's cookie store (its user data
// folder). Used for both the login window and headless fetches, since WebView2
// locks a user data folder to one environment at a time — so login and fetch
// must share the same instance (and therefore the same cookies).
public sealed class WebViewHost : IDisposable
{
    private readonly Form _form;
    private readonly WebView2 _web;
    private readonly string _userDataFolder;
    private bool _initialized;
    private bool _disposing;
    private Action? _onLoginFormClosed;

    public WebViewHost(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
        _form = new Form
        {
            FormBorderStyle = FormBorderStyle.Sizable,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Width = 640,
            Height = 760,
            Visible = false,
        };
        _web = new WebView2 { Dock = DockStyle.Fill };
        _form.Controls.Add(_web);
        var icon = AppAssets.LoadAppIcon();
        if (icon is not null) _form.Icon = icon;

        // Closing the login window must not destroy the host — just hide it.
        _form.FormClosing += (_, e) =>
        {
            if (_disposing) return;
            e.Cancel = true;
            _form.Hide();
            _onLoginFormClosed?.Invoke();
        };
    }

    private async Task EnsureAsync()
    {
        if (_initialized) return;

        Directory.CreateDirectory(_userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);

        // Force the form handle + render pipeline by briefly showing it off-screen.
        _form.Location = new Point(-32000, -32000);
        _form.Show();
        await _web.EnsureCoreWebView2Async(env);
        _form.Hide();

        _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _initialized = true;
    }

    public string CurrentUrl => _initialized ? _web.CoreWebView2.Source ?? "" : "";

    public async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(string uri)
    {
        await EnsureAsync();
        return await _web.CoreWebView2.CookieManager.GetCookiesAsync(uri);
    }

    public async Task DeleteCookiesAsync(string domain)
    {
        await EnsureAsync();
        var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync($"https://{domain}");
        foreach (var c in cookies) _web.CoreWebView2.CookieManager.DeleteCookie(c);
    }

    // Show the login window, navigate to startUrl, and resolve true once
    // checkSignedIn() returns true (or false if the user closes the window first).
    public async Task<bool> ShowLoginAsync(string title, string startUrl, Func<Task<bool>> checkSignedIn)
    {
        await EnsureAsync();
        var tcs = new TaskCompletionSource<bool>();

        async void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (await checkSignedIn())
                {
                    await Task.Delay(1200); // let cookies persist to disk
                    tcs.TrySetResult(true);
                }
            }
            catch { /* keep waiting */ }
        }

        _onLoginFormClosed = () => tcs.TrySetResult(false);
        _web.CoreWebView2.NavigationCompleted += OnNav;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        _form.Location = new Point(area.X + (area.Width - _form.Width) / 2,
                                   area.Y + (area.Height - _form.Height) / 2);
        _form.Text = title;
        _form.Show();
        _form.Activate();
        _web.CoreWebView2.Navigate(startUrl);

        bool result = await tcs.Task;
        _web.CoreWebView2.NavigationCompleted -= OnNav;
        _onLoginFormClosed = null;
        _form.Hide();
        return result;
    }

    // Navigate (hidden) to pageUrl, then run an async JS body that calls post(...)
    // exactly once with a JSON string. Returns that string, or null on timeout.
    public async Task<string?> RunAsync(string pageUrl, string asyncBody,
        int navTimeoutMs = 30000, int settleMs = 1500, int msgTimeoutMs = 20000)
    {
        await EnsureAsync();
        await NavigateAndWaitAsync(pageUrl, navTimeoutMs);
        await Task.Delay(settleMs);

        var tcs = new TaskCompletionSource<string?>();
        void OnMsg(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { tcs.TrySetResult(e.TryGetWebMessageAsString()); }
            catch { tcs.TrySetResult(null); }
        }

        _web.CoreWebView2.WebMessageReceived += OnMsg;
        try
        {
            string wrapper =
                "(async () => { const post = s => window.chrome.webview.postMessage(s); try { "
                + asyncBody +
                " } catch (e) { post(JSON.stringify({ error: String(e) })); } })();";
            await _web.CoreWebView2.ExecuteScriptAsync(wrapper);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(msgTimeoutMs));
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            _web.CoreWebView2.WebMessageReceived -= OnMsg;
        }
    }

    private Task NavigateAndWaitAsync(string url, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _web.CoreWebView2.NavigationCompleted -= OnNav;
            tcs.TrySetResult(e.IsSuccess);
        }
        _web.CoreWebView2.NavigationCompleted += OnNav;
        _web.CoreWebView2.Navigate(url);
        return Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
    }

    public void Dispose()
    {
        _disposing = true;
        _web.Dispose();
        _form.Dispose();
    }
}
