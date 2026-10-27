using System.Drawing.Drawing2D;

namespace AiUsageCounter;

public sealed class PopupModel
{
    public string Title = "Claude";
    public string StatusLine = "";
    public bool SignedIn;
    public bool HasSession = true;
    public double SessionFrac;
    public string SessionValue = "—";
    public string SessionReset = "";
    public double WeeklyFrac;
    public string WeeklyValue = "—";
    public string WeeklyReset = "";
    public bool HasWeeklyModel;
    public string WeeklyModelLabel = "";
    public double WeeklyModelFrac;
    public string WeeklyModelValue = "—";
    public string WeeklyModelReset = "";
    public Color Accent = Color.FromArgb(80, 170, 255);

    internal static bool ShouldShowSession(string providerId, double? sessionUsage)
        => providerId != "codex" || sessionUsage != null;
}

// Popup anchored bottom-right, shown on tray click. Renders one section per
// provider, stacked. Closes when it loses focus (click-outside).
public sealed class PopupForm : Form
{
    private IReadOnlyList<PopupModel> _models = Array.Empty<PopupModel>();
    private SystemUsage? _system;
    private const int BarBlock = 54;       // one label+bar+reset row
    private const int BarTop = 48;         // first bar offset inside a section
    private const int BottomPad = 4;       // keeps the separator clear of the reset text
    private const int TopPad = 14;
    private const int SystemSectionHeight = BarTop + 3 * BarBlock + BottomPad; // CPU / RAM / temp

    // Sections size to their bar count so the gap between cards stays even.
    private static int BarCountFor(PopupModel m)
        => 1 + (m.HasSession ? 1 : 0) + (m.HasWeeklyModel ? 1 : 0);

    private static int SectionHeightFor(PopupModel m)
        => BarTop + BarCountFor(m) * BarBlock + BottomPad;

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Width = 320;
        BackColor = Color.FromArgb(32, 32, 36);
        DoubleBuffered = true;
        Deactivate += (_, _) => Hide();
    }

    public void ShowWith(IReadOnlyList<PopupModel> models, SystemUsage? system)
    {
        _models = models;
        _system = system;
        int content = models.Count == 0 ? BarTop + 2 * BarBlock + BottomPad : models.Sum(SectionHeightFor);
        Height = TopPad * 2 + content + SystemSectionHeight;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Invalidate();
        Show();
        Activate();
    }

    // เรียกทุก 2 วินาทีระหว่าง popup เปิด — วาดใหม่เฉพาะตัวเลขระบบ
    public void UpdateSystem(SystemUsage system)
    {
        _system = system;
        if (Visible) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int y = TopPad;
        for (int i = 0; i < _models.Count; i++)
        {
            DrawSection(g, _models[i], y);
            y += SectionHeightFor(_models[i]);
            // System อยู่ท้ายเสมอ จึงคั่นทุก provider
            using var sep = new Pen(Color.FromArgb(55, 55, 60));
            g.DrawLine(sep, 16, y - 8, Width - 16, y - 8);
        }
        DrawSystem(g, _system, y);
    }

    private void DrawSystem(Graphics g, SystemUsage? s, int top)
    {
        using var titleFont = new Font("Segoe UI Semibold", 11f);
        using var labelFont = new Font("Segoe UI", 9f);
        using var valueFont = new Font("Segoe UI Semibold", 10f);
        using var smallFont = new Font("Segoe UI", 8f);
        using var whiteBrush = new SolidBrush(Color.FromArgb(235, 235, 240));
        using var dimBrush = new SolidBrush(Color.FromArgb(150, 150, 158));

        g.DrawString("System", titleFont, whiteBrush, 16, top);
        g.DrawString("Updates every 2s", smallFont, dimBrush, 16, top + 22);

        int y = top + BarTop;
        DrawBar(g, "CPU", s?.CpuPct is { } c ? $"{c:F1}%" : "—", "", (s?.CpuPct ?? 0) / 100.0,
            Color.FromArgb(80, 170, 255), y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
        y += BarBlock;

        string ramDetail = s is { RamTotalGb: > 0 } ? $"{s.RamUsedGb:F1} / {s.RamTotalGb:F1} GB" : "";
        DrawBar(g, "RAM", s?.RamPct is { } r ? $"{r:F1}%" : "—", ramDetail, (s?.RamPct ?? 0) / 100.0,
            Color.FromArgb(120, 200, 130), y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
        y += BarBlock;

        // แถบอุณหภูมิเทียบกับ 100 °C (ใกล้ Tjmax ของ CPU ทั่วไป)
        DrawBar(g, "CPU temp", s?.TempC is { } t ? $"{t:F0} °C" : "—", s?.TempNote ?? "", (s?.TempC ?? 0) / 100.0,
            Color.FromArgb(225, 160, 90), y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
    }

    private void DrawSection(Graphics g, PopupModel m, int top)
    {
        var white = Color.FromArgb(235, 235, 240);
        var dim = Color.FromArgb(150, 150, 158);

        using var titleFont = new Font("Segoe UI Semibold", 11f);
        using var labelFont = new Font("Segoe UI", 9f);
        using var valueFont = new Font("Segoe UI Semibold", 10f);
        using var smallFont = new Font("Segoe UI", 8f);
        using var whiteBrush = new SolidBrush(white);
        using var dimBrush = new SolidBrush(dim);

        g.DrawString(m.Title, titleFont, whiteBrush, 16, top);
        g.DrawString(m.StatusLine, smallFont, dimBrush, 16, top + 22);

        var weeklyColor = Color.FromArgb(120, 200, 130);
        var modelColor = Color.FromArgb(225, 160, 90);

        int y = top + BarTop;
        if (m.HasSession)
        {
            DrawBar(g, "Session (5h)", m.SessionValue, m.SessionReset, m.SessionFrac,
                m.Accent, y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
            y += BarBlock;
        }

        DrawBar(g, m.HasWeeklyModel ? "Weekly · all models" : "Weekly", m.WeeklyValue, m.WeeklyReset,
            m.WeeklyFrac, weeklyColor, y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
        y += BarBlock;

        if (m.HasWeeklyModel)
            DrawBar(g, "Weekly · " + m.WeeklyModelLabel, m.WeeklyModelValue, m.WeeklyModelReset,
                m.WeeklyModelFrac, modelColor, y, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
    }

    private void DrawBar(Graphics g, string label, string value, string reset, double frac,
        Color color, int y, Font labelFont, Font valueFont, Font smallFont,
        SolidBrush whiteBrush, SolidBrush dimBrush)
    {
        frac = Math.Clamp(frac, 0, 1);
        int x = 16, w = Width - 32, barH = 8;

        g.DrawString(label, labelFont, dimBrush, x, y);
        var valSize = g.MeasureString(value, valueFont);
        g.DrawString(value, valueFont, whiteBrush, Width - 16 - valSize.Width, y - 1);

        int barY = y + 22;
        using var track = new SolidBrush(Color.FromArgb(60, 60, 66));
        using var fill = new SolidBrush(frac >= 0.9999 ? Color.FromArgb(235, 90, 90) : color);
        FillRounded(g, track, new Rectangle(x, barY, w, barH), barH / 2);
        int fw = (int)Math.Round(w * frac);
        if (fw > 0) FillRounded(g, fill, new Rectangle(x, barY, Math.Max(barH, fw), barH), barH / 2);

        if (!string.IsNullOrEmpty(reset))
            g.DrawString(reset, smallFont, dimBrush, x, barY + 12);
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
