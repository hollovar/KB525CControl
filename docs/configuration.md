# Configuration reference

KB525C Control is configured through the **Settings window** (tray menu → *Settings…*, or
`kb525c settings`) or by editing `%APPDATA%\KB525CControl\config.json` directly. Both write the
same file; this page documents the file.

- Tray menu → **Open config.json** opens it in your editor (`"editor"` in the config, else
  Notepad++ if installed, else the Windows default for `.json`).
- Tray menu → **Reload config.json** (or `kb525c reload`) applies hand edits.
- The Settings window validates hotkeys, macro steps and patterns before writing. Its
  bottom-left buttons save the whole configuration to a file of your choice, load such a file
  into the window, or reset the window to the factory defaults — none of them applies anything
  until *Save & apply*.
- Configs written by version 0.3 are migrated automatically (`onlyDuringMeetings: true` →
  `Layered`, `false` → `Always`).

## Modes

Tray menu → **Mode**, or `"mode"`:

| Mode | During a meeting | Outside meetings |
|---|---|---|
| `Layered` (default) | meeting layer | **idle layer**: launchers, macros, optional double press |
| `MeetingsOnly` | meeting layer | buttons dark and inert |
| `Always` | meeting layer | meeting layer, buttons always lit |

A meeting is detected by **microphone usage** — Windows records under
`HKCU\...\CapabilityAccessManager\ConsentStore` which applications currently hold the microphone
and the camera, and the app polls that once a second. It needs no app-specific hooks: Teams, Google
Meet, Zoom, Discord and any browser look the same. "Idle" therefore means "nobody is using the
microphone".

**No double press during meetings — on purpose.** A double-press binding forces every single
press to wait for the double-press window; in a meeting you want the Mic button to react instantly
and predictably. Double press exists only in the idle layer, only for the buttons you give a
`double` action.

## The file

```jsonc
{
  "mode": "Layered",                 // Layered | MeetingsOnly | Always
  "micLedFollowsSystemMute": true,   // Mic LED red while the Windows microphone is muted
  "videoLedFollowsCamera": true,     // Video LED lit while an application uses the camera

  // --- meeting layer (single press) -------------------------------------------
  "mic":   { "kind": "SmartToggleMic" },
  "video": { "kind": "SmartToggleCamera" },
  "share": { "kind": "SmartShare" },
  "chat":  { "kind": "SmartChat" },

  // --- idle layer (press + optional double press) -----------------------------
  "idle": {
    "leds": "Assigned",              // Off | Assigned (white where an action exists) | All
    "doublePressMs": 300,
    "sleepAfterSeconds": 300,        // LEDs dark after N s without mouse/keyboard input; 0 = never
    "mic":   { "press": { "kind": "ToggleMicMute" } },
    "video": { "press": { "kind": "RunInActiveFolder", "value": "C:\\Program Files (x86)\\SpeedCrunch\\speedcrunch.exe" } },
    "share": { "press":  { "kind": "RunInActiveFolder", "value": "wt.exe", "arguments": "-d ." },
               "double": { "kind": "RunInActiveFolder", "value": "code", "arguments": "." } },
    "chat":  { "press":  { "kind": "RunInActiveFolder", "value": "wt.exe", "arguments": "-d . claude" } }
  },

  // --- on-screen popup --------------------------------------------------------
  "osd": { "enabled": true, "durationMs": 1200, "fontSize": 14 },

  // --- shortcuts the Smart* actions send to the meeting app in front ----------
  "apps": {
    "meet":  { "micToggle": "ctrl+d",       "cameraToggle": "ctrl+e",       "share": "",             "chat": "ctrl+alt+c" },
    "teams": { "micToggle": "ctrl+shift+m", "cameraToggle": "ctrl+shift+o", "share": "ctrl+shift+e", "chat": "" },
    "zoom":  { "micToggle": "alt+a",        "cameraToggle": "alt+v",        "share": "alt+s",        "chat": "alt+h" }
  },

  // --- LED signals ------------------------------------------------------------
  "signals": {
    "stickyTimeoutMs": 600000,       // a sticky signal lives at most this long (0 = unlimited)
    "presets": {                     // your own named patterns; may override the built-ins
      "bounce": "V---/-S--/--C-/---M/--C-/-S--@200x3",
      "build":  "V---/-S--/--C-/---M@100x*"
    }
  },

  "editor": "code",                  // used by "Open config.json"; omit for automatic
  "menuFontSize": 11                 // tray menu font size in points (WinForms default is 9)
}
```

The factory defaults ship a mild idle layer: Mic = OS mute toggle, Share = Windows Terminal in the
active folder, Video and Chat unassigned (dark).

### LED sleep (`idle.sleepAfterSeconds`)

On by default (300 = 5 minutes; 0 turns it off). The idle-layer LEDs go dark after that long without mouse or
keyboard activity (read from Windows' last-input time — no hook, no keystrokes seen). Anything
that has something to show wakes them: a LED signal plays as usual and the row stays lit
afterwards; a collab button press wakes them **without running the button's action** (a short
popup says so). The countdown restarts after every signal and every press. The buttons stay
enabled while asleep, and the option applies only to the idle layer — a meeting always lights the
row. `kb525c status` reports `ledsAsleep`.

## Action types (`kind`)

| Value | What it does |
|---|---|
| `None` | nothing |
| `ToggleMicMute` | mutes only the Windows microphone (idle layer; the LED shows red while muted) |
| `SmartToggleMic` | OS mute **and** the mute shortcut of the meeting app in front |
| `SmartToggleCamera` | the camera shortcut of the meeting app in front |
| `SmartShare` / `SmartChat` | the matching shortcut of the meeting app in front |
| `Hotkey` | any key combination, `"value": "ctrl+alt+k"` |
| `Run` | starts a program; `"value"` is the path, `"arguments"` optional |
| `RunInActiveFolder` | like `Run`, but the working directory is the folder you are in (see below) |
| `FocusOrLaunch` | brings the program's main window to the front (`"value": "WindowsTerminal"` — the process name), or launches it when not running |
| `Macro` | runs `"steps"` in order — see below |

### How programs are started

`Run`, `RunInActiveFolder` and macro `run` steps: `.ps1` goes through PowerShell (`pwsh` when
available, else `powershell`), `.bat`/`.cmd` through `cmd`, executables directly (`PATH` is
searched), documents and URLs through the shell. `%ENV%` variables in `value` and `arguments` are
expanded.

> **CLI tools.** A console program started directly from the tray app (`claude.exe`, `python.exe`)
> gets a plain console host with none of Windows Terminal's environment (`WT_SESSION` and friends),
> so colour-aware tools run monochrome. Start them through the terminal instead:
> `"value": "wt.exe", "arguments": "-d . claude"`.

### The active folder

`RunInActiveFolder` picks, in this order:

1. Total Commander's **source panel**, when Total Commander is the foreground window;
2. the **Explorer window** in front;
3. the **desktop**, when the desktop is in front;
4. the **last Explorer window** you used;
5. your **user profile**.

`"value": "wt.exe", "arguments": "-d ."` therefore opens a terminal wherever you are. The chosen
folder is shown in the popup and in the log.

Total Commander has no automation API; the panel path is read from its command-line prompt label
(`C:\path>`), with `cm_CopySrcPathToClip` (clipboard restored afterwards) as fallback when the
command line is hidden. Archives and FTP/plugin panels are not real folders and are skipped.

### Environment variables for launched programs

Every program started by a button gets these, so a script knows who called it:

```
KB525C_BUTTON=share          mic | video | share | chat
KB525C_PRESS=press           press | double | ack
KB525C_LAYER=idle            meeting | idle
KB525C_ACTIVE_FOLDER=C:\...  (RunInActiveFolder only, otherwise empty)
KB525C_MIC_MUTED=0           1 when the Windows microphone is muted
KB525C_MEETING=0             1 while some application is using the microphone
```

Documents and URLs opened through the shell do not receive them. `CLAUDE*` variables inherited by
the tray app itself (when it was started from a Claude Code terminal) are scrubbed, so a launched
`claude` starts as a fresh session.

### Macro

```jsonc
"chat": { "press": { "kind": "Macro", "steps": [
  { "run": "wt.exe", "arguments": "-d ." },
  { "delay": 800 },
  { "text": "git status\n" }
] } }
```

Steps: `hotkey` (key combination), `text` (typed as Unicode; `\n` = Enter, `\t` = Tab),
`delay` (milliseconds), `run` + `arguments`. Hotkeys and text go to whatever window is focused
at that moment.

### Hotkey syntax

Modifiers `ctrl` (or `control`), `alt`, `shift`, `win`, joined with `+` to a key name:
`ctrl+shift+m`, `win+shift+s`, `alt+f4`, `ctrl+alt+c`. Keys: single letters and digits,
`f1`–`f24`, `enter`, `esc`, `tab`, `space`, `backspace`, `delete`, `up`/`down`/`left`/`right`,
`printscreen`. The Settings window rejects anything else before saving.

## Meeting apps (`apps`)

The `Smart*` actions look at the foreground window: the Teams process, the Zoom process, or a
browser (Chrome, Edge, Firefox, Brave, Opera, Vivaldi) whose title contains *Meet*, *Teams* or
*Zoom*. The matching row of `apps` supplies the shortcut; an empty string means "this app has no
such shortcut" and the popup says so.

Defaults as of 2026: Google Meet `ctrl+d` / `ctrl+e` / — / `ctrl+alt+c`; Teams `ctrl+shift+m` /
`ctrl+shift+o` / `ctrl+shift+e` / —; Zoom `alt+a` / `alt+v` / `alt+s` / `alt+h`. If an app changes
a shortcut, fix it here — no rebuild needed.

## On-screen popup (`osd`)

A small dark label near the bottom of the screen — "Mic · muted", "Share · wt.exe in
C:\Projects" — for every button press that does something. It never takes the focus.
`enabled`, `durationMs`, `fontSize`. Acknowledging a signal shows the signal's label for 2 s.

## Signals (`signals`)

`stickyTimeoutMs` caps the lifetime of sticky (infinite) signals; `presets` are your named
patterns, usable as `kb525c signal <name>`, and may override the built-in names. The pattern
language and the behaviour of the queue are in [signals.md](signals.md).

## Known limitations and workarounds

- **A launched program's window opens behind the current one / only flashes on the taskbar.**
  Windows lets only the foreground process, or the process that received the last user input,
  activate a window — and a program inherits that right from whoever started it. The tray app
  is neither (a button press is a HID report, not user input in Windows' eyes), so a program it
  starts gets its window but not the focus. That is also why it *does* work right after you
  closed the Settings window: for a moment the app is the last one you interacted with.
  Since 0.6.1 the app handles this itself: after `Run`, `RunInActiveFolder` and a `FocusOrLaunch`
  launch it waits up to 6 s for the new window and brings it to the front (the log says
  `brought the new window to the front`). When no new window appears — a single-instance program
  handed the request to its running copy, or the terminal opened a tab in an existing window —
  it falls back to the program's process name (`wt.exe` → `WindowsTerminal`).
  Workaround, and the better choice for single-instance programs anyway: use `FocusOrLaunch`;
  pressing the button again while the program runs brings its window forward.
- Shortcuts only reach the **focused** window. If the meeting window is not in front, the Mic
  button still mutes at the OS level, but the app UI does not follow. The log says so.
- **Google Meet has no screen-share shortcut**, so `apps.meet.share` is empty by default. Usable
  alternatives: `ctrl+alt+h` (raise hand) or `ctrl+alt+p` (participants).
- The Teams combinations are not verified on every version. If one does not work, fix it in
  `apps.teams`.
- Microphone usage is the meeting indicator. If a browser keeps the microphone open for another
  reason (a voice-typing tab, say), the meeting layer stays active.
- Only Video and Mic have a red LED state; Share and Chat are white-or-dark in hardware.
- The KB525C is the only supported model. Other Dell keyboards with collaboration keys are
  reported by PID in the tray balloon but not driven.
