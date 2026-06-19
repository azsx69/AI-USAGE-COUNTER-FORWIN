using System.Runtime.InteropServices;
using Velopack;
using Velopack.Sources;

namespace AiUsageCounter;

// Owns the tray icon, the periodic fetch loop, the local-file watcher, and the
// popup. Drives N providers uniformly; the tray number comes from the selected
// "menu bar source" provider. This is the app's root object.
public sealed class TrayContext : ApplicationContext
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly PopupForm _popup = new();
    private readonly Bitmap _trayLogo = AppAssets.LoadLogoBitmap();
    private readonly SynchronizationContext _ui;

    private readonly ClaudeProvider _claude = new();
    private readonly List<IUsageProvider> _providers;
    private readonly Dictionary<string, Color> _accents = new()
    {
        ["claude"] = Color.FromArgb(80, 170, 255),
        ["codex"] = Color.FromArgb(120, 200, 130),
        ["gemini"] = Color.FromArgb(190, 150, 255),
    };
    private readonly Dictionary<string, string> _liveHost = new()
    {
        ["claude"] = "claude.ai",
        ["codex"] = "chatgpt.com",
        ["gemini"] = "gemini.google.com",
    };

    private readonly Dictionary<string, AuthState> _auth = new();
    private readonly Dictionary<string, ProviderUsage?> _usage = new();
    private readonly HashSet<string> _fetching = new();
    private LocalUsage _local = new();

    private string _menubarSource = "claude";
    private FileSystemWatcher? _watcher;
    private System.Windows.Forms.Timer? _localDebounce;
    private IntPtr _iconHandle = IntPtr.Zero;
    private bool _busyLogin;
    private bool _checkingUpdate;

    private const string UpdateRepoUrl = "https://github.com/azsx69/AI-USAGE-COUNTER-FORWIN";

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _providers = new List<IUsageProvider> { _claude, new CodexProvider(), new GeminiProvider() };
        foreach (var p in _providers) { _auth[p.Id] = AuthState.SignedOut; _usage[p.Id] = null; }

        _menu.Opening += (_, _) => RebuildMenu();
        _notify = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "AI Usage — starting…",
        };
        _notify.MouseClick += OnTrayClick;
        SetTrayIcon("…", false);

        _timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _timer.Tick += (_, _) => FetchAllSignedIn();
        _timer.Start();

        _ = StartupAsync();
    }

    private async Task StartupAsync()
    {
        foreach (var p in _providers) _auth[p.Id] = await p.CheckAuthAsync();
        await RefreshLocalAsync();
        StartWatcher();
        FetchAllSignedIn();
        RefreshDisplay();
        _ = CheckForUpdatesAsync(silent: true);
    }

    // MARK: - Auto update (Velopack + GitHub Releases)

    // ตรวจและติดตั้ง update จาก GitHub Releases. silent=true เรียกตอนเปิดแอป
    // จะเงียบเมื่อไม่มีอัปเดต/รันนอก installer; silent=false เป็นการกดจากเมนู
    // จะแจ้งผลทุกกรณี
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            var mgr = new UpdateManager(new GithubSource(UpdateRepoUrl, null, false));
            if (!mgr.IsInstalled)
            {
                if (!silent)
                    MessageBox.Show("ยังไม่ได้ติดตั้งผ่านตัวติดตั้ง — update ใช้ได้เฉพาะเวอร์ชันที่ติดตั้งแล้ว",
                        "AI Usage Counter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null)
            {
                if (!silent)
                    MessageBox.Show($"เป็นเวอร์ชันล่าสุดแล้ว (v{mgr.CurrentVersion})",
                        "AI Usage Counter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ask = MessageBox.Show(
                $"มีเวอร์ชันใหม่ v{info.TargetFullRelease.Version} (ปัจจุบัน v{mgr.CurrentVersion})\nต้องการอัปเดตและรีสตาร์ทตอนนี้หรือไม่?",
                "AI Usage Counter — มีอัปเดต", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;

            await mgr.DownloadUpdatesAsync(info);
            mgr.ApplyUpdatesAndRestart(info);
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show("ตรวจอัปเดตไม่สำเร็จ: " + ex.Message,
                    "AI Usage Counter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _checkingUpdate = false; }
    }

    // MARK: - Fetching

    private void FetchAllSignedIn()
    {
        foreach (var p in _providers)
            if (_auth[p.Id] == AuthState.SignedIn)
                _ = FetchAsync(p);
    }

    private async Task FetchAsync(IUsageProvider p)
    {
        if (_auth[p.Id] != AuthState.SignedIn || !_fetching.Add(p.Id)) return;
        try
        {
            var res = await p.FetchUsageAsync();
            switch (res.Kind)
            {
                case FetchKind.Success: _usage[p.Id] = res.Usage; break;
                case FetchKind.AuthExpired: _auth[p.Id] = AuthState.Expired; _usage[p.Id] = null; break;
                case FetchKind.Failure: break;
            }
        }
        finally { _fetching.Remove(p.Id); }
        RefreshDisplay();
    }

    private async Task RefreshLocalAsync()
    {
        _local = await LocalUsageParser.ParseAsync();
        RefreshDisplay();
    }

    // MARK: - Local file watcher (Claude only)

    private void StartWatcher()
    {
        string dir = LocalUsageParser.ProjectsDir;
        if (!Directory.Exists(dir)) return;

        _localDebounce = new System.Windows.Forms.Timer { Interval = 800 };
        _localDebounce.Tick += (_, _) => { _localDebounce!.Stop(); _ = RefreshLocalAsync(); };

        _watcher = new FileSystemWatcher(dir, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler onChange = (_, _) =>
            _ui.Post(_ => { _localDebounce!.Stop(); _localDebounce.Start(); }, null);
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
    }

    // MARK: - Auth actions

    private async void OnSignIn(IUsageProvider p)
    {
        if (_busyLogin) return;
        _busyLogin = true;
        try
        {
            await p.PresentLoginAsync();
            _auth[p.Id] = await p.CheckAuthAsync();
            if (_auth[p.Id] == AuthState.SignedIn)
            {
                if (_menubarSource == "claude" && SignedInCount() == 1) _menubarSource = p.Id;
                await FetchAsync(p);
            }
            RefreshDisplay();
        }
        finally { _busyLogin = false; }
    }

    private async void OnSignOut(IUsageProvider p)
    {
        await p.SignOutAsync();
        _auth[p.Id] = AuthState.SignedOut;
        _usage[p.Id] = null;
        if (_menubarSource == p.Id) _menubarSource = "claude";
        RefreshDisplay();
    }

    private int SignedInCount() => _providers.Count(p => _auth[p.Id] == AuthState.SignedIn);

    // MARK: - Display resolution

    // (session%, weekly%, statusLine, live) for a provider. Claude falls back to
    // the local .jsonl estimate; other providers show live-only.
    private (double? session, double? weekly, string status, bool live) Resolve(string id)
    {
        bool live = _auth[id] == AuthState.SignedIn && _usage[id] is not null;
        var u = _usage[id];

        if (id == "claude")
        {
            double s = (live ? u!.SessionPct : null) ?? _local.SessionPct;
            double w = (live ? u!.WeeklyPct : null) ?? _local.WeeklyPct;
            string status = live ? "Live • claude.ai"
                : _auth[id] == AuthState.Expired ? "Session expired — re-sign in"
                : _local.SessionActive || _local.WeeklyTokens > 0 ? "Local estimate (Claude Code)"
                : "Not signed in";
            return (s, w, status, live);
        }

        string host = _liveHost.GetValueOrDefault(id, "");
        string st = live ? $"Live • {host}"
            : _auth[id] == AuthState.Expired ? "Session expired — re-sign in"
            : _auth[id] == AuthState.SignedIn ? "Signed in — fetching…"
            : "Not signed in";
        return (live ? u!.SessionPct : null, live ? u!.WeeklyPct : null, st, live);
    }

    private void RefreshDisplay()
    {
        var (session, weekly, _, live) = Resolve(_menubarSource);
        double s = session ?? 0;
        bool atLimit = s >= 99.99;
        bool unknown = session == null;

        string iconText = unknown ? "—" : atLimit ? "MAX" : ((int)Math.Round(s)).ToString();
        SetTrayIcon(iconText, atLimit);

        string name = _providers.First(p => p.Id == _menubarSource).DisplayName;
        string src = live ? "live" : unknown ? "—" : "local";
        string wk = weekly is { } wv ? $"{wv:F1}%" : "—";
        string ses = session is { } sv ? $"{sv:F1}%" : "—";
        _notify.Text = Truncate($"{name}  {ses} | {wk}  ({src})", 63);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        foreach (var p in _providers)
        {
            var state = _auth[p.Id];
            if (state == AuthState.SignedIn)
                _menu.Items.Add(new ToolStripMenuItem($"{p.DisplayName}: Sign out", null, (_, _) => OnSignOut(p)));
            else
            {
                string label = state == AuthState.Expired ? $"{p.DisplayName}: Re-sign in" : $"{p.DisplayName}: Sign in";
                _menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => OnSignIn(p)));
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var sourceMenu = new ToolStripMenuItem("Show in tray");
        foreach (var p in _providers)
        {
            var item = new ToolStripMenuItem(p.DisplayName, null, (_, _) => { _menubarSource = p.Id; RefreshDisplay(); })
            {
                Checked = _menubarSource == p.Id,
                Enabled = p.Id == "claude" || _auth[p.Id] == AuthState.SignedIn,
            };
            sourceMenu.DropDownItems.Add(item);
        }
        _menu.Items.Add(sourceMenu);

        _menu.Items.Add(new ToolStripMenuItem("Refresh now", null, (_, _) => { FetchAllSignedIn(); _ = RefreshLocalAsync(); }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Check for updates…", null, (_, _) => _ = CheckForUpdatesAsync(silent: false)));
        _menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
    }

    private void SetTrayIcon(string text, bool alert)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(_trayLogo, new Rectangle(0, 0, 32, 32));

            var badgeRect = text.Length >= 3
                ? new Rectangle(2, 18, 28, 12)
                : new Rectangle(15, 17, 15, 13);
            using var badge = new SolidBrush(alert
                ? Color.FromArgb(230, 210, 45, 45)
                : Color.FromArgb(210, 18, 34, 64));
            g.FillEllipse(badge, badgeRect);

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float size = text.Length >= 3 ? 8f : 9f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var sz = g.MeasureString(text, font);
            g.DrawString(text, font, brush,
                badgeRect.X + (badgeRect.Width - sz.Width) / 2f,
                badgeRect.Y + (badgeRect.Height - sz.Height) / 2f - 1f);
        }

        IntPtr handle = bmp.GetHicon();
        _notify.Icon = Icon.FromHandle(handle);
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_popup.Visible) { _popup.Hide(); return; }
        _popup.ShowWith(BuildPopupModels());
    }

    private List<PopupModel> BuildPopupModels()
    {
        var list = new List<PopupModel>();
        foreach (var p in _providers)
        {
            var (session, weekly, status, live) = Resolve(p.Id);
            var u = _usage[p.Id];
            DateTime? sReset = live ? u!.SessionResetAt : (p.Id == "claude" ? _local.SessionResetAt : null);
            DateTime? wReset = live ? u!.WeeklyResetAt : null;

            list.Add(new PopupModel
            {
                Title = p.DisplayName,
                StatusLine = status,
                SignedIn = _auth[p.Id] == AuthState.SignedIn,
                Accent = _accents.GetValueOrDefault(p.Id, Color.FromArgb(80, 170, 255)),
                SessionFrac = (session ?? 0) / 100.0,
                SessionValue = session is { } sv ? $"{sv:F1}%" : "—",
                SessionReset = ResetLabel(sReset),
                WeeklyFrac = (weekly ?? 0) / 100.0,
                WeeklyValue = weekly is { } wv ? $"{wv:F1}%" : "—",
                WeeklyReset = ResetLabel(wReset),
            });
        }
        return list;
    }

    private static string ResetLabel(DateTime? at)
    {
        if (at is not { } t || t <= DateTime.Now) return "";
        return "Resets in " + FormatDuration(t - DateTime.Now);
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalMinutes < 1) return "<1m";
        if (ts.TotalHours < 1) return $"{(int)ts.TotalMinutes}m";
        if (ts.TotalDays < 1) return $"{ts.Hours}h {ts.Minutes}m";
        return $"{(int)ts.TotalDays}d {ts.Hours}h";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void Quit()
    {
        _timer.Stop();
        _watcher?.Dispose();
        _notify.Visible = false;
        _notify.Dispose();
        _trayLogo.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        ExitThread();
    }
}
