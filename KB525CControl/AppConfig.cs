using System.Text.Json;
using System.Text.Json.Serialization;

namespace KB525CControl;

public enum ActionKind
{
    /// <summary>Does nothing.</summary>
    None,
    /// <summary>Mutes/unmutes the Windows default microphone.</summary>
    ToggleMicMute,
    /// <summary>Sends a key combination to the focused window.</summary>
    Hotkey,
    /// <summary>Starts a program or command.</summary>
    Run,
    /// <summary>
    /// Mute: toggles the OS microphone (the source of the LED state) AND sends the focused
    /// meeting app's own mute shortcut so its UI matches. This avoids the "you are muted"
    /// warnings.
    /// </summary>
    SmartToggleMic,
    /// <summary>
    /// Camera: sends the focused meeting app's camera shortcut. The LED follows the actual
    /// camera usage, so it corrects itself.
    /// </summary>
    SmartToggleCamera,
    /// <summary>Sends the focused meeting app's "share" shortcut.</summary>
    SmartShare,
    /// <summary>Sends the focused meeting app's "chat" shortcut.</summary>
    SmartChat,
    /// <summary>
    /// Brings the main window of the given program to the front (Value = process name,
    /// e.g. "WindowsTerminal"), or launches it when it is not running.
    /// </summary>
    FocusOrLaunch,
    /// <summary>
    /// Starts a program (Value, Arguments) with the working directory set to the folder of
    /// the Explorer window in the foreground (falls back to the last Explorer window, then
    /// the user profile). E.g. Value "wt.exe", Arguments "-d ." opens a terminal there.
    /// </summary>
    RunInActiveFolder,
    /// <summary>Runs the Steps in order: hotkey / text / delay / run.</summary>
    Macro
}

/// <summary>How the buttons are activated.</summary>
public enum ActivationMode
{
    /// <summary>Meeting layer during a meeting, idle layer otherwise.</summary>
    Layered,
    /// <summary>Buttons live and lit only during a meeting; dark and dead otherwise.</summary>
    MeetingsOnly,
    /// <summary>The meeting layer applies all the time; buttons always lit.</summary>
    Always
}

/// <summary>Which buttons are lit in the idle layer.</summary>
public enum IdleLeds
{
    /// <summary>All dark (buttons still live).</summary>
    Off,
    /// <summary>White for the buttons that have an idle action.</summary>
    Assigned,
    /// <summary>All white.</summary>
    All
}

/// <summary>One step of a Macro action. Exactly one of the fields is used per step.</summary>
public sealed class MacroStep
{
    /// <summary>Key combination, e.g. "ctrl+l".</summary>
    public string? Hotkey { get; set; }
    /// <summary>Text typed as Unicode input; "\n" sends Enter.</summary>
    public string? Text { get; set; }
    /// <summary>Pause in milliseconds.</summary>
    public int? Delay { get; set; }
    /// <summary>Program to start (same rules as the Run action).</summary>
    public string? Run { get; set; }
    public string? Arguments { get; set; }
}

public sealed class ButtonAction
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionKind Kind { get; set; } = ActionKind.None;

    /// <summary>Hotkey: the combination ("ctrl+shift+m"). Run/RunInActiveFolder: the program. FocusOrLaunch: the process name.</summary>
    public string? Value { get; set; }

    /// <summary>Run/RunInActiveFolder: the arguments.</summary>
    public string? Arguments { get; set; }

    /// <summary>Macro: the steps.</summary>
    public List<MacroStep>? Steps { get; set; }

    [JsonIgnore] public bool IsSet => Kind != ActionKind.None;
}

/// <summary>Idle-layer binding of one button: a press action and an optional double-press action.</summary>
public sealed class IdleButton
{
    public ButtonAction Press { get; set; } = new();

    /// <summary>
    /// Optional. When set, a single press waits DoublePressMs to see whether a second press
    /// follows; buttons without a double action fire immediately.
    /// </summary>
    public ButtonAction? Double { get; set; }

    [JsonIgnore] public bool HasAction => Press.IsSet || (Double?.IsSet ?? false);
}

/// <summary>What the buttons do outside meetings (Layered mode).</summary>
public sealed class IdleLayer
{
    public IdleLeds Leds { get; set; } = IdleLeds.Assigned;

    /// <summary>Double-press window in ms (100-1000).</summary>
    public int DoublePressMs { get; set; } = 300;

    public IdleButton Mic { get; set; } = new() { Press = new ButtonAction { Kind = ActionKind.ToggleMicMute } };
    public IdleButton Video { get; set; } = new();
    public IdleButton Share { get; set; } = new()
    {
        Press = new ButtonAction { Kind = ActionKind.RunInActiveFolder, Value = "wt.exe", Arguments = "-d ." }
    };
    public IdleButton Chat { get; set; } = new();

    public IdleButton For(CollabButton button) => button switch
    {
        CollabButton.Mic => Mic,
        CollabButton.Video => Video,
        CollabButton.Share => Share,
        CollabButton.Chat => Chat,
        _ => new IdleButton()
    };
}

/// <summary>The on-screen popup shown when a button does something.</summary>
public sealed class OsdSettings
{
    public bool Enabled { get; set; } = true;
    public int DurationMs { get; set; } = 1200;
    public float FontSize { get; set; } = 14f;
}

/// <summary>LED signal settings (kb525c CLI / named pipe).</summary>
public sealed class SignalSettings
{
    /// <summary>Maximum lifetime of a sticky (infinite) signal in ms unless the caller says otherwise. 0 = unlimited.</summary>
    public int StickyTimeoutMs { get; set; } = 10 * 60 * 1000;

    /// <summary>
    /// User-defined named patterns. They may override the built-in ones (done, error,
    /// attention, running, chase, pulse, red, all). E.g. "build": "V---/-S--/--C-/---M@100x*".
    /// </summary>
    public Dictionary<string, string> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shortcuts of one meeting application. Empty string = the app has no such shortcut
/// (e.g. Google Meet has no share shortcut).
/// </summary>
public sealed class AppShortcuts
{
    public string MicToggle { get; set; } = "";
    public string CameraToggle { get; set; } = "";
    public string Share { get; set; } = "";
    public string Chat { get; set; } = "";

    public string For(ActionKind kind) => kind switch
    {
        ActionKind.SmartToggleMic => MicToggle,
        ActionKind.SmartToggleCamera => CameraToggle,
        ActionKind.SmartShare => Share,
        ActionKind.SmartChat => Chat,
        _ => ""
    };
}

public sealed class AppConfig
{
    public ActivationMode Mode { get; set; } = ActivationMode.Layered;

    /// <summary>Legacy (v0.3): replaced by Mode. Read for migration only, never written.</summary>
    public bool? OnlyDuringMeetings { get; set; }

    /// <summary>The Mic LED mirrors the Windows mute state.</summary>
    public bool MicLedFollowsSystemMute { get; set; } = true;

    /// <summary>The Video LED mirrors actual camera usage.</summary>
    public bool VideoLedFollowsCamera { get; set; } = true;

    // Meeting layer. Single press only - no double press during meetings, on purpose:
    // predictability and zero latency matter more there.
    public ButtonAction Mic { get; set; } = new() { Kind = ActionKind.SmartToggleMic };
    public ButtonAction Video { get; set; } = new() { Kind = ActionKind.SmartToggleCamera };
    public ButtonAction Share { get; set; } = new() { Kind = ActionKind.SmartShare };
    public ButtonAction Chat { get; set; } = new() { Kind = ActionKind.SmartChat };

    public IdleLayer Idle { get; set; } = new();

    public OsdSettings Osd { get; set; } = new();

    /// <summary>
    /// Per-application shortcuts. If one does not match your version, fix it here without
    /// recompiling.
    ///
    /// Google Meet has NO share shortcut, so it is empty. If you want the Share button to do
    /// something else there, use "ctrl+alt+h" (raise hand) or "ctrl+alt+p" (participants).
    /// </summary>
    public Dictionary<string, AppShortcuts> Apps { get; set; } = new()
    {
        [MeetingAppDetector.Meet] = new AppShortcuts
        {
            MicToggle = "ctrl+d",
            CameraToggle = "ctrl+e",
            Share = "",
            Chat = "ctrl+alt+c"
        },
        [MeetingAppDetector.Teams] = new AppShortcuts
        {
            MicToggle = "ctrl+shift+m",
            CameraToggle = "ctrl+shift+o",
            Share = "ctrl+shift+e",
            Chat = ""
        },
        [MeetingAppDetector.Zoom] = new AppShortcuts
        {
            MicToggle = "alt+a",
            CameraToggle = "alt+v",
            Share = "alt+s",
            Chat = "alt+h"
        }
    };

    public SignalSettings Signals { get; set; } = new();

    /// <summary>
    /// Editor used by "Open settings". Empty = auto: Notepad++ when installed, otherwise the
    /// Windows default for .json. Example: "C:\\Program Files\\Notepad++\\notepad++.exe" or "code".
    /// </summary>
    public string? Editor { get; set; }

    /// <summary>Font size (points) of the tray menu. Default 11; the WinForms default would be 9.</summary>
    public float MenuFontSize { get; set; } = 11f;

    public AppShortcuts? ShortcutsFor(string appKey) =>
        Apps.TryGetValue(appKey, out var s) ? s : null;

    /// <summary>Meeting-layer action of a button.</summary>
    public ButtonAction For(CollabButton button) => button switch
    {
        CollabButton.Mic => Mic,
        CollabButton.Video => Video,
        CollabButton.Share => Share,
        CollabButton.Chat => Chat,
        _ => new ButtonAction()
    };

    // --- load / save --------------------------------------------------------------

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        // Otherwise '+' is written as +, which is confusing when editing by hand.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KB525CControl", "config.json");

    /// <summary>Reads a settings file without touching it (no migration write, no default file). Null with an error when unreadable.</summary>
    public static AppConfig? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (cfg is null) { error = "the file is empty"; return null; }
            cfg.Migrate(json);
            return cfg;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Deep copy (JSON round trip) - the settings window edits a copy.</summary>
    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Options), Options) ?? new AppConfig();

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
                if (cfg.Migrate(json)) cfg.Save(path);
                return cfg;
            }
        }
        catch
        {
            // A corrupt config is better replaced by the defaults than not starting at all.
        }

        var fresh = new AppConfig();
        fresh.Save(path);
        return fresh;
    }

    /// <summary>Upgrades older config files in place. True when something changed.</summary>
    bool Migrate(string json)
    {
        var changed = false;

        // v0.3: "onlyDuringMeetings" instead of "mode". true -> Layered (the idle layer is the
        // new feature; its defaults are mild), false -> Always.
        if (OnlyDuringMeetings is bool legacy)
        {
            if (!json.Contains("\"mode\"", StringComparison.OrdinalIgnoreCase))
                Mode = legacy ? ActivationMode.Layered : ActivationMode.Always;
            OnlyDuringMeetings = null;
            changed = true;
        }

        return changed;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
        }
    }
}
