using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiUsageCounter;

// Gemini usage via DOM scraping of gemini.google.com's Usage Limits view
// (Settings -> Usage Limits: 5-hour window + weekly limit). No known JSON API —
// gemini.google.com uses obfuscated batchexecute RPCs, so we read the rendered
// page text near the "5-hour" / "week" labels.
public sealed class GeminiProvider : IUsageProvider
{
    private static readonly string[] GoogleCookieNames = { "SID", "__Secure-1PSID", "SAPISID" };
    private readonly WebViewHost _host;

    public string Id => "gemini";
    public string DisplayName => "Gemini";

    public GeminiProvider()
    {
        string udf = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiUsageCounter", "WebView2", "gemini");
        _host = new WebViewHost(udf);
    }

    public async Task<AuthState> CheckAuthAsync()
    {
        var cookies = await _host.GetCookiesAsync("https://gemini.google.com");
        bool signedIn = cookies.Any(c => GoogleCookieNames.Contains(c.Name) && !string.IsNullOrEmpty(c.Value));
        return signedIn ? AuthState.SignedIn : AuthState.SignedOut;
    }

    public Task<bool> PresentLoginAsync()
        => _host.ShowLoginAsync("Sign in to Gemini",
            "https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fgemini.google.com%2Fapp",
            async () =>
            {
                // Only consider it done once we've actually landed on gemini (cookies
                // alone appear mid-SSO). Cookie-only read here — never navigates.
                if (!_host.CurrentUrl.Contains("gemini.google.com")) return false;
                var cookies = await _host.GetCookiesAsync("https://gemini.google.com");
                return cookies.Any(c => GoogleCookieNames.Contains(c.Name) && !string.IsNullOrEmpty(c.Value));
            });

    public Task SignOutAsync() => _host.DeleteCookiesAsync("google.com");

    public async Task<FetchResult> FetchUsageAsync()
    {
        // Strategy: read percentages near the "5-hour"/"week" labels from page text.
        // If not visible, walk the UI: click Settings, then Usage Limits, read dialog.
        // Label-based matching only — Gemini's class names churn weekly.
        const string body = """
            if (location.host.indexOf('accounts.google.com') >= 0) { post(JSON.stringify({ error: 'auth' })); return; }
            const pageText = () => (document.body.innerText || '').replace(/ /g, ' ');
            function near(text, re) {
                const idx = text.search(re); if (idx < 0) return null;
                const seg = text.slice(idx, idx + 300);
                const m = seg.match(/(\d+(?:\.\d+)?)\s*%/); if (!m) return null;
                const around = seg.slice(Math.max(0, m.index - 40), m.index + 40);
                const out = { pct: parseFloat(m[1]), remaining: /left|remain/i.test(around) };
                const rm = seg.match(/resets?[^\n.;]{0,80}/i); if (rm) out.reset = rm[0];
                return out;
            }
            function grab() {
                const text = pageText();
                const session = near(text, /5[\s-]?hour|five[\s-]?hour|current\s+limit|current\s+usage|usage\s+limit/i);
                const weekly = near(text, /week|weekly/i);
                if (!session && !weekly) return null;
                return { session, weekly };
            }
            function clickMatch(re) {
                const els = Array.from(document.querySelectorAll('button, [role="button"], [role="menuitem"], [role="tab"], a'));
                const el = els.find(e => {
                    const label = e.getAttribute('aria-label') || '';
                    const txt = (e.textContent || '').trim();
                    return re.test(label) || (txt.length > 0 && txt.length < 40 && re.test(txt));
                });
                if (el) { el.click(); return true; }
                return false;
            }
            let r = grab();
            if (!r) {
                if (clickMatch(/settings|setting|usage|quota|limit/i)) {
                    await new Promise(res => setTimeout(res, 1200));
                    if (clickMatch(/usage|quota|limit/i)) {
                        for (let i = 0; i < 5 && !r; i++) { await new Promise(res => setTimeout(res, 1000)); r = grab(); }
                    }
                    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true }));
                }
            }
            post(JSON.stringify(r ? { data: r } : { error: 'notfound' }));
            """;

        string[] urls =
        {
            "https://gemini.google.com/app?hl=en",
            "https://gemini.google.com/app/settings?hl=en",
            "https://gemini.google.com/app/settings/usage?hl=en",
            "https://gemini.google.com/app/usage?hl=en",
        };

        foreach (var url in urls)
        {
            var result = await FetchFromPageAsync(url, body);
            if (result.Kind != FetchKind.Failure) return result;
        }
        return FetchResult.Failure;
    }

    private async Task<FetchResult> FetchFromPageAsync(string url, string body)
    {
        // Gemini's SPA is slow to hydrate — give it a longer settle.
        string? raw = await _host.RunAsync(url, body, settleMs: 3000);
        if (raw == null) return FetchResult.Failure;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetString() == "auth")
                return FetchResult.AuthExpired;
            if (!root.TryGetProperty("data", out var data)) return FetchResult.Failure;

            var (sPct, sReset) = ParseLane(data, "session");
            var (wPct, wReset) = ParseLane(data, "weekly");
            if (sPct == null && wPct == null) return FetchResult.Failure;

            return FetchResult.Success(new ProviderUsage
            {
                FetchedAt = DateTime.Now,
                SessionPct = sPct,
                SessionResetAt = sReset,
                WeeklyPct = wPct,
                WeeklyResetAt = wReset,
            });
        }
        catch { return FetchResult.Failure; }
    }

    private static (double? pct, DateTime? reset) ParseLane(JsonElement data, string key)
    {
        if (!data.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object) return (null, null);
        if (el.TryGetProperty("pct", out var pe) == false || JsonHelpers.Num(pe) is not { } pct) return (null, null);

        bool remaining = el.TryGetProperty("remaining", out var re) && re.ValueKind == JsonValueKind.True;
        double used = remaining ? Math.Max(0, 100 - pct) : pct;
        string? resetText = el.TryGetProperty("reset", out var rt) ? rt.GetString() : null;
        return (used, ParseResetDuration(resetText));
    }

    // Parses absolute reset times ("Resets at 2:04 PM", "Resets Jun 23 at 2:04 PM") and
    // relative durations ("1h 23m", "57 min", "2 days") -> future Date.
    private static DateTime? ParseResetDuration(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // Try parsing absolute dates/times first (e.g. "Resets at 2:04 PM" or "Resets Jun 23 at 2:04 PM")
        string clean = text.Replace("Resets", "", StringComparison.OrdinalIgnoreCase)
                           .Replace("at", "", StringComparison.OrdinalIgnoreCase)
                           .Trim();
        clean = Regex.Replace(clean, @"\s+", " ");

        if (DateTime.TryParse(clean, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            if (parsedDate < DateTime.Now && !text.Contains("yesterday", StringComparison.OrdinalIgnoreCase))
            {
                // If it's just a time like "2:04 PM" and it has already passed today, it refers to tomorrow.
                if (!Regex.IsMatch(clean, @"[a-zA-Z]"))
                {
                    parsedDate = parsedDate.AddDays(1);
                }
            }
            return parsedDate;
        }

        double total = 0;
        var d = Regex.Match(text, @"(\d+)\s*d", RegexOptions.IgnoreCase);
        var h = Regex.Match(text, @"(\d+)\s*h", RegexOptions.IgnoreCase);
        var m = Regex.Match(text, @"(\d+)\s*m(?:in)?", RegexOptions.IgnoreCase);
        if (d.Success) total += int.Parse(d.Groups[1].Value) * 86400.0;
        if (h.Success) total += int.Parse(h.Groups[1].Value) * 3600.0;
        if (m.Success) total += int.Parse(m.Groups[1].Value) * 60.0;
        return total > 0 ? DateTime.Now.AddSeconds(total) : null;
    }
}
