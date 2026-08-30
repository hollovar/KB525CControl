using System.Diagnostics;
using System.Drawing.Drawing2D;
using KB525CControl.Ipc;
using Microsoft.Win32;

namespace KB525CControl;

/// <summary>Which set of button bindings is live right now.</summary>
public enum Layer { Off, Meeting, Idle }

/// <summary>
/// The tray application. Wires together the keyboard, meeting detection, the two button
/// layers, LED signals and external control (named pipe).
///
/// How it works:
///   - meeting layer while any application uses the microphone: Mic mutes the Windows
///     microphone (the LED shows the real state), Video/Share/Chat drive the meeting app
///   - idle layer otherwise (Layered mode): launchers, macros, hotkeys; optional double press
///   - external programs (kb525c CLI, Claude Code hooks, scripts) can request LED signals;
///     any collab button acknowledges a sticky signal
///   - optional LED sleep in the idle layer: after N seconds without mouse/keyboard input
///     the LEDs go dark; a signal or a button press wakes them (that press does nothing else)
///   - if the keyboard is missing, is a different model, or its channel is busy, a tray
///     balloon says so and the icon turns grey
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    readonly Kb525cDevice _device = new();
    readonly MediaUsageMonitor _media = new();
    readonly MicMute _mic = new();
    readonly InputIdleMonitor _inputIdle = new();
    readonly SignalEngine _signals;
    readonly PipeServer _ipc;
    readonly NotifyIcon _tray;
    readonly List<string> _logLines = new();
    readonly Dictionary<CollabButton, System.Windows.Forms.Timer> _pendingPress = new();

    /// <summary>Hidden control, only so there is something to Invoke onto the UI thread.</summary>
    readonly Control _ui = new();

    AppConfig _config;
    Icon? _currentIcon;
    bool _hadDeviceError;
    bool _ledsAsleep;
    bool _signalWasPlaying;
    string _wakeReason = "mouse/keyboard";
    OsdForm? _osd;
    LogForm? _logForm;
    Settings.SettingsForm? _settingsForm;
    CollabLed _baseLeds;

    public static string Version =>
        typeof(TrayApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public TrayApp()
    {
        _ = _ui.Handle;   // reading Handle creates the window so Invoke works from now on
        _config = AppConfig.Load();

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "KB525C Control",
            ContextMenuStrip = BuildMenu()
        };
        UpdateIcon();

        // Left click opens the same menu as right click.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenu();
        };

        _device.ButtonPressed += OnButtonPressed;
        _device.ConnectionChanged += _ => BeginInvokeUi(Refresh);
        _device.ErrorChanged += err => BeginInvokeUi(() => OnDeviceError(err));
        _device.Log += line => BeginInvokeUi(() => AddLog(line));

        _media.Changed += (_, _) => BeginInvokeUi(Refresh);
        _inputIdle.Changed += _ => BeginInvokeUi(Refresh);

        _signals = new SignalEngine(_device);
        _signals.Log += line => BeginInvokeUi(() => AddLog(line));
        _signals.StateChanged += () => BeginInvokeUi(OnSignalStateChanged);

        _ipc = new PipeServer(HandleIpc);
        _ipc.Log += line => BeginInvokeUi(() => AddLog(line));

        // HID handles die across sleep, so reconnect on resume.
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) _device.Reconnect();
        };

        _signals.Start();
        _media.Start();
        _inputIdle.Start();
        _device.Start();
        _ipc.Start();
        Refresh();
        AddLog($"KB525C Control {Version} started ({_config.Mode})");
    }

    // --- state -----------------------------------------------------------------

    Layer CurrentLayer
    {
        get
        {
            var meeting = _media.MicInUse;
            return _config.Mode switch
            {
                ActivationMode.Always => Layer.Meeting,
                ActivationMode.MeetingsOnly => meeting ? Layer.Meeting : Layer.Off,
                _ => meeting ? Layer.Meeting : Layer.Idle
            };
        }
    }

    void Refresh()
    {
        var layer = CurrentLayer;
        var muted = _config.MicLedFollowsSystemMute && (_mic.IsMuted ?? false);
        _inputIdle.ThresholdMs = Math.Max(0, _config.Idle.SleepAfterSeconds) * 1000;

        bool enabled;
        CollabLed leds;
        switch (layer)
        {
            case Layer.Off:
                enabled = false;
                leds = CollabLed.None;
                break;

            case Layer.Meeting:
            {
                var cameraOn = !_config.VideoLedFollowsCamera || _media.CameraInUse;
                enabled = true;
                leds = CollabLed.ShareWhite | CollabLed.ChatWhite
                       | (muted ? CollabLed.MicRed : CollabLed.MicWhite)
                       | (cameraOn ? CollabLed.VideoWhite : CollabLed.VideoRed);
                break;
            }

            default:
                enabled = true;
                leds = ComputeIdleLeds(muted);
                break;
        }

        // LED sleep: idle layer only, never while a signal plays. The buttons stay enabled
        // (a disabled row would not report presses), only the LEDs go dark.
        var asleep = layer == Layer.Idle && _config.Idle.SleepAfterSeconds > 0
                     && _signals.Playing is null && _inputIdle.IsIdle;
        if (asleep != _ledsAsleep)
        {
            _ledsAsleep = asleep;
            var after = _config.Idle.SleepAfterSeconds;
            AddLog(asleep
                ? $"LEDs asleep ({(after % 60 == 0 ? $"{after / 60} min" : $"{after} s")} without input)"
                : $"LEDs awake ({_wakeReason})");
        }
        _wakeReason = "mouse/keyboard";
        if (asleep) leds = CollabLed.None;

        _baseLeds = enabled ? leds : CollabLed.None;

        // The signal engine is the only writer to the device; this is just the base state
        // it shows when no signal is playing.
        _signals.SetBase(enabled, leds);
        UpdateIcon();
        UpdateTooltip();
    }

    CollabLed ComputeIdleLeds(bool muted)
    {
        var idle = _config.Idle;
        bool Lit(CollabButton b) =>
            idle.Leds == IdleLeds.All || (idle.Leds == IdleLeds.Assigned && idle.For(b).HasAction);

        var leds = CollabLed.None;
        if (Lit(CollabButton.Video)) leds |= CollabLed.VideoWhite;
        if (Lit(CollabButton.Share)) leds |= CollabLed.ShareWhite;
        if (Lit(CollabButton.Chat)) leds |= CollabLed.ChatWhite;
        if (Lit(CollabButton.Mic)) leds |= muted ? CollabLed.MicRed : CollabLed.MicWhite;
        return leds;
    }

    /// <summary>
    /// A signal started or ended. The end of a signal counts as activity, so the sleep
    /// countdown starts over; a signal that starts while asleep is what woke the LEDs.
    /// </summary>
    void OnSignalStateChanged()
    {
        var playing = _signals.Playing is not null;
        if (_signalWasPlaying && !playing) _inputIdle.Touch();
        else if (playing && _ledsAsleep) _wakeReason = "signal";
        _signalWasPlaying = playing;
        Refresh();
    }

    /// <summary>Keyboard missing / wrong model / channel busy -> balloon; recovered -> balloon.</summary>
    void OnDeviceError(string? error)
    {
        if (error is not null)
        {
            _hadDeviceError = true;
            ShowBalloon(error, ToolTipIcon.Warning);
        }
        else if (_hadDeviceError)
        {
            _hadDeviceError = false;
            ShowBalloon("Dell KB525C connected.", ToolTipIcon.Info);
        }
        Refresh();
    }

    void ShowBalloon(string text, ToolTipIcon icon)
    {
        try { _tray.ShowBalloonTip(5000, "KB525C Control", text, icon); } catch { }
    }

    // --- button handling ----------------------------------------------------------

    void OnButtonPressed(CollabButton button)
    {
        BeginInvokeUi(() =>
        {
            var layer = CurrentLayer;
            AddLog($"{button} pressed ({layer})");

            // The OS does not see collab presses as input; count them for the LED sleep.
            var wasAsleep = _ledsAsleep;
            _inputIdle.Touch();

            // While the row is blinking, a press only means "seen": the signal stops, a
            // popup says what it was about, the signal's ack action (if any) runs - and the
            // button's normal function does NOT fire. That is what people press for.
            if (_signals.TryAck(out var acknowledged) && acknowledged is not null)
            {
                var why = string.IsNullOrWhiteSpace(acknowledged.Label)
                    ? $"Signal: {acknowledged.Pattern.Source}"
                    : acknowledged.Label;
                Osd(why, 2000);
                if (!string.IsNullOrWhiteSpace(acknowledged.OnAck))
                    RunAction(ParseAckAction(acknowledged.OnAck), button, layer, "ack");
                Refresh();
                return;
            }

            // Asleep: the press only wakes the LEDs; the button's function does NOT fire.
            if (wasAsleep)
            {
                AddLog("  (LEDs were asleep - woke them, no action)");
                _wakeReason = $"{button} button";
                Osd("LEDs woke up", 1200);
                Refresh();
                return;
            }

            switch (layer)
            {
                case Layer.Off:
                    AddLog("  (buttons are inactive - no meeting)");
                    break;
                case Layer.Meeting:
                    RunAction(_config.For(button), button, layer, "press");
                    break;
                default:
                    HandleIdlePress(button);
                    break;
            }
            Refresh();
        });
    }

    /// <summary>
    /// Idle layer: buttons without a double action fire at once; buttons with one wait the
    /// double-press window for a second press.
    /// </summary>
    void HandleIdlePress(CollabButton button)
    {
        var binding = _config.Idle.For(button);

        if (binding.Double is null || !binding.Double.IsSet)
        {
            RunAction(binding.Press, button, Layer.Idle, "press");
            return;
        }

        if (_pendingPress.Remove(button, out var pending))
        {
            pending.Stop();
            pending.Dispose();
            RunAction(binding.Double, button, Layer.Idle, "double");
            return;
        }

        var timer = new System.Windows.Forms.Timer
        {
            Interval = Math.Clamp(_config.Idle.DoublePressMs, 100, 1000)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            _pendingPress.Remove(button);
            RunAction(binding.Press, button, Layer.Idle, "press");
            Refresh();
        };
        _pendingPress[button] = timer;
        timer.Start();
    }

    sealed record ActionContext(CollabButton Button, Layer Layer, string Press);

    void RunAction(ButtonAction action, CollabButton button, Layer layer, string press)
    {
        var summary = Execute(action, new ActionContext(button, layer, press));
        if (summary is not null)
            Osd(press == "double" ? $"{button} ×2 · {summary}" : $"{button} · {summary}");
    }

    /// <summary>Runs an action and returns a short summary for the OSD (null = show nothing).</summary>
    string? Execute(ButtonAction action, ActionContext ctx)
    {
        switch (action.Kind)
        {
            case ActionKind.ToggleMicMute:
            {
                var state = _mic.Toggle();
                var text = state is null ? "no microphone available" : state.Value ? "muted" : "mic live";
                AddLog("  " + text);
                return text;
            }

            case ActionKind.SmartToggleMic:
            {
                // The OS mute is the source of the LED, because we can always read it.
                var next = _mic.Toggle();
                var text = next is null ? "no microphone available" : next.Value ? "muted" : "mic live";
                AddLog("  " + text);

                // The app UI should follow, otherwise it shows "you are muted" warnings.
                if (!SendAppShortcut(ActionKind.SmartToggleMic, quiet: true, out var app))
                    AddLog("  the app UI did not follow (not focused / no shortcut)");
                else
                    text += $" ({app})";
                return text;
            }

            case ActionKind.SmartToggleCamera:
            case ActionKind.SmartShare:
            case ActionKind.SmartChat:
            {
                var what = action.Kind switch
                {
                    ActionKind.SmartToggleCamera => "camera",
                    ActionKind.SmartShare => "share",
                    _ => "chat"
                };
                return SendAppShortcut(action.Kind, quiet: false, out var app)
                    ? $"{what} ({app})"
                    : app is null ? $"{what}: no meeting app in front" : $"{what}: no shortcut for {app}";
            }

            case ActionKind.Hotkey:
                if (string.IsNullOrWhiteSpace(action.Value)) { AddLog("  empty hotkey"); return "empty hotkey"; }
                if (Hotkeys.Send(action.Value)) { AddLog($"  hotkey: {action.Value}"); return action.Value; }
                AddLog($"  invalid hotkey: {action.Value}");
                return $"invalid hotkey {action.Value}";

            case ActionKind.Run:
            case ActionKind.RunInActiveFolder:
            {
                if (string.IsNullOrWhiteSpace(action.Value)) { AddLog("  empty command"); return "empty command"; }

                string? folder = null;
                if (action.Kind == ActionKind.RunInActiveFolder)
                {
                    folder = ActiveFolder.Detect(out var source);
                    AddLog($"  active folder: {folder} ({source})");
                }

                try
                {
                    var before = WindowFocus.Snapshot();
                    var what = ProcessLauncher.Start(action.Value, action.Arguments, folder, BuildEnvironment(ctx, folder));
                    AddLog($"  started: {what}");
                    FocusAfterLaunch(before, WindowFocus.ProcessNameFor(action.Value));
                    return what;
                }
                catch (Exception ex)
                {
                    AddLog($"  start failed: {ex.Message}");
                    return $"start failed: {ex.Message}";
                }
            }

            case ActionKind.FocusOrLaunch:
            {
                if (string.IsNullOrWhiteSpace(action.Value)) { AddLog("  empty process name"); return "empty process name"; }
                var before = WindowFocus.Snapshot();
                WindowFocus.FocusOrLaunch(action.Value, out var what, out var launched);
                AddLog("  " + what);
                if (launched) FocusAfterLaunch(before, WindowFocus.ProcessNameFor(action.Value));
                return what;
            }

            case ActionKind.Macro:
            {
                var steps = action.Steps;
                if (steps is null || steps.Count == 0) { AddLog("  macro without steps"); return "macro without steps"; }
                var env = BuildEnvironment(ctx, null);
                _ = RunMacroAsync(steps, env);
                AddLog($"  macro: {steps.Count} step(s)");
                return $"macro ({steps.Count} steps)";
            }

            default:
                AddLog($"  no action assigned to the {ctx.Button} button ({ctx.Layer})");
                return null;
        }
    }

    /// <summary>
    /// A program started by the tray app gets its window, but Windows does not activate it:
    /// the launcher (this app) is not the foreground process and a HID report is not user
    /// input, so the new window only flashes on the taskbar. Wait for the window that was not
    /// in <paramref name="before"/> and bring it to the front the same way FocusOrLaunch does.
    /// When no new window appears (single-instance program handed the request to its running
    /// copy, terminal opened a tab in an existing window), fall back to the process by name.
    /// </summary>
    void FocusAfterLaunch(HashSet<IntPtr> before, string? processName)
    {
        const int timeoutMs = 6000;
        _ = Task.Run(() =>
        {
            var hwnd = WindowFocus.WaitForNewWindow(before, timeoutMs);
            BeginInvokeUi(() =>
            {
                if (hwnd != IntPtr.Zero)
                {
                    if (!WindowFocus.StillExists(hwnd)) return;
                    if (WindowFocus.IsForeground(hwnd)) { AddLog("  new window is in front"); return; }
                    AddLog(WindowFocus.Force(hwnd)
                        ? "  brought the new window to the front"
                        : "  could not bring the new window to the front");
                    return;
                }

                if (string.IsNullOrEmpty(processName)) return;
                AddLog(WindowFocus.BringToFront(processName)
                    ? $"  no new window within {timeoutMs / 1000} s; brought {processName} to the front"
                    : $"  no new window within {timeoutMs / 1000} s (and no {processName} window to bring to the front)");
            });
        });
    }

    async Task RunMacroAsync(List<MacroStep> steps, IReadOnlyDictionary<string, string> env)
    {
        foreach (var step in steps)
        {
            try
            {
                if (step.Delay is > 0) { await Task.Delay(step.Delay.Value); continue; }
                if (!string.IsNullOrEmpty(step.Hotkey)) Hotkeys.Send(step.Hotkey);
                else if (!string.IsNullOrEmpty(step.Text)) Hotkeys.TypeText(step.Text);
                else if (!string.IsNullOrEmpty(step.Run)) ProcessLauncher.Start(step.Run, step.Arguments, null, env);
                await Task.Delay(30);
            }
            catch (Exception ex)
            {
                BeginInvokeUi(() => AddLog($"  macro step failed: {ex.Message}"));
            }
        }
    }

    /// <summary>Environment variables handed to launched programs so scripts know who called them.</summary>
    IReadOnlyDictionary<string, string> BuildEnvironment(ActionContext ctx, string? folder) =>
        new Dictionary<string, string>
        {
            ["KB525C_BUTTON"] = ctx.Button.ToString().ToLowerInvariant(),
            ["KB525C_PRESS"] = ctx.Press,
            ["KB525C_LAYER"] = ctx.Layer.ToString().ToLowerInvariant(),
            ["KB525C_ACTIVE_FOLDER"] = folder ?? "",
            ["KB525C_MIC_MUTED"] = (_mic.IsMuted ?? false) ? "1" : "0",
            ["KB525C_MEETING"] = _media.MicInUse ? "1" : "0"
        };

    /// <summary>
    /// Ack action from text: "focus:WindowsTerminal", "run:C:\x.ps1", "hotkey:ctrl+alt+t".
    /// Without a prefix: focus.
    /// </summary>
    static ButtonAction ParseAckAction(string text)
    {
        text = text.Trim();
        var idx = text.IndexOf(':');
        var prefix = idx < 0 ? "focus" : text[..idx].Trim().ToLowerInvariant();
        var value = idx < 0 ? text : text[(idx + 1)..].Trim();

        switch (prefix)
        {
            case "focus":
                return new ButtonAction { Kind = ActionKind.FocusOrLaunch, Value = value };
            case "hotkey":
                return new ButtonAction { Kind = ActionKind.Hotkey, Value = value };
            case "run":
            {
                // "run:pwsh.exe -File x.ps1" -> program + arguments, unless the whole thing is an existing file.
                if (!File.Exists(value) && value.Contains(' '))
                {
                    var sp = value.IndexOf(' ');
                    return new ButtonAction { Kind = ActionKind.Run, Value = value[..sp], Arguments = value[(sp + 1)..] };
                }
                return new ButtonAction { Kind = ActionKind.Run, Value = value };
            }
            default:
                if (Enum.TryParse<ActionKind>(prefix, ignoreCase: true, out var kind))
                    return new ButtonAction { Kind = kind, Value = value };
                // Unknown prefix: treat the whole thing as a process name / URI (e.g. "ms-teams:").
                return new ButtonAction { Kind = ActionKind.FocusOrLaunch, Value = text };
        }
    }

    /// <summary>
    /// Sends the matching shortcut of the focused meeting app. False when no app is
    /// recognised (app = null) or it has no such shortcut (app = its name).
    /// </summary>
    bool SendAppShortcut(ActionKind kind, bool quiet, out string? app)
    {
        app = null;
        var appKey = MeetingAppDetector.Detect();
        if (appKey is null)
        {
            if (!quiet) AddLog("  no meeting app in the foreground");
            return false;
        }

        app = MeetingAppDetector.DisplayName(appKey);
        var combo = _config.ShortcutsFor(appKey)?.For(kind) ?? "";

        if (string.IsNullOrWhiteSpace(combo))
        {
            if (!quiet) AddLog($"  {app}: no shortcut for this (set it in the apps section of config.json)");
            return false;
        }

        if (Hotkeys.Send(combo))
        {
            if (!quiet) AddLog($"  {app}: {combo}");
            return true;
        }

        if (!quiet) AddLog($"  {app}: invalid combination: {combo}");
        return false;
    }

    // --- OSD -------------------------------------------------------------------

    void Osd(string text, int? durationMs = null)
    {
        if (!_config.Osd.Enabled) return;
        try
        {
            _osd ??= new OsdForm();
            _osd.ShowMessage(text, durationMs ?? _config.Osd.DurationMs, _config.Osd.FontSize);
        }
        catch (Exception ex)
        {
            AddLog($"OSD failed: {ex.Message}");
        }
    }

    // --- signals ---------------------------------------------------------------

    /// <summary>Starts a signal by name or pattern text. Null + error message on failure.</summary>
    SignalEngine.Signal? PlaySignal(string patternText, string? label, int priority, string? ack,
        int? timeoutMs, bool forceSticky, out string? error)
    {
        if (!LedPattern.TryParse(patternText, _config.Signals.Presets, out var pattern, out error) || pattern is null)
            return null;

        if (forceSticky && !pattern.IsInfinite) pattern = pattern.WithRepeat(-1);

        var timeout = timeoutMs ?? (pattern.IsInfinite ? _config.Signals.StickyTimeoutMs : 0);
        var sig = _signals.Play(pattern, label, priority, ack, timeout);
        if (sig is null) error = "dropped: a higher-priority sticky signal is active";
        return sig;
    }

    IReadOnlyDictionary<string, string> AllPatterns()
    {
        var all = new Dictionary<string, string>(LedPattern.Builtin, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _config.Signals.Presets)
            if (!string.IsNullOrWhiteSpace(v)) all[k] = v;
        return all;
    }

    // --- IPC -------------------------------------------------------------------

    /// <summary>Called on the pipe server thread; everything is marshalled to the UI thread.</summary>
    IpcResponse HandleIpc(IpcRequest req)
    {
        if (!_ui.IsHandleCreated) return IpcResponse.Fail("the app is still starting");
        try
        {
            return (IpcResponse)_ui.Invoke(() => HandleIpcOnUi(req));
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    IpcResponse HandleIpcOnUi(IpcRequest req)
    {
        switch (req.Cmd.Trim().ToLowerInvariant())
        {
            case "ping":
                return IpcResponse.Success("pong");

            case "status":
                return new IpcResponse { Ok = true, Status = BuildStatus() };

            case "patterns":
                return new IpcResponse
                {
                    Ok = true,
                    Patterns = AllPatterns().ToDictionary(p => p.Key, p => p.Value)
                };

            case "reload":
                ReloadConfig("IPC");
                return IpcResponse.Success("config reloaded");

            case "settings":
                ShowSettings();
                return IpcResponse.Success("settings window opened");

            case "log":
            {
                var tail = Math.Clamp(req.Tail ?? 40, 1, 500);
                var lines = _logLines.Skip(Math.Max(0, _logLines.Count - tail));
                return IpcResponse.Success(string.Join("\n", lines));
            }

            case "off":
            {
                var n = _signals.Cancel(req.Id);
                AddLog(req.Id is null ? $"IPC: all signals stopped ({n})" : $"IPC: signal #{req.Id} stopped ({n})");
                return IpcResponse.Success($"{n} signal(s) stopped");
            }

            case "signal":
            {
                if (string.IsNullOrWhiteSpace(req.Pattern))
                    return IpcResponse.Fail("missing pattern");

                var sig = PlaySignal(req.Pattern, req.Label, req.Priority ?? 0, req.Ack,
                    req.TimeoutMs, req.Sticky == true, out var error);
                if (sig is null) return IpcResponse.Fail(error ?? "unknown error");

                AddLog($"IPC signal: {sig}");
                return IpcResponse.Success($"signal #{sig.Id}{(sig.Sticky ? " (sticky)" : "")}: {sig.Pattern.Source}", sig.Id);
            }

            case "press":
            {
                if (!Enum.TryParse<CollabButton>(req.Button ?? "", ignoreCase: true, out var button)
                    || button == CollabButton.None)
                    return IpcResponse.Fail("button must be one of: mic, video, share, chat");
                AddLog($"IPC: simulated {button} press");
                var wakesOnly = _ledsAsleep;
                OnButtonPressed(button);
                return IpcResponse.Success($"{button} pressed ({CurrentLayer}){(wakesOnly ? " - woke the LEDs, no action" : "")}");
            }

            default:
                return IpcResponse.Fail($"unknown command: {req.Cmd}");
        }
    }

    IpcStatus BuildStatus()
    {
        var fg = MeetingAppDetector.Detect();
        var layer = CurrentLayer;
        return new IpcStatus
        {
            AppVersion = Version,
            Connected = _device.IsConnected,
            DeviceError = _device.LastError,
            Mode = ModeName(_config.Mode),
            Layer = layer.ToString().ToLowerInvariant(),
            ButtonsActive = layer != Layer.Off,
            MicInUse = _media.MicInUse,
            CameraInUse = _media.CameraInUse,
            Muted = _mic.IsMuted,
            Leds = LedPattern.Describe(_signals.LastLeds),
            LedsAsleep = _ledsAsleep,
            ForegroundApp = fg is null ? null : MeetingAppDetector.DisplayName(fg),
            Playing = ToInfo(_signals.Playing),
            Sticky = ToInfo(_signals.StickySignal),
            Queued = _signals.QueuedCount
        };
    }

    static string ModeName(ActivationMode mode) => mode switch
    {
        ActivationMode.MeetingsOnly => "meetings only",
        ActivationMode.Always => "always active",
        _ => "layered (meeting / idle)"
    };

    static IpcSignalInfo? ToInfo(SignalEngine.Signal? s) => s is null ? null : new IpcSignalInfo
    {
        Id = s.Id,
        Label = s.Label,
        Pattern = s.Pattern.Source,
        Sticky = s.Sticky,
        Priority = s.Priority,
        Ack = s.OnAck,
        StartedAt = s.StartedAt.ToString("HH:mm:ss")
    };

    void ReloadConfig(string origin)
    {
        _config = AppConfig.Load();
        AddLog($"config reloaded ({origin}): mode {_config.Mode}");
        UpdateModeChecks();
        Refresh();
    }

    // --- tray ------------------------------------------------------------------

    /// <summary>
    /// Shows the tray menu at the cursor. NotifyIcon only does this for the right button
    /// through its private ShowContextMenu, which also handles proper dismissal; we call
    /// that via reflection and fall back to a plain Show.
    /// </summary>
    void ShowMenu()
    {
        try
        {
            var m = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (m is not null) { m.Invoke(_tray, null); return; }
        }
        catch { }
        _tray.ContextMenuStrip?.Show(Cursor.Position);
    }

    readonly Dictionary<ActivationMode, ToolStripMenuItem> _modeItems = new();

    ContextMenuStrip BuildMenu()
    {
        var size = _config.MenuFontSize is > 6 and < 40 ? _config.MenuFontSize : 11f;
        // Row height follows ImageScalingSize (even without images) and keeps the text
        // vertically centred; item Padding would push the text off-centre instead.
        var row = (int)Math.Round(size * 2.2);
        var menu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point),
            ImageScalingSize = new Size(row, row),
            ShowImageMargin = false,
            ShowCheckMargin = true
        };

        var status = new ToolStripMenuItem("Status") { Enabled = false, Name = "status" };
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());

        var mode = new ToolStripMenuItem("Mode");
        foreach (var (m, label) in new[]
                 {
                     (ActivationMode.Layered, "Layered (meeting / idle)"),
                     (ActivationMode.MeetingsOnly, "Meetings only"),
                     (ActivationMode.Always, "Always active")
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = _config.Mode == m };
            var chosen = m;
            item.Click += (_, _) =>
            {
                _config.Mode = chosen;
                _config.Save();
                UpdateModeChecks();
                AddLog($"mode: {chosen}");
                Refresh();
            };
            _modeItems[m] = item;
            mode.DropDownItems.Add(item);
        }
        menu.Items.Add(mode);

        var signals = new ToolStripMenuItem("Test signal");
        foreach (var name in new[] { "done", "error", "chase", "pulse" })
        {
            var n = name;
            signals.DropDownItems.Add(n, null, (_, _) => TestSignal(n, false));
        }
        signals.DropDownItems.Add("attention (sticky - press a button to dismiss)", null, (_, _) => TestSignal("attention", true));
        signals.DropDownItems.Add(new ToolStripSeparator());
        signals.DropDownItems.Add("Stop all signals", null, (_, _) =>
        {
            var n = _signals.Cancel(null);
            AddLog($"all signals stopped ({n})");
        });
        menu.Items.Add(signals);

        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add("Open config.json", null, (_, _) => OpenConfigInEditor());
        menu.Items.Add("Reload config.json", null, (_, _) => ReloadConfig("menu"));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Log", null, (_, _) => ShowLog());
        menu.Items.Add("Reconnect", null, (_, _) => _device.Reconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        // Submenus are separate drop-downs; give them the same font and row height.
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
        {
            item.DropDown.Font = menu.Font;
            item.DropDown.ImageScalingSize = menu.ImageScalingSize;
            if (item.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
        }

        return menu;
    }

    void UpdateModeChecks()
    {
        foreach (var (m, item) in _modeItems) item.Checked = _config.Mode == m;
    }

    /// <summary>
    /// Opens config.json in the configured editor; when none is configured, Notepad++ is
    /// used if installed, otherwise the Windows default for .json.
    /// </summary>
    void OpenConfigInEditor()
    {
        var editor = _config.Editor?.Trim();
        if (string.IsNullOrEmpty(editor))
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notepad++", "notepad++.exe"),
            };
            editor = candidates.FirstOrDefault(File.Exists);
        }

        try
        {
            if (string.IsNullOrEmpty(editor))
            {
                Process.Start(new ProcessStartInfo(AppConfig.DefaultPath) { UseShellExecute = true });
                AddLog("config opened with the default .json application");
            }
            else
            {
                var psi = new ProcessStartInfo(editor) { UseShellExecute = true };
                psi.ArgumentList.Add(AppConfig.DefaultPath);
                Process.Start(psi);
                AddLog($"config opened with {Path.GetFileName(editor)}");
            }
        }
        catch (Exception ex) { AddLog($"could not open config: {ex.Message}"); }
    }

    void TestSignal(string name, bool sticky)
    {
        var sig = PlaySignal(name, "test: " + name, 0, null, null, sticky, out var error);
        if (sig is null) AddLog($"test signal failed: {error}");
    }

    void UpdateTooltip()
    {
        var parts = new List<string> { "KB525C Control" };

        if (!_device.IsConnected)
            parts.Add(_device.LastError ?? "keyboard not available");
        else if (_signals.StickySignal is { } sticky)
            parts.Add("signal: " + (sticky.Label ?? sticky.Pattern.Source));
        else
            parts.Add(CurrentLayer switch
            {
                Layer.Off => "standby (no meeting)",
                Layer.Idle => _ledsAsleep ? "idle layer - LEDs asleep" : "idle layer",
                _ => (_mic.IsMuted ?? false) ? "meeting - muted" : "meeting - mic live"
            });

        // NotifyIcon.Text is limited to 63 characters.
        var text = string.Join(" - ", parts);
        _tray.Text = text.Length > 63 ? text[..63] : text;

        if (_tray.ContextMenuStrip?.Items["status"] is ToolStripMenuItem status)
            status.Text = parts.Count > 1 ? parts[1] : "";
    }

    void UpdateIcon()
    {
        var connected = _device.IsConnected;
        var layer = connected ? CurrentLayer : Layer.Off;
        var muted = _mic.IsMuted ?? false;

        var frame = !connected ? Color.FromArgb(110, 110, 116)
            : layer == Layer.Off ? Color.FromArgb(96, 116, 150)
            : layer == Layer.Idle ? Color.FromArgb(120, 170, 215)
            : muted ? Color.FromArgb(225, 70, 60)
            : Color.FromArgb(236, 238, 242);

        var old = _currentIcon;
        _currentIcon = MakeIcon(connected ? _baseLeds : CollabLed.None, frame, connected);
        _tray.Icon = _currentIcon;
        old?.Dispose();
    }

    /// <summary>
    /// Tray icon drawn at runtime: a dark rounded keycap with a coloured frame and a 2x2 grid
    /// of LED dots that mirror the four buttons (Video, Share / Chat, Mic) - white, red or dark.
    /// </summary>
    static Icon MakeIcon(CollabLed leds, Color frame, bool connected)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var cap = new Rectangle(1, 1, 30, 30);
            using (var path = RoundedRect(cap, 8))
            using (var fill = new LinearGradientBrush(cap,
                       connected ? Color.FromArgb(62, 66, 78) : Color.FromArgb(70, 70, 74),
                       connected ? Color.FromArgb(30, 32, 40) : Color.FromArgb(40, 40, 44),
                       LinearGradientMode.Vertical))
            using (var pen = new Pen(frame, 2.5f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            // Video, Share / Chat, Mic
            var dots = new (CollabLed white, CollabLed red, Point at)[]
            {
                (CollabLed.VideoWhite, CollabLed.VideoRed, new Point(11, 11)),
                (CollabLed.ShareWhite, CollabLed.None, new Point(21, 11)),
                (CollabLed.ChatWhite, CollabLed.None, new Point(11, 21)),
                (CollabLed.MicWhite, CollabLed.MicRed, new Point(21, 21)),
            };

            foreach (var (white, red, at) in dots)
            {
                Color color;
                var lit = true;
                if (red != CollabLed.None && leds.HasFlag(red)) color = Color.FromArgb(235, 72, 62);
                else if (leds.HasFlag(white)) color = Color.FromArgb(250, 250, 252);
                else { color = Color.FromArgb(58, 62, 74); lit = false; }

                if (lit)
                {
                    using var glow = new SolidBrush(Color.FromArgb(70, color));
                    g.FillEllipse(glow, at.X - 6, at.Y - 6, 12, 12);
                }
                using var dot = new SolidBrush(color);
                g.FillEllipse(dot, at.X - 3.5f, at.Y - 3.5f, 7, 7);
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    void ShowLog()
    {
        if (_logForm is null || _logForm.IsDisposed)
        {
            _logForm = new LogForm(_logLines);
            _logForm.Cleared += () => _logLines.Clear();
        }
        if (!_logForm.Visible) _logForm.Show();
        if (_logForm.WindowState == FormWindowState.Minimized) _logForm.WindowState = FormWindowState.Normal;
        _logForm.Activate();
    }

    void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new Settings.SettingsForm(_config)
            {
                OpenJson = OpenConfigInEditor,
                TestPattern = pattern =>
                {
                    var sig = PlaySignal(pattern, "settings test", 0, null, null, false, out var error);
                    return sig is null ? error ?? "invalid pattern" : null;
                }
            };
            _settingsForm.Saved += ApplyConfig;
        }
        if (!_settingsForm.Visible) _settingsForm.Show();
        if (_settingsForm.WindowState == FormWindowState.Minimized) _settingsForm.WindowState = FormWindowState.Normal;
        _settingsForm.Activate();
    }

    /// <summary>Takes over a configuration saved by the settings window.</summary>
    void ApplyConfig(AppConfig cfg)
    {
        _config = cfg;
        _tray.ContextMenuStrip = BuildMenu();
        UpdateModeChecks();
        AddLog($"settings saved: mode {_config.Mode}");
        Refresh();
    }

    void AddLog(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {line}";
        _logLines.Add(entry);
        if (_logLines.Count > 500) _logLines.RemoveRange(0, _logLines.Count - 500);
        if (_logForm is { IsDisposed: false }) _logForm.Append(entry);
    }

    void BeginInvokeUi(Action action)
    {
        if (_ui.IsHandleCreated)
        {
            try { _ui.BeginInvoke(action); return; } catch { }
        }
        action();
    }

    void ExitApp()
    {
        _tray.Visible = false;
        foreach (var t in _pendingPress.Values) { t.Stop(); t.Dispose(); }
        _pendingPress.Clear();
        _ipc.Dispose();
        _signals.Dispose();
        _device.Dispose();
        _media.Dispose();
        _inputIdle.Dispose();
        _mic.Dispose();
        _osd?.Dispose();
        _logForm?.Dispose();
        _settingsForm?.Dispose();
        _currentIcon?.Dispose();
        _tray.Dispose();
        _ui.Dispose();
        ExitThread();
    }
}

static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
}
