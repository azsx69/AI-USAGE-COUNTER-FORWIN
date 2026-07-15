using System.Text.Json;

namespace AiUsageCounter;

// Codex (ChatGPT) usage via the internal API, run inside the logged-in
// chatgpt.com page context:
//   GET /api/auth/session                 -> { accessToken }   (cookie auth)
//   GET /backend-api/wham/usage  (Bearer) -> rate_limit windows (Codex is now
//     weekly-only; the 5h session window was dropped).
// Falls back to scraping chatgpt.com/codex/settings/usage.
public sealed class CodexProvider : IUsageProvider
{
    private readonly WebViewHost _host;
    private const string LoginVerifiedKey = "codexLoginVerified";

    public string Id => "codex";
    public string DisplayName => "Codex";

    public CodexProvider()
    {
        string udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiUsageCounter", "WebView2", "codex");
        _host = new WebViewHost(udf);
    }

    public async Task<AuthState> CheckAuthAsync()
    {
        // next-auth session cookie (sometimes chunked .0/.1) marks a real login;
        // chatgpt.com sets plenty of cookies even for anonymous visitors.
        var cookies = await _host.GetCookiesAsync("https://chatgpt.com");
        if (cookies.Any(c => c.Name.Contains("session-token") && !string.IsNullOrEmpty(c.Value)))
            return AuthState.SignedIn;

        // Cookie names shift across OpenAI auth revisions — if the login window
        // ever verified a token, trust that until a fetch says otherwise.
        if (AppState.GetBool(LoginVerifiedKey) && cookies.Count > 0)
            return AuthState.SignedIn;

        return AuthState.SignedOut;
    }

    public Task<bool> PresentLoginAsync()
        => _host.ShowLoginAsync("Sign in to ChatGPT", "https://chatgpt.com/auth/login", async () =>
        {
            // Cookie-only check (no navigation, so it never fights the login flow).
            // The next-auth session-token cookie is only set once actually signed in.
            var cookies = await _host.GetCookiesAsync("https://chatgpt.com");
            bool ok = cookies.Any(c => c.Name.Contains("session-token") && !string.IsNullOrEmpty(c.Value));
            if (ok) AppState.SetBool(LoginVerifiedKey, true);
            return ok;
        });

    public async Task SignOutAsync()
    {
        AppState.SetBool(LoginVerifiedKey, false);
        await _host.DeleteCookiesAsync("chatgpt.com");
    }

    public async Task<FetchResult> FetchUsageAsync()
    {
        var viaApi = await FetchViaInternalApiAsync();
        if (viaApi.Kind != FetchKind.Failure) return viaApi;
        return await FetchViaUsagePageAsync();
    }

    private async Task<FetchResult> FetchViaInternalApiAsync()
    {
        const string body = """
            const sr = await fetch('/api/auth/session', { credentials: 'include' });
            if (!sr.ok) { post(JSON.stringify({ error: 'auth' })); return; }
            const sj = await sr.json();
            const token = sj && sj.accessToken;
            if (!token) { post(JSON.stringify({ error: 'auth' })); return; }
            const r = await fetch('https://chatgpt.com/backend-api/wham/usage', {
                credentials: 'include',
                headers: { 'Authorization': 'Bearer ' + token, 'Accept': 'application/json' }
            });
            if (r.status === 401 || r.status === 403) { post(JSON.stringify({ error: 'auth' })); return; }
            if (!r.ok) { post(JSON.stringify({ error: 'http_' + r.status })); return; }
            post(JSON.stringify({ data: await r.json() }));
            """;

        string? raw = await _host.RunAsync("https://chatgpt.com/", body);
        if (raw == null) return FetchResult.Failure;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetString() == "auth")
                return FetchResult.AuthExpired;
            if (!root.TryGetProperty("data", out var data)) return FetchResult.Failure;

            var usage = ParseWhamUsage(data);
            return usage != null ? FetchResult.Success(usage) : FetchResult.Failure;
        }
        catch { return FetchResult.Failure; }
    }

    // Tolerant parse — field names have shifted across versions.
    private static ProviderUsage? ParseWhamUsage(JsonElement root)
    {
        var rl = JsonHelpers.Prop(root, "rate_limit", "rate_limits") ?? root;
        var w1 = ParseWindow(JsonHelpers.Prop(rl, "primary_window", "primary"));
        var w2 = ParseWindow(JsonHelpers.Prop(rl, "secondary_window", "secondary"));

        // Codex dropped the 5h session window; only a weekly window remains, and
        // it can arrive in either slot. Route each window by its length instead
        // of by position: short (<=6h) -> session, long -> weekly.
        var u = new ProviderUsage { FetchedAt = DateTime.Now };
        foreach (var w in new[] { w1, w2 })
        {
            if (w.pct == null && w.reset == null) continue;
            if (IsWeeklyWindow(w)) { u.WeeklyPct = w.pct; u.WeeklyResetAt = w.reset; }
            else { u.SessionPct = w.pct; u.SessionResetAt = w.reset; }
        }

        if (u.SessionPct == null && u.WeeklyPct == null) return null;
        return u;
    }

    // A window is "weekly" when its length is more than a session (5h). Prefer the
    // reported window duration; otherwise infer from how far out the reset is.
    private static bool IsWeeklyWindow((double? pct, DateTime? reset, double? dur) w)
    {
        if (w.dur is { } d) return d > 6 * 3600;
        if (w.reset is { } r) return (r - DateTime.Now).TotalHours > 6;
        return false;
    }

    private static (double? pct, DateTime? reset, double? dur) ParseWindow(JsonElement? any)
    {
        if (any is not { } d || d.ValueKind != JsonValueKind.Object) return (null, null, null);

        double? pct = null;
        if (JsonHelpers.Prop(d, "used_percent", "usage_percent", "used_percentage") is { } pe)
            pct = JsonHelpers.Pct(pe);

        DateTime? reset = null;
        if (JsonHelpers.Prop(d, "resets_in_seconds", "reset_after_seconds", "resets_after_seconds") is { } se
            && JsonHelpers.Num(se) is { } secs)
            reset = DateTime.Now.AddSeconds(secs);
        else if (JsonHelpers.Prop(d, "resets_at", "reset_at") is { } ae)
            reset = JsonHelpers.Date(ae);

        double? dur = null;
        if (JsonHelpers.Prop(d, "limit_window_seconds", "window_seconds") is { } we)
            dur = JsonHelpers.Num(we);
        if (dur == null && JsonHelpers.Prop(d, "window_minutes") is { } me && JsonHelpers.Num(me) is { } mins)
            dur = mins * 60;

        return (pct, reset, dur);
    }

    // Fallback: scrape the usage settings page meters.
    private async Task<FetchResult> FetchViaUsagePageAsync()
    {
        const string body = """
            if (location.host.indexOf('chatgpt.com') < 0 || location.pathname.indexOf('/auth') >= 0) {
                post(JSON.stringify({ error: 'auth' })); return;
            }
            for (let i = 0; i < 8; i++) {
                const text = (document.body.innerText || '').replace(/ /g, ' ');
                const near = (re) => {
                    const idx = text.search(re);
                    if (idx < 0) return null;
                    const seg = text.slice(idx, idx + 260);
                    const m = seg.match(/(\d+(?:\.\d+)?)\s*%/);
                    if (!m) return null;
                    const around = seg.slice(Math.max(0, m.index - 40), m.index + 40);
                    return { pct: parseFloat(m[1]), remaining: /left|remain/i.test(around) };
                };
                const session = near(/5[\s-]?hour/i);
                const weekly = near(/week/i);
                if (session || weekly) { post(JSON.stringify({ data: { session, weekly } })); return; }
                await new Promise(r => setTimeout(r, 1000));
            }
            post(JSON.stringify({ error: 'notfound' }));
            """;

        string? raw = await _host.RunAsync("https://chatgpt.com/codex/settings/usage", body, settleMs: 1500);
        if (raw == null) return FetchResult.Failure;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetString() == "auth")
                return FetchResult.AuthExpired;
            if (!root.TryGetProperty("data", out var data)) return FetchResult.Failure;

            double? UsedPct(string key)
            {
                if (data.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("pct", out var pe) && JsonHelpers.Num(pe) is { } pct)
                {
                    bool remaining = el.TryGetProperty("remaining", out var re) && re.ValueKind == JsonValueKind.True;
                    return remaining ? Math.Max(0, 100 - pct) : pct;
                }
                return null;
            }

            var u = new ProviderUsage { FetchedAt = DateTime.Now, SessionPct = UsedPct("session"), WeeklyPct = UsedPct("weekly") };
            if (u.SessionPct == null && u.WeeklyPct == null) return FetchResult.Failure;
            return FetchResult.Success(u);
        }
        catch { return FetchResult.Failure; }
    }
}
