using System.Text;

namespace KB525CControl.Settings;

/// <summary>
/// Edits one <see cref="ButtonAction"/>: the kind, its value/arguments and - for macros -
/// the steps, with a short description of the selected kind underneath.
/// </summary>
public sealed class ActionEditor : UserControl
{
    static readonly (ActionKind Kind, string Label, string Help)[] Kinds =
    {
        (ActionKind.None, "None",
            "Nothing happens. In the idle layer the button stays dark (with LEDs = Assigned)."),
        (ActionKind.SmartToggleMic, "Smart mic toggle (meeting)",
            "Mutes/unmutes the Windows microphone AND sends the mute shortcut of the meeting app in front (Meet, Teams, Zoom) so its UI matches. The Mic LED turns red while muted."),
        (ActionKind.SmartToggleCamera, "Smart camera toggle (meeting)",
            "Sends the camera shortcut of the meeting app in front. The Video LED follows real camera usage, so it corrects itself."),
        (ActionKind.SmartShare, "Smart share (meeting)",
            "Sends the share-screen shortcut of the meeting app in front. Google Meet has no such shortcut - set one under Meeting apps (e.g. ctrl+alt+h = raise hand) if you want the button to do something there."),
        (ActionKind.SmartChat, "Smart chat (meeting)",
            "Sends the chat shortcut of the meeting app in front."),
        (ActionKind.ToggleMicMute, "Toggle Windows microphone mute",
            "Mutes/unmutes the Windows default microphone. Works with every application (Discord, games, dictation); the Mic LED turns red while muted."),
        (ActionKind.Hotkey, "Send a hotkey",
            "Sends a key combination to the window in front, e.g. ctrl+shift+m. Keys: ctrl, shift, alt, win, a-z, 0-9, f1-f24, tab, enter, esc, space, backspace, delete, left/right/up/down, printscreen."),
        (ActionKind.Run, "Run a program",
            "Starts a program, script, document or URL. .ps1 runs with PowerShell, .bat/.cmd with cmd, everything else the way Explorer would open it. The program receives KB525C_* environment variables (button, layer, active folder...)."),
        (ActionKind.RunInActiveFolder, "Run a program in the active folder",
            "Like Run, but the working directory is the folder you are in: Total Commander's active panel, or the Explorer window in front (then the last Explorer window, then your profile). Example: program wt.exe, arguments -d .  opens a terminal there."),
        (ActionKind.FocusOrLaunch, "Focus or launch a program",
            "Brings the program's main window to the front, or starts it when it is not running. Value = process name (WindowsTerminal, Code, notepad++, chrome...) or a command. The best choice for single-instance programs: pressing again while it runs brings its window forward."),
        (ActionKind.Macro, "Macro (several steps)",
            "Runs the steps in order, one per line:\n  hotkey ctrl+l\n  text hello\\n        (\\n = Enter, \\t = Tab)\n  delay 300            (milliseconds)\n  run wt.exe -d .      (program, then arguments; quote paths with spaces)"),
    };

    readonly WheelSafeComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, DropDownWidth = 420 };
    readonly Label _help = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(560, 0), Margin = new Padding(0, 2, 0, 8) };
    readonly Label _valueLabel = new() { AutoSize = true, Text = "Value", Margin = new Padding(0, 6, 8, 0) };
    readonly TextBox _value = new() { Width = 420 };
    readonly Label _argsLabel = new() { AutoSize = true, Text = "Arguments", Margin = new Padding(0, 6, 8, 0) };
    readonly TextBox _args = new() { Width = 420 };
    readonly Label _stepsLabel = new() { AutoSize = true, Text = "Steps", Margin = new Padding(0, 6, 8, 0) };
    readonly TextBox _steps = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Width = 420, Height = 90,
        Font = new Font("Consolas", 9.5f, FontStyle.Regular, GraphicsUnit.Point)
    };

    readonly TableLayoutPanel _grid = new() { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top };

    /// <summary>Optional caption shown above the kind selector ("Press", "Double press").</summary>
    public ActionEditor(string? caption = null)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0, 0, 0, 6);

        foreach (var (_, label, _) in Kinds) _kind.Items.Add(label);
        _kind.SelectedIndexChanged += (_, _) => UpdateVisibility();

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        if (caption is not null)
        {
            var cap = new Label { AutoSize = true, Text = caption, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) };
            _grid.Controls.Add(cap, 0, row);
            _grid.SetColumnSpan(cap, 2);
            row++;
        }

        _grid.Controls.Add(new Label { AutoSize = true, Text = "Action", Margin = new Padding(0, 6, 8, 0) }, 0, row);
        _grid.Controls.Add(_kind, 1, row++);
        _grid.Controls.Add(_help, 1, row++);
        _grid.Controls.Add(_valueLabel, 0, row);
        _grid.Controls.Add(_value, 1, row++);
        _grid.Controls.Add(_argsLabel, 0, row);
        _grid.Controls.Add(_args, 1, row++);
        _grid.Controls.Add(_stepsLabel, 0, row);
        _grid.Controls.Add(_steps, 1, row++);

        Controls.Add(_grid);
        _kind.SelectedIndex = 0;
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ActionKind Kind
    {
        get => Kinds[Math.Max(0, _kind.SelectedIndex)].Kind;
        set => _kind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == value));
    }

    /// <summary>Loads an action; null means "None".</summary>
    public void LoadAction(ButtonAction? action)
    {
        Kind = action?.Kind ?? ActionKind.None;
        _value.Text = action?.Value ?? "";
        _args.Text = action?.Arguments ?? "";
        _steps.Text = FormatSteps(action?.Steps);
        UpdateVisibility();
    }

    /// <summary>The edited action; null when the kind is None.</summary>
    public ButtonAction? Result()
    {
        var kind = Kind;
        if (kind == ActionKind.None) return null;

        var a = new ButtonAction { Kind = kind };
        switch (kind)
        {
            case ActionKind.Hotkey:
            case ActionKind.FocusOrLaunch:
                a.Value = Blank(_value.Text);
                break;
            case ActionKind.Run:
            case ActionKind.RunInActiveFolder:
                a.Value = Blank(_value.Text);
                a.Arguments = Blank(_args.Text);
                break;
            case ActionKind.Macro:
                a.Steps = ParseSteps(_steps.Text, out _);
                break;
        }
        return a;
    }

    /// <summary>Returns a problem description, or null when the action is usable.</summary>
    public string? Check()
    {
        switch (Kind)
        {
            case ActionKind.Hotkey:
                if (string.IsNullOrWhiteSpace(_value.Text)) return "hotkey is empty";
                if (!Hotkeys.IsValid(_value.Text)) return $"unknown key in hotkey '{_value.Text.Trim()}'";
                return null;
            case ActionKind.Run:
            case ActionKind.RunInActiveFolder:
                return string.IsNullOrWhiteSpace(_value.Text) ? "program is empty" : null;
            case ActionKind.FocusOrLaunch:
                return string.IsNullOrWhiteSpace(_value.Text) ? "process name is empty" : null;
            case ActionKind.Macro:
            {
                var steps = ParseSteps(_steps.Text, out var error);
                if (error is not null) return error;
                return steps.Count == 0 ? "macro has no steps" : null;
            }
            default:
                return null;
        }
    }

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    void UpdateVisibility()
    {
        var (kind, _, help) = Kinds[Math.Max(0, _kind.SelectedIndex)];
        _help.Text = help;

        var showValue = kind is ActionKind.Hotkey or ActionKind.Run or ActionKind.RunInActiveFolder or ActionKind.FocusOrLaunch;
        var showArgs = kind is ActionKind.Run or ActionKind.RunInActiveFolder;
        var showSteps = kind is ActionKind.Macro;

        _valueLabel.Text = kind switch
        {
            ActionKind.Hotkey => "Hotkey",
            ActionKind.FocusOrLaunch => "Process / command",
            _ => "Program"
        };

        _valueLabel.Visible = _value.Visible = showValue;
        _argsLabel.Visible = _args.Visible = showArgs;
        _stepsLabel.Visible = _steps.Visible = showSteps;
    }

    // --- macro steps <-> text ------------------------------------------------------

    public static string FormatSteps(List<MacroStep>? steps)
    {
        if (steps is null || steps.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var s in steps)
        {
            if (s.Hotkey is not null) sb.Append("hotkey ").Append(s.Hotkey);
            else if (s.Text is not null) sb.Append("text ").Append(s.Text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t"));
            else if (s.Delay is not null) sb.Append("delay ").Append(s.Delay.Value);
            else if (s.Run is not null)
            {
                sb.Append("run ");
                sb.Append(s.Run.Contains(' ') ? $"\"{s.Run}\"" : s.Run);
                if (!string.IsNullOrEmpty(s.Arguments)) sb.Append(' ').Append(s.Arguments);
            }
            else continue;
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static List<MacroStep> ParseSteps(string text, out string? error)
    {
        error = null;
        var steps = new List<MacroStep>();
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var space = line.IndexOf(' ');
            var verb = (space < 0 ? line : line[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : line[(space + 1)..].Trim();

            switch (verb)
            {
                case "hotkey":
                    if (!Hotkeys.IsValid(rest)) { error = $"macro line {lineNo}: unknown key in '{rest}'"; return steps; }
                    steps.Add(new MacroStep { Hotkey = rest });
                    break;
                case "text":
                    steps.Add(new MacroStep { Text = Unescape(rest) });
                    break;
                case "delay":
                    if (!int.TryParse(rest, out var ms) || ms < 0) { error = $"macro line {lineNo}: delay needs a number of milliseconds"; return steps; }
                    steps.Add(new MacroStep { Delay = ms });
                    break;
                case "run":
                {
                    if (rest.Length == 0) { error = $"macro line {lineNo}: run needs a program"; return steps; }
                    string program, args;
                    if (rest[0] == '"' && rest.IndexOf('"', 1) is var close and > 0)
                    {
                        program = rest[1..close];
                        args = rest[(close + 1)..].Trim();
                    }
                    else
                    {
                        var sp = rest.IndexOf(' ');
                        program = sp < 0 ? rest : rest[..sp];
                        args = sp < 0 ? "" : rest[(sp + 1)..].Trim();
                    }
                    steps.Add(new MacroStep { Run = program, Arguments = args.Length == 0 ? null : args });
                    break;
                }
                default:
                    error = $"macro line {lineNo}: expected hotkey / text / delay / run, got '{verb}'";
                    return steps;
            }
        }
        return steps;
    }

    static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 't') { sb.Append('\t'); i++; continue; }
                if (n == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
