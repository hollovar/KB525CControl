using System.Diagnostics;
using System.Text;

namespace KB525CControl.Settings;

/// <summary>
/// Settings window: every option of config.json with a description next to it. Works on a
/// copy of the configuration; "Save & apply" validates, writes config.json and hands the
/// new configuration to the tray app.
/// </summary>
public sealed class SettingsForm : Form
{
    AppConfig _cfg;

    /// <summary>Raised with the validated, saved configuration.</summary>
    public event Action<AppConfig>? Saved;

    /// <summary>Plays a pattern for the "Test" buttons; returns an error text or null.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? TestPattern { get; set; }

    readonly ListBox _sections = new() { Dock = DockStyle.Left, Width = 170, IntegralHeight = false, BorderStyle = BorderStyle.None };
    readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 8, 16, 8) };
    readonly List<(string Name, Control Page)> _pages = new();
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(8, 8, 0, 0) };

    // General
    readonly ComboBox _mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 600, DropDownWidth = 600 };
    readonly Label _modeHelp = new();
    readonly CheckBox _micLed = new() { AutoSize = true, Text = "Mic LED follows the Windows mute state (red while muted)" };
    readonly CheckBox _videoLed = new() { AutoSize = true, Text = "Video LED follows real camera usage" };
    readonly NumericUpDown _menuFont = new() { Minimum = 7, Maximum = 30, Width = 80 };
    readonly TextBox _editor = new() { Width = 420 };

    // Meeting layer
    readonly Dictionary<CollabButton, ActionEditor> _meeting = new();

    // Idle layer
    readonly ComboBox _idleLeds = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 600, DropDownWidth = 600 };
    readonly NumericUpDown _doubleMs = new() { Minimum = 100, Maximum = 1000, Increment = 50, Width = 80 };
    readonly Dictionary<CollabButton, (ActionEditor Press, ActionEditor Double)> _idle = new();

    // Meeting apps
    readonly Dictionary<(string App, string Field), TextBox> _shortcuts = new();

    // OSD
    readonly CheckBox _osdEnabled = new() { AutoSize = true, Text = "Show the on-screen popup when a button does something" };
    readonly NumericUpDown _osdMs = new() { Minimum = 200, Maximum = 10000, Increment = 100, Width = 80 };
    readonly NumericUpDown _osdFont = new() { Minimum = 8, Maximum = 40, Width = 80 };

    // Signals
    readonly NumericUpDown _stickyMs = new() { Minimum = 0, Maximum = 86_400_000, Increment = 60_000, Width = 110 };
    readonly DataGridView _presets = new()
    {
        Width = 560, Height = 160, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle
    };

    static readonly (CollabButton Button, string Name)[] Buttons =
    {
        (CollabButton.Video, "Video"), (CollabButton.Share, "Share"), (CollabButton.Chat, "Chat"), (CollabButton.Mic, "Mic")
    };

    public SettingsForm(AppConfig current)
    {
        _cfg = current.Clone();

        Text = "KB525C Control - settings";
        Size = new Size(1000, 720);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point);

        BuildPages();

        _sections.Font = new Font(Font.FontFamily, 11f, FontStyle.Regular, GraphicsUnit.Point);
        _sections.ItemHeight = 30;
        _sections.DrawMode = DrawMode.OwnerDrawFixed;
        _sections.DrawItem += DrawSection;
        foreach (var (name, _) in _pages) _sections.Items.Add(name);
        _sections.SelectedIndexChanged += (_, _) => ShowPage(_sections.SelectedIndex);

        // Bottom bar: file operations on the left, apply/cancel on the right.
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        static Button Btn(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(8, 2, 8, 2), Margin = new Padding(4) };

        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Left };
        var exportAll = Btn("Save all settings to file...");
        var importAll = Btn("Load settings from file...");
        var defaults = Btn("Set all to default");
        exportAll.Click += (_, _) => ExportToFile();
        importAll.Click += (_, _) => ImportFromFile();
        defaults.Click += (_, _) => ResetToDefaults();
        left.Controls.Add(exportAll);
        left.Controls.Add(importAll);
        left.Controls.Add(defaults);
        left.Controls.Add(_status);

        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Dock = DockStyle.Right };
        var save = Btn("Save && apply");
        var cancel = Btn("Cancel");
        var openJson = Btn("Open config.json");
        save.Click += (_, _) => SaveAndApply();
        cancel.Click += (_, _) => Close();
        openJson.Click += (_, _) => OpenJson?.Invoke();
        right.Controls.Add(save);
        right.Controls.Add(cancel);
        right.Controls.Add(openJson);

        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);
        AcceptButton = save;
        CancelButton = cancel;

        var separator = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = SystemColors.ControlDark };

        Controls.Add(_host);
        Controls.Add(separator);
        Controls.Add(_sections);
        Controls.Add(bar);

        LoadValues();
        _sections.SelectedIndex = 0;
    }

    /// <summary>Set by the tray app: opens config.json in the editor.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OpenJson { get; set; }

    void DrawSection(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? SystemColors.Highlight : SystemColors.Window);
        e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _sections.Items[e.Index].ToString(), _sections.Font,
            new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
            selected ? SystemColors.HighlightText : SystemColors.WindowText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    void ShowPage(int index)
    {
        _host.SuspendLayout();
        _host.Controls.Clear();
        if (index >= 0 && index < _pages.Count)
        {
            var page = _pages[index].Page;
            _host.Controls.Add(page);
            _host.AutoScrollPosition = Point.Empty;
        }
        _host.ResumeLayout();
    }

    // --- page construction --------------------------------------------------------

    /// <summary>A vertical stack that fills the host width and grows with its content.</summary>
    static TableLayoutPanel NewPage()
    {
        var p = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return p;
    }

    static Label Title(string text) => new()
    {
        AutoSize = true, Text = text, Margin = new Padding(0, 4, 0, 2),
        Font = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point)
    };

    static Label Help(string text) => new()
    {
        AutoSize = true, Text = text, ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(640, 0), Margin = new Padding(0, 2, 0, 12)
    };

    static Label Heading(string text) => new()
    {
        AutoSize = true, Text = text, Margin = new Padding(0, 0, 0, 4),
        Font = new Font("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Point)
    };

    static void Row(TableLayoutPanel page, string title, Control control, string help)
    {
        page.Controls.Add(Title(title));
        control.Margin = new Padding(0, 0, 0, 0);
        page.Controls.Add(control);
        page.Controls.Add(Help(help));
    }

    static GroupBox Group(string title, params Control[] children)
    {
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10) };
        // A single-column TableLayoutPanel keeps the insertion order (unlike plain Dock=Top).
        var stack = NewPage();
        foreach (var c in children) stack.Controls.Add(c);
        g.Controls.Add(stack);
        return g;
    }

    void BuildPages()
    {
        // 1. General ------------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("General"));

            foreach (var label in new[] { "Layered - meeting layer during meetings, idle layer otherwise", "Meetings only - buttons live and lit only during meetings", "Always - the meeting layer applies all the time" })
                _mode.Items.Add(label);
            _modeHelp.AutoSize = true; _modeHelp.ForeColor = SystemColors.GrayText; _modeHelp.MaximumSize = new Size(640, 0); _modeHelp.Margin = new Padding(0, 2, 0, 12);
            _mode.SelectedIndexChanged += (_, _) => _modeHelp.Text = _mode.SelectedIndex switch
            {
                0 => "A meeting is detected when an application uses the microphone. During a meeting the four buttons use the Meeting buttons page; the rest of the time the Idle buttons page. Recommended.",
                1 => "Outside meetings the buttons are dark and do nothing (the keyboard's default behaviour).",
                _ => "The Meeting buttons page applies always and the buttons are always lit - useful when the meeting detection does not fit your setup.",
            };
            p.Controls.Add(Title("Mode"));
            p.Controls.Add(_mode);
            p.Controls.Add(_modeHelp);

            Row(p, "LEDs", Stack(_micLed, _videoLed),
                "The keyboard's Mic and Video LEDs show the real state instead of a guess: red Mic = the Windows microphone is muted; Video lit = an application is using the camera.");

            Row(p, "Tray menu font size (points)", _menuFont, "Default 11. Applies immediately after saving.");

            var browse = new Button { Text = "Browse...", AutoSize = true };
            browse.Click += (_, _) =>
            {
                using var dlg = new OpenFileDialog { Filter = "Programs|*.exe|All files|*.*", Title = "Editor for config.json" };
                if (dlg.ShowDialog(this) == DialogResult.OK) _editor.Text = dlg.FileName;
            };
            Row(p, "Editor for config.json", Inline(_editor, browse),
                "Used by \"Open config.json\". Leave empty for automatic: Notepad++ when installed, otherwise the Windows default for .json files.");

            AddPage("General", p);
        }

        // 2. Meeting buttons ------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("Meeting buttons"));
            p.Controls.Add(Help("What the four collaboration buttons do while a meeting is running (an application is using the microphone), and always in \"Always\" mode. Single press only - no double press during meetings, on purpose: predictability and zero latency matter more there."));
            foreach (var (button, name) in Buttons)
            {
                var ed = new ActionEditor();
                _meeting[button] = ed;
                p.Controls.Add(Group(name, ed));
            }
            AddPage("Meeting buttons", p);
        }

        // 3. Idle buttons -----------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("Idle buttons"));
            p.Controls.Add(Help("What the buttons do outside meetings in Layered mode: launchers, macros, Windows mute. Each button has a press action and an optional double-press action."));

            foreach (var label in new[] { "Off - all dark (buttons still work)", "Assigned - white for the buttons that have an action", "All - all four white" })
                _idleLeds.Items.Add(label);
            Row(p, "LEDs outside meetings", _idleLeds, "Which buttons are lit in the idle layer. The Mic LED still turns red while the Windows microphone is muted.");
            Row(p, "Double-press window (ms)", _doubleMs,
                "Only buttons WITH a double-press action wait this long after a press to see whether a second press follows; the others fire immediately. 250-350 ms feels natural.");

            foreach (var (button, name) in Buttons)
            {
                var press = new ActionEditor("Press");
                var dbl = new ActionEditor("Double press (optional)");
                _idle[button] = (press, dbl);
                p.Controls.Add(Group(name, press, dbl));
            }
            AddPage("Idle buttons", p);
        }

        // 4. Meeting apps -----------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("Meeting apps"));
            p.Controls.Add(Help("Keyboard shortcuts the Smart actions send to the meeting application in front. Empty = the app has no such shortcut (the button then reports it on screen). Fix them here if a new version of the app changes them."));

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 5, Margin = new Padding(0, 0, 0, 12) };
            var fields = new[] { ("MicToggle", "Mic toggle"), ("CameraToggle", "Camera toggle"), ("Share", "Share screen"), ("Chat", "Chat") };
            grid.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
            for (var c = 0; c < fields.Length; c++)
                grid.Controls.Add(new Label { Text = fields[c].Item2, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(4, 4, 8, 4) }, c + 1, 0);

            var apps = new[] { (MeetingAppDetector.Meet, "Google Meet"), (MeetingAppDetector.Teams, "Microsoft Teams"), (MeetingAppDetector.Zoom, "Zoom") };
            for (var r = 0; r < apps.Length; r++)
            {
                grid.Controls.Add(new Label { Text = apps[r].Item2, AutoSize = true, Margin = new Padding(0, 8, 8, 4) }, 0, r + 1);
                for (var c = 0; c < fields.Length; c++)
                {
                    var tb = new TextBox { Width = 130, Margin = new Padding(4) };
                    _shortcuts[(apps[r].Item1, fields[c].Item1)] = tb;
                    grid.Controls.Add(tb, c + 1, r + 1);
                }
            }
            p.Controls.Add(grid);
            p.Controls.Add(Help("Google Meet has no share-screen shortcut. Alternatives for that button: ctrl+alt+h (raise hand), ctrl+alt+p (participants). Detection: a browser tab titled \"Meet\", the Teams app, or Zoom."));
            AddPage("Meeting apps", p);
        }

        // 5. On-screen popup ----------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("On-screen popup"));
            Row(p, "Popup", _osdEnabled, "A small, non-focusable message at the bottom of the screen with what the button just did (\"Mic - muted\", \"Share - wt.exe in C:\\...\").");
            Row(p, "Duration (ms)", _osdMs, "How long the popup stays before fading out.");
            Row(p, "Font size (points)", _osdFont, "");
            AddPage("On-screen popup", p);
        }

        // 6. Signals -------------------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("Signals"));
            p.Controls.Add(Help("LED signals are blink patterns other programs can request through the kb525c command-line client or the named pipe - e.g. Claude Code blinks when it is waiting for you. A sticky (infinite) signal stays until a button is pressed, the caller stops it, or this timeout passes."));
            Row(p, "Sticky signal timeout (ms)", _stickyMs, "Safety limit for infinite signals. 0 = unlimited. Default 600000 (10 minutes).");

            _presets.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", FillWeight = 30 });
            _presets.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Pattern", FillWeight = 70 });
            var test = new Button { Text = "Test selected", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            test.Click += (_, _) =>
            {
                var row = _presets.CurrentRow;
                var pattern = row?.Cells[1].Value?.ToString();
                if (string.IsNullOrWhiteSpace(pattern)) { SetStatus("select a preset row first"); return; }
                SetStatus(TestPattern?.Invoke(pattern) ?? "");
            };
            Row(p, "Named presets", Stack(_presets, test),
                "Your own named patterns; they may override the built-in ones (done, error, attention, running, chase, pulse, red, all). Use them as  kb525c signal <name>.\n\n" +
                "Pattern language:  FRAMES[@MS][xN]\n" +
                "  frames: 4 characters per frame, one per button - V S C M lit white, v / m red, - dark; frames separated by /\n" +
                "  @MS = milliseconds per frame (default 150), @0 = hold the last frame\n" +
                "  xN = repeat count, x* = forever (sticky)\n" +
                "  examples:  VSCM/----@150x3      v--m@0      V---/-S--/--C-/---M@100x*\n" +
                "  raw LED byte for experiments: 0x41@0 (bits 0x40/0x80 do nothing)");
            AddPage("Signals", p);
        }

        // 7. Integration -------------------------------------------------------------------------
        {
            var p = NewPage();
            p.Controls.Add(Heading("Integration"));
            var cli = Path.Combine(AppContext.BaseDirectory, "kb525c.exe");
            var cliNote = File.Exists(cli) ? cli : "kb525c.exe (not found next to the app - run install.ps1)";

            p.Controls.Add(Help(
                "How other programs talk to KB525C Control:\n\n" +
                "1. Command line:  kb525c.exe\n" +
                $"   {cliNote}\n" +
                "   kb525c signal done            play a pattern once\n" +
                "   kb525c signal attention --sticky --ack focus:WindowsTerminal   blink until a button is pressed; that press focuses the terminal\n" +
                "   kb525c run -- <command>       run something, blink 'running' meanwhile, 'done' or 'error' at the end\n" +
                "   kb525c status / off / press <button> / reload\n\n" +
                "2. Named pipe  \\\\.\\pipe\\KB525CControl  - one JSON line in, one JSON line out (same commands). Only the current user may connect.\n\n" +
                "3. Outward: every Run / RunInActiveFolder / Macro action receives KB525C_BUTTON, KB525C_PRESS, KB525C_LAYER, KB525C_ACTIVE_FOLDER, KB525C_MIC_MUTED and KB525C_MEETING environment variables, so a script can branch on them.\n\n" +
                "Claude Code: the hooks below make the keyboard blink 'done' when Claude finishes and blink 'attention' (sticky) when it waits for a permission or for you. Pressing any button dismisses the blink and focuses Windows Terminal. They go into ~/.claude/settings.json under \"hooks\"."));

            // The hooks that are actually configured right now - they live in Claude Code's own
            // settings file, not in this app's config, so show them here to avoid the hunt.
            var claudeSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            var live = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Width = 640, Height = 150,
                Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point), Text = ReadClaudeHooks(claudeSettings)
            };
            var openClaude = new Button { Text = "Open settings.json", AutoSize = true, Margin = new Padding(0, 4, 0, 0), Enabled = File.Exists(claudeSettings) };
            openClaude.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(claudeSettings) { UseShellExecute = true }); }
                catch (Exception ex) { SetStatus($"could not open: {ex.Message}"); }
            };
            var refresh = new Button { Text = "Refresh", AutoSize = true, Margin = new Padding(8, 4, 0, 0) };
            refresh.Click += (_, _) => live.Text = ReadClaudeHooks(claudeSettings);
            Row(p, "Your Claude Code hooks right now - this is what decides which pattern blinks", Stack(live, Inline(openClaude, refresh)),
                $"Read live from {claudeSettings}. Each hook line is summarised as  plays: <pattern>  with its label. To change the pattern or the label, edit that file (Open settings.json), then Refresh; Claude Code picks it up in a new session.");

            var hooks = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Width = 640, Height = 220,
                Font = new Font("Consolas", 9f, FontStyle.Regular, GraphicsUnit.Point), Text = ClaudeHooksJson
            };
            var copy = new Button { Text = "Copy template", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            copy.Click += (_, _) => { try { Clipboard.SetText(ClaudeHooksJson); SetStatus("copied", ok: true); } catch { } };
            Row(p, "Template - paste into ~/.claude/settings.json if you have no hooks yet", Stack(hooks, copy),
                "Hooks run in Git Bash, so the path uses forward slashes. The trailing \"|| true\" keeps a missing kb525c.exe from blocking Claude (exit code 2 would). Any pattern name works in place of done / attention - e.g. a preset from the Signals page - and --label is the text the popup shows when you press a button to stop the blinking. Add --sticky to the Stop hook too if you want the \"finished\" blink to wait for your press instead of ending after a few seconds - otherwise a late press runs the button's normal function.");

            p.Controls.Add(Help($"Settings file: {AppConfig.DefaultPath}\nThe tray app reloads it on Save here, from the tray menu, or with  kb525c reload."));
            AddPage("Integration", p);
        }
    }

    const string ClaudeHooksJson =
        "\"hooks\": {\n" +
        "  \"Stop\": [\n" +
        "    { \"hooks\": [ { \"type\": \"command\", \"timeout\": 5,\n" +
        "        \"command\": \"\\\"$LOCALAPPDATA/KB525CControl/kb525c.exe\\\" signal done --label \\\"Claude Code finished\\\" >/dev/null 2>&1 || true\" } ] }\n" +
        "  ],\n" +
        "  \"Notification\": [\n" +
        "    { \"matcher\": \"permission_prompt|idle_prompt\",\n" +
        "      \"hooks\": [ { \"type\": \"command\", \"timeout\": 5,\n" +
        "        \"command\": \"\\\"$LOCALAPPDATA/KB525CControl/kb525c.exe\\\" signal attention --sticky --ack focus:WindowsTerminal --label \\\"Claude Code is waiting\\\" >/dev/null 2>&1 || true\" } ] }\n" +
        "  ]\n" +
        "}";

    void AddPage(string name, Control page) => _pages.Add((name, page));

    /// <summary>"plays: bounce (sticky), label: Claude Code is waiting" for a kb525c hook command.</summary>
    static string SummariseHook(string cmd)
    {
        if (!cmd.Contains("kb525c", StringComparison.OrdinalIgnoreCase)) return "(not a kb525c hook)";
        var m = System.Text.RegularExpressions.Regex.Match(cmd, @"\bsignal\s+(""[^""]+""|\S+)");
        if (!m.Success) return "(kb525c, no signal command)";
        var pattern = m.Groups[1].Value.Trim('"');
        var sticky = cmd.Contains("--sticky", StringComparison.OrdinalIgnoreCase) ? " (sticky)" : "";
        var label = System.Text.RegularExpressions.Regex.Match(cmd, @"--label\s+(?:""([^""]*)""|(\S+))");
        var labelText = label.Success ? (label.Groups[1].Success ? label.Groups[1].Value : label.Groups[2].Value) : "";
        return $"plays: {pattern}{sticky}" + (labelText.Length > 0 ? $", label: {labelText}" : ", no label (the popup shows the pattern)");
    }

    /// <summary>Lists the hook commands of Claude Code's settings.json, one per line.</summary>
    static string ReadClaudeHooks(string path)
    {
        if (!File.Exists(path)) return "(no ~/.claude/settings.json - Claude Code is not set up for this user)";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
                new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "(no hooks configured)";

            var sb = new StringBuilder();
            foreach (var evt in hooks.EnumerateObject())
            {
                if (evt.Value.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var group in evt.Value.EnumerateArray())
                {
                    var matcher = group.TryGetProperty("matcher", out var m) ? $" [{m.GetString()}]" : "";
                    if (!group.TryGetProperty("hooks", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var h in list.EnumerateArray())
                    {
                        var cmd = h.TryGetProperty("command", out var c) ? c.GetString() : null;
                        if (cmd is null) continue;
                        var summary = SummariseHook(cmd);
                        sb.AppendLine($"{evt.Name}{matcher}:  {summary}");
                        sb.AppendLine($"    {cmd}");
                    }
                }
            }
            return sb.Length == 0 ? "(no command hooks configured)" : sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(could not read: {ex.Message})";
        }
    }

    static FlowLayoutPanel Stack(params Control[] controls)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        foreach (var c in controls) f.Controls.Add(c);
        return f;
    }

    static FlowLayoutPanel Inline(params Control[] controls)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        foreach (var c in controls) f.Controls.Add(c);
        return f;
    }

    // --- values ----------------------------------------------------------------------

    void LoadValues()
    {
        _mode.SelectedIndex = (int)_cfg.Mode;
        _micLed.Checked = _cfg.MicLedFollowsSystemMute;
        _videoLed.Checked = _cfg.VideoLedFollowsCamera;
        _menuFont.Value = Math.Clamp((decimal)_cfg.MenuFontSize, _menuFont.Minimum, _menuFont.Maximum);
        _editor.Text = _cfg.Editor ?? "";

        foreach (var (button, _) in Buttons) _meeting[button].LoadAction(_cfg.For(button));

        _idleLeds.SelectedIndex = (int)_cfg.Idle.Leds;
        _doubleMs.Value = Math.Clamp(_cfg.Idle.DoublePressMs, (int)_doubleMs.Minimum, (int)_doubleMs.Maximum);
        foreach (var (button, _) in Buttons)
        {
            var b = _cfg.Idle.For(button);
            _idle[button].Press.LoadAction(b.Press);
            _idle[button].Double.LoadAction(b.Double);
        }

        foreach (var ((app, field), tb) in _shortcuts)
        {
            var s = _cfg.ShortcutsFor(app) ?? new AppShortcuts();
            tb.Text = field switch
            {
                "MicToggle" => s.MicToggle,
                "CameraToggle" => s.CameraToggle,
                "Share" => s.Share,
                _ => s.Chat
            };
        }

        _osdEnabled.Checked = _cfg.Osd.Enabled;
        _osdMs.Value = Math.Clamp(_cfg.Osd.DurationMs, (int)_osdMs.Minimum, (int)_osdMs.Maximum);
        _osdFont.Value = Math.Clamp((decimal)_cfg.Osd.FontSize, _osdFont.Minimum, _osdFont.Maximum);

        _stickyMs.Value = Math.Clamp(_cfg.Signals.StickyTimeoutMs, (int)_stickyMs.Minimum, (int)_stickyMs.Maximum);
        _presets.Rows.Clear();
        foreach (var (name, pattern) in _cfg.Signals.Presets) _presets.Rows.Add(name, pattern);
    }

    void SaveAndApply()
    {
        var cfg = Collect();
        if (cfg is null) return;
        cfg.Save();
        Saved?.Invoke(cfg);
        Close();
    }

    /// <summary>Writes the current (validated) values to a file of the user's choice.</summary>
    void ExportToFile()
    {
        var cfg = Collect();
        if (cfg is null) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Save all settings to file", Filter = "JSON|*.json|All files|*.*", DefaultExt = "json",
            FileName = "kb525c-settings.json", AddExtension = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            cfg.Save(dlg.FileName);
            SetStatus($"saved to {Path.GetFileName(dlg.FileName)} (not applied - use Save & apply for that)", ok: true);
        }
        catch (Exception ex) { SetStatus($"save failed: {ex.Message}"); }
    }

    /// <summary>Loads a settings file into the window; nothing is applied until Save &amp; apply.</summary>
    void ImportFromFile()
    {
        using var dlg = new OpenFileDialog { Title = "Load settings from file", Filter = "JSON|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var loaded = AppConfig.TryRead(dlg.FileName, out var error);
        if (loaded is null) { SetStatus($"load failed: {error}"); return; }
        _cfg = loaded;
        LoadValues();
        SetStatus($"loaded {Path.GetFileName(dlg.FileName)} - press Save & apply to use it", ok: true);
    }

    /// <summary>Puts the factory defaults into the window; nothing is applied until Save &amp; apply.</summary>
    void ResetToDefaults()
    {
        if (MessageBox.Show(this, "Replace every setting in this window with the factory defaults?\n\nNothing is saved until you press Save & apply.",
                "Set all to default", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _cfg = new AppConfig();
        LoadValues();
        SetStatus("defaults loaded - press Save & apply to use them", ok: true);
    }

    /// <summary>Validates the window and copies its values into the working configuration. Null (with a message) when something is wrong.</summary>
    AppConfig? Collect()
    {
        var errors = new List<string>();

        foreach (var (button, name) in Buttons)
        {
            if (_meeting[button].Check() is { } e1) errors.Add($"Meeting buttons / {name}: {e1}");
            if (_idle[button].Press.Check() is { } e2) errors.Add($"Idle buttons / {name} press: {e2}");
            if (_idle[button].Double.Check() is { } e3) errors.Add($"Idle buttons / {name} double press: {e3}");
        }

        foreach (var ((app, field), tb) in _shortcuts)
        {
            var text = tb.Text.Trim();
            if (text.Length > 0 && !Hotkeys.IsValid(text)) errors.Add($"Meeting apps / {app} {field}: unknown key in '{text}'");
        }

        var presets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _presets.Rows)
        {
            if (row.IsNewRow) continue;
            var name = row.Cells[0].Value?.ToString()?.Trim() ?? "";
            var pattern = row.Cells[1].Value?.ToString()?.Trim() ?? "";
            if (name.Length == 0 && pattern.Length == 0) continue;
            if (name.Length == 0) { errors.Add($"Signals: a preset with pattern '{pattern}' has no name"); continue; }
            if (!LedPattern.TryParse(pattern, null, out _, out var perr)) { errors.Add($"Signals / {name}: {perr}"); continue; }
            presets[name] = pattern;
        }

        if (errors.Count > 0)
        {
            SetStatus(errors[0] + (errors.Count > 1 ? $"  (+{errors.Count - 1} more)" : ""));
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Settings - please fix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        _cfg.Mode = (ActivationMode)_mode.SelectedIndex;
        _cfg.MicLedFollowsSystemMute = _micLed.Checked;
        _cfg.VideoLedFollowsCamera = _videoLed.Checked;
        _cfg.MenuFontSize = (float)_menuFont.Value;
        _cfg.Editor = string.IsNullOrWhiteSpace(_editor.Text) ? null : _editor.Text.Trim();

        _cfg.Mic = _meeting[CollabButton.Mic].Result() ?? new ButtonAction();
        _cfg.Video = _meeting[CollabButton.Video].Result() ?? new ButtonAction();
        _cfg.Share = _meeting[CollabButton.Share].Result() ?? new ButtonAction();
        _cfg.Chat = _meeting[CollabButton.Chat].Result() ?? new ButtonAction();

        _cfg.Idle.Leds = (IdleLeds)_idleLeds.SelectedIndex;
        _cfg.Idle.DoublePressMs = (int)_doubleMs.Value;
        foreach (var (button, _) in Buttons)
        {
            var target = _cfg.Idle.For(button);
            target.Press = _idle[button].Press.Result() ?? new ButtonAction();
            target.Double = _idle[button].Double.Result();
        }

        foreach (var ((app, field), tb) in _shortcuts)
        {
            if (!_cfg.Apps.TryGetValue(app, out var s)) _cfg.Apps[app] = s = new AppShortcuts();
            var text = tb.Text.Trim();
            switch (field)
            {
                case "MicToggle": s.MicToggle = text; break;
                case "CameraToggle": s.CameraToggle = text; break;
                case "Share": s.Share = text; break;
                default: s.Chat = text; break;
            }
        }

        _cfg.Osd.Enabled = _osdEnabled.Checked;
        _cfg.Osd.DurationMs = (int)_osdMs.Value;
        _cfg.Osd.FontSize = (float)_osdFont.Value;

        _cfg.Signals.StickyTimeoutMs = (int)_stickyMs.Value;
        _cfg.Signals.Presets = presets;

        return _cfg;
    }

    void SetStatus(string text, bool ok = false)
    {
        _status.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
        _status.Text = text;
    }
}
