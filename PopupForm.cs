using System.Drawing.Drawing2D;

namespace AiUsageCounter;

public sealed class PopupModel
{
    public string Title = "Claude";
    public string StatusLine = "";
    public bool SignedIn;
    public double SessionFrac;
    public string SessionValue = "—";
    public string SessionReset = "";
    public double WeeklyFrac;
    public string WeeklyValue = "—";
    public string WeeklyReset = "";
    public Color Accent = Color.FromArgb(80, 170, 255);
}

// Popup anchored bottom-right, shown on tray click. Renders one section per
// provider, stacked. Closes when it loses focus (click-outside).
public sealed class PopupForm : Form
{
    private IReadOnlyList<PopupModel> _models = Array.Empty<PopupModel>();
    private const int SectionHeight = 150;
    private const int TopPad = 14;

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

    public void ShowWith(IReadOnlyList<PopupModel> models)
    {
        _models = models;
        Height = TopPad * 2 + Math.Max(1, models.Count) * SectionHeight;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Invalidate();
        Show();
        Activate();
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
            y += SectionHeight;
            if (i < _models.Count - 1)
            {
                using var sep = new Pen(Color.FromArgb(55, 55, 60));
                g.DrawLine(sep, 16, y - 8, Width - 16, y - 8);
            }
        }
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

        DrawBar(g, "Session (5h)", m.SessionValue, m.SessionReset, m.SessionFrac,
            m.Accent, top + 48, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
        DrawBar(g, "Weekly", m.WeeklyValue, m.WeeklyReset, m.WeeklyFrac,
            Color.FromArgb(120, 200, 130), top + 102, labelFont, valueFont, smallFont, whiteBrush, dimBrush);
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
