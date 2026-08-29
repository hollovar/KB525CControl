using System.Drawing.Drawing2D;

namespace KB525CControl;

/// <summary>
/// Small on-screen popup ("Mic · muted") shown near the bottom of the screen for a moment.
///
/// It must never take the focus: the meeting app's shortcuts go to the focused window, and
/// the user is typing somewhere. Hence WS_EX_NOACTIVATE and ShowWithoutActivation.
/// </summary>
public sealed class OsdForm : Form
{
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;

    readonly System.Windows.Forms.Timer _hold = new();
    readonly System.Windows.Forms.Timer _fade = new() { Interval = 25 };
    string _text = "";
    Font? _font;

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.White;
        DoubleBuffered = true;

        _hold.Tick += (_, _) => { _hold.Stop(); _fade.Start(); };
        _fade.Tick += (_, _) =>
        {
            Opacity -= 0.08;
            if (Opacity <= 0.05) { _fade.Stop(); Hide(); }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowMessage(string text, int durationMs, float fontSize)
    {
        _text = text;
        _font?.Dispose();
        _font = new Font("Segoe UI", fontSize is > 6 and < 60 ? fontSize : 14f, FontStyle.Regular, GraphicsUnit.Point);

        var pad = (int)Math.Round(_font.Size * 1.1);
        var measured = TextRenderer.MeasureText(text, _font);
        Size = new Size(measured.Width + pad * 2, measured.Height + pad * 2);

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 80);

        var radius = Height / 2;
        using (var path = new GraphicsPath())
        {
            var d = radius;
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(Width - d, 0, d, d, 270, 90);
            path.AddArc(Width - d, Height - d, d, d, 0, 90);
            path.AddArc(0, Height - d, d, d, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        }

        _fade.Stop();
        _hold.Stop();
        Opacity = 0.96;
        _hold.Interval = Math.Max(300, durationMs);

        if (!Visible) Show(); else Invalidate();
        _hold.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_font is null) return;
        TextRenderer.DrawText(e.Graphics, _text, _font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hold.Dispose();
            _fade.Dispose();
            _font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
