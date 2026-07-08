using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AiUsageCounter;

// A short-lived hidden WebView2 for one provider's cookie store (its user data
// folder). Cookies persist in the folder, while renderer processes are released
// after login checks and headless fetches to keep tray-app memory low.
public sealed class WebViewHost : IDisposable
{
    private Form? _form;
    private WebView2? _web;
    private readonly string _userDataFolder;
    private bool _initialized;
    private bool _disposing;
    private Action? _onLoginFormClosed;

    public WebViewHost(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
    }

    private void CreateControls()
    {
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

    private Form Form => _form ?? throw new ObjectDisposedException(nameof(WebViewHost));
    private WebView2 Web => _web ?? throw new ObjectDisposedException(nameof(WebViewHost));

    private bool IsVisible => _form is { IsDisposed: false, Visible: true };

    private void ReleaseIdleWebView()
    {
        if (_disposing || IsVisible) return;

        _initialized = false;
        _web?.Dispose();
        _form?.Dispose();
        _web = null;
        _form = null;
    }

    private async Task EnsureAsync()
    {
        if (_initialized) return;
        if (_form is null || _form.IsDisposed || _web is null || _web.IsDisposed)
            CreateControls();

        var form = Form;
        var web = Web;
        Directory.CreateDirectory(_userDataFolder);
        var env = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);

        // Force the form handle + render pipeline by briefly showing it off-screen.
        form.Location = new Point(-32000, -32000);
        form.Show();
        await web.EnsureCoreWebView2Async(env);
        form.Hide();

        web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _initialized = true;
    }

    public string CurrentUrl => _initialized && _web?.CoreWebView2 is not null ? _web.CoreWebView2.Source ?? "" : "";

    public async Task<IReadOnlyList<CoreWebView2Cookie>> GetCookiesAsync(string uri)
    {
        await EnsureAsync();
        try
        {
            return (await Web.CoreWebView2.CookieManager.GetCookiesAsync(uri)).ToList();
        }
        finally
        {
            ReleaseIdleWebView();
        }
    }

    public async Task DeleteCookiesAsync(string domain)
    {
        await EnsureAsync();
        try
        {
            var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync($"https://{domain}");
            foreach (var c in cookies) Web.CoreWebView2.CookieManager.DeleteCookie(c);
        }
        finally
        {
            ReleaseIdleWebView();
        }
    }

    // Show the login window, navigate to startUrl, and resolve true once
    // checkSignedIn() returns true (or false if the user closes the window first).
    public async Task<bool> ShowLoginAsync(string title, string startUrl, Func<Task<bool>> checkSignedIn)
    {
        await EnsureAsync();
        var form = Form;
        var web = Web;
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
        web.CoreWebView2.NavigationCompleted += OnNav;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        form.Location = new Point(area.X + (area.Width - form.Width) / 2,
                                  area.Y + (area.Height - form.Height) / 2);
        form.Text = title;
        form.Show();
        form.Activate();
        web.CoreWebView2.Navigate(startUrl);

        bool result = await tcs.Task;
        web.CoreWebView2.NavigationCompleted -= OnNav;
        _onLoginFormClosed = null;
        form.Hide();
        ReleaseIdleWebView();
        return result;
    }

    // Navigate (hidden) to pageUrl, then run an async JS body that calls post(...)
    // exactly once with a JSON string. Returns that string, or null on timeout.
    public async Task<string?> RunAsync(string pageUrl, string asyncBody,
        int navTimeoutMs = 30000, int settleMs = 1500, int msgTimeoutMs = 20000)
    {
        await EnsureAsync();
        var web = Web;
        await NavigateAndWaitAsync(pageUrl, navTimeoutMs);
        await Task.Delay(settleMs);

        var tcs = new TaskCompletionSource<string?>();
        void OnMsg(object? s, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { tcs.TrySetResult(e.TryGetWebMessageAsString()); }
            catch { tcs.TrySetResult(null); }
        }

        web.CoreWebView2.WebMessageReceived += OnMsg;
        try
        {
            string wrapper =
                "(async () => { const post = s => window.chrome.webview.postMessage(s); try { "
                + asyncBody +
                " } catch (e) { post(JSON.stringify({ error: String(e) })); } })();";
            await web.CoreWebView2.ExecuteScriptAsync(wrapper);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(msgTimeoutMs));
            return done == tcs.Task ? await tcs.Task : null;
        }
        finally
        {
            web.CoreWebView2.WebMessageReceived -= OnMsg;
            ReleaseIdleWebView();
        }
    }

    private async Task NavigateAndWaitAsync(string url, int timeoutMs)
    {
        var web = Web;
        var tcs = new TaskCompletionSource<bool>();
        void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            tcs.TrySetResult(e.IsSuccess);
        }
        web.CoreWebView2.NavigationCompleted += OnNav;
        try
        {
            web.CoreWebView2.Navigate(url);
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }
        finally
        {
            web.CoreWebView2.NavigationCompleted -= OnNav;
        }
    }

    public void Dispose()
    {
        _disposing = true;
        _initialized = false;
        _web?.Dispose();
        _form?.Dispose();
        _web = null;
        _form = null;
    }
}
