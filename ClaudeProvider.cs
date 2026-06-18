using System.Text.Json;

namespace AiUsageCounter;

// Claude usage via the internal claude.ai JSON API, called from inside the
// logged-in page context (fetch with the page's cookies). This mirrors the
// macOS app's WebView fallback path, which is the most robust against Cloudflare.
public sealed class ClaudeProvider : IUsageProvider
{
    private readonly WebViewHost _host;

    public string Id => "claude";
    public string DisplayName => "Claude";

    public ClaudeProvider()
    {
        string udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiUsageCounter", "WebView2", "claude");
        _host = new WebViewHost(udf);
    }

    public async Task<AuthState> CheckAuthAsync()
    {
        var cookies = await _host.GetCookiesAsync("https://claude.ai");
        bool signedIn = cookies.Any(c => c.Name == "sessionKey" && !string.IsNullOrEmpty(c.Value));
        return signedIn ? AuthState.SignedIn : AuthState.SignedOut;
    }

    public Task<bool> PresentLoginAsync()
        => _host.ShowLoginAsync("Sign in to Claude", "https://claude.ai/login", async () =>
        {
            var c = await _host.GetCookiesAsync("https://claude.ai");
            return c.Any(x => x.Name == "sessionKey" && !string.IsNullOrEmpty(x.Value));
        });

    public Task SignOutAsync() => _host.DeleteCookiesAsync("claude.ai");

    public async Task<FetchResult> FetchUsageAsync()
    {
        const string body = """
            const headers = { 'Accept': 'application/json' };
            const orgRes = await fetch('/api/organizations', { credentials: 'include', headers });
            if (orgRes.status === 401 || orgRes.status === 403) { post(JSON.stringify({ error: 'auth' })); return; }
            if (!orgRes.ok) { post(JSON.stringify({ error: 'http_' + orgRes.status })); return; }
            const orgs = await orgRes.json();
            const org = Array.isArray(orgs) && orgs.length
                ? ((orgs.find(o => (o.capabilities || []).includes('chat')) || orgs[0]).uuid)
                : null;
            if (!org) { post(JSON.stringify({ error: 'noorg' })); return; }
            const r = await fetch('/api/organizations/' + org + '/usage', { credentials: 'include', headers });
            if (r.status === 401 || r.status === 403) { post(JSON.stringify({ error: 'auth' })); return; }
            if (!r.ok) { post(JSON.stringify({ error: 'http_' + r.status })); return; }
            post(JSON.stringify({ data: await r.json() }));
            """;

        string? raw = await _host.RunAsync("https://claude.ai/", body);
        if (raw == null) return FetchResult.Failure;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetString() == "auth")
                return FetchResult.AuthExpired;
            if (!root.TryGetProperty("data", out var data))
                return FetchResult.Failure;

            var usage = ParseUsage(data);
            return usage != null ? FetchResult.Success(usage) : FetchResult.Failure;
        }
        catch
        {
            return FetchResult.Failure;
        }
    }

    private static ProviderUsage? ParseUsage(JsonElement obj)
    {
        var u = new ProviderUsage { FetchedAt = DateTime.Now };

        if (obj.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
        {
            if (fh.TryGetProperty("utilization", out var ut)) u.SessionPct = JsonHelpers.Pct(ut);
            if (fh.TryGetProperty("resets_at", out var ra)) u.SessionResetAt = JsonHelpers.Date(ra);
        }
        if (obj.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
        {
            if (sd.TryGetProperty("utilization", out var ut)) u.WeeklyPct = JsonHelpers.Pct(ut);
            if (sd.TryGetProperty("resets_at", out var ra)) u.WeeklyResetAt = JsonHelpers.Date(ra);
        }

        if (u.SessionPct == null && u.WeeklyPct == null) return null;
        return u;
    }
}

internal static class JsonHelpers
{
    // First present property among candidate names (tolerant of field renames).
    public static JsonElement? Prop(JsonElement o, params string[] names)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
            if (o.TryGetProperty(n, out var v)) return v;
        return null;
    }

    public static double? Num(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.String => double.TryParse(el.GetString(), out var d) ? d : null,
        _ => null,
    };

    // Normalize a utilization value that may be 0-1 fractional or 0-100 percent.
    public static double? Pct(JsonElement el)
    {
        var n = Num(el);
        if (n == null) return null;
        double v = n.Value;
        if (v <= 1.0 && v != Math.Round(v)) return v * 100;
        return v;
    }

    public static DateTime? Date(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (DateTimeOffset.TryParse(s, out var dto)) return dto.LocalDateTime;
            return null;
        }
        var num = Num(el);
        if (num == null) return null;
        double v = num.Value;
        return DateTimeOffset.FromUnixTimeMilliseconds(
            (long)(v > 10_000_000_000 ? v : v * 1000)).LocalDateTime;
    }
}
