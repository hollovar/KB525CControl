namespace KB525CControl;

/// <summary>Scrollable live log window. Closing it only hides it.</summary>
public sealed class LogForm : Form
{
    readonly TextBox _box = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point),
        BackColor = Color.FromArgb(24, 24, 28),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.None
    };

    /// <summary>The user pressed Clear.</summary>
    public event Action? Cleared;

    public LogForm(IEnumerable<string> initialLines)
    {
        Text = "KB525C Control - log";
        Size = new Size(860, 520);
        MinimumSize = new Size(400, 200);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(6)
        };

        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) =>
        {
            try { if (_box.Text.Length > 0) Clipboard.SetText(_box.Text); } catch { }
        };

        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => { _box.Clear(); Cleared?.Invoke(); };

        bar.Controls.Add(copy);
        bar.Controls.Add(clear);

        Controls.Add(_box);
        Controls.Add(bar);

        _box.Text = string.Join(Environment.NewLine, initialLines);
        ScrollToEnd();
    }

    public void Append(string line)
    {
        _box.AppendText((_box.TextLength > 0 ? Environment.NewLine : "") + line);
        ScrollToEnd();
    }

    void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
