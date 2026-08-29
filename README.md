# KB525C Control

Custom handling of the four collaboration buttons (Video, Share, Chat, Mic) of the
Dell KB525C keyboard, LED control included — without Dell Peripheral Manager and without Zoom.

Talks to the keyboard's own USB HID vendor interface directly - no Dell software involved.

---

## What it does

- **Two layers.** During a meeting (any application is using the microphone — Teams, Google
  Meet, Zoom, Discord alike) the buttons drive the meeting: Mic mutes the Windows microphone
  and the app, Video toggles the camera, Share and Chat send the app's shortcuts. Outside
  meetings the same four buttons become launchers and macros — open a terminal in the folder
  you are looking at (Explorer or Total Commander), focus Teams, run a script — with an
  optional double-press action each.
- **Settings window** (tray menu → *Settings...*) edits every option with a description next
  to it; the raw `config.json` stays available for scripting and version control.
- **LED feedback.** White = active, red = muted / camera off, dark = no function. Only Video
  and Mic can show red; the hardware knows only white for Share and Chat.
- **On-screen popup** ("Mic · muted", "Share · wt.exe in C:\Projects") for every button
  action; it never takes the focus.
- **LED signals from outside.** Any program or script (through the `kb525c` CLI or a named
  pipe) can flash the button row — "the build finished", "Claude Code is asking". A sticky
  signal is acknowledged by pressing a button, which can also bring the terminal to the front.
- **Keyboard detection.** If the KB525C is unplugged, a different Dell model is connected, or
  the command channel is busy, a tray balloon says so and the icon turns grey.
- **Tray icon** mirrors the four LEDs (2×2 dots: Video, Share / Chat, Mic) and the state
  (grey = no keyboard, blue = idle, white = meeting, red = muted).

---

## Installing on another machine

### 1. Build a release

```powershell
.\tools\publish.ps1
```

This compiles both programs into single self-contained executables and collects them,
together with the install/uninstall scripts, into `dist\`:

```
dist\KB525CControl.exe    tray app (~50 MB, self-contained)
dist\kb525c.exe           command-line client (~36 MB)
dist\install.ps1
dist\uninstall.ps1
```

They are self-contained, so **no .NET installation is needed on the target machine**.

### 2. Copy and install

Copy the contents of `dist\` to the target machine, then:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The script:

1. checks whether the KB525C is connected,
2. copies the executables to `%LOCALAPPDATA%\KB525CControl\` and adds that folder to the
   user `PATH` (so `kb525c` works from any new terminal),
3. **disables the `DPMService`** (a UAC prompt appears),
4. registers the app to start at logon,
5. starts it.

Switches: `-NoAutostart`, `-KeepDellService`, `-Source <exe path>`

### 3. Why DPMService has to be disabled

Dell Peripheral Manager opens the keyboard's vendor command channel **exclusively**
(without `FILE_SHARE`). While it runs, no other program can control the collaboration
buttons — `CreateFile` fails with `ERROR_SHARING_VIOLATION (32)`.

What you lose: the factory Zoom/Teams integration of the buttons and keyboard firmware
updates. Regular keys, media keys and the F-row do **not** depend on it.

Note: there is no separate "Dell Peripheral Manager" installation entry. The service comes
with the **Dell Peripheral Core** package, which is part of **Dell Display and Peripheral
Manager** — and that also manages Dell monitors. Hence **disabling the service** is the
right move, not uninstalling the software.

### Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RestoreDellService
```

---

## Modes

Tray menu → **Mode** (or `"mode"` in the config):

| Mode | During a meeting | Outside meetings |
|---|---|---|
| `Layered` (default) | meeting layer | **idle layer**: launchers, macros, optional double press |
| `MeetingsOnly` | meeting layer | buttons dark and dead |
| `Always` | meeting layer | meeting layer, buttons always lit |

A meeting is detected by microphone usage (Windows' own capability tracking), so it needs no
app-specific hooks. "Idle" therefore means "nobody is using the microphone".

**No double press during meetings — on purpose.** A double-press binding forces every single
press to wait for the double-press window; in a meeting you want the Mic button to react
instantly and predictably. Double press exists only in the idle layer, only for the buttons
you give a `double` action.

---

## Settings

Tray menu → **Settings...** (or `kb525c settings`) opens the settings window: General,
Meeting buttons, Idle buttons, Meeting apps, On-screen popup, Signals and Integration pages,
each option explained in place. *Save & apply* validates (hotkeys, macro steps, patterns),
writes the file and applies it immediately — including the menu font size. The bottom-left
buttons save all settings to a file of your choice, load such a file into the window, or
reset the window to the factory defaults — none of them applies anything until *Save & apply*.

The file itself: `%APPDATA%\KB525CControl\config.json` (tray menu → **Open config.json**).
After editing it by hand: tray menu → **Reload config.json** (or `kb525c reload`).

```jsonc
{
  "mode": "Layered",                 // Layered | MeetingsOnly | Always
  "micLedFollowsSystemMute": true,
  "videoLedFollowsCamera": true,

  // --- meeting layer (single press) ------------------------------------------
  "mic":   { "kind": "SmartToggleMic" },
  "video": { "kind": "SmartToggleCamera" },
  "share": { "kind": "SmartShare" },
  "chat":  { "kind": "SmartChat" },

  // --- idle layer (press + optional double press) --------------------------------
  "idle": {
    "leds": "Assigned",              // Off | Assigned (white where an action exists) | All
    "doublePressMs": 300,
    "mic":   { "press": { "kind": "ToggleMicMute" } },
    "video": { "press": { "kind": "Hotkey", "value": "win+shift+s" } },
    "share": { "press":  { "kind": "RunInActiveFolder", "value": "wt.exe", "arguments": "-d ." },
               "double": { "kind": "RunInActiveFolder", "value": "code", "arguments": "." } },
    "chat":  { "press":  { "kind": "FocusOrLaunch", "value": "ms-teams" } }
  },

  "osd": { "enabled": true, "durationMs": 1200, "fontSize": 14 },

  "apps": {
    "meet":  { "micToggle": "ctrl+d",       "cameraToggle": "ctrl+e",       "share": "",             "chat": "ctrl+alt+c" },
    "teams": { "micToggle": "ctrl+shift+m", "cameraToggle": "ctrl+shift+o", "share": "ctrl+shift+e", "chat": "" },
    "zoom":  { "micToggle": "alt+a",        "cameraToggle": "alt+v",        "share": "alt+s",        "chat": "alt+h" }
  },

  "signals": {
    "stickyTimeoutMs": 600000,       // a sticky signal lives at most this long (0 = unlimited)
    "presets": {                     // your own named patterns; may override the built-ins
      "build": "V---/-S--/--C-/---M@100x*"
    }
  },

  "editor": "code",                  // "Open config.json" uses this; omit = Notepad++ if installed, else the Windows default
  "menuFontSize": 11                 // tray menu font size in points (the WinForms default would be 9)
}
```

The defaults ship a mild idle layer: Mic = OS mute toggle (the LED shows red while muted —
handy for Discord and the like), Share = Windows Terminal in the active folder, Video and
Chat unassigned (dark). Configs from version 0.3 are migrated automatically
(`onlyDuringMeetings: true` → `Layered`, `false` → `Always`).

The tray menu opens on both left and right click.

### Action types (`kind`)

| Value | What it does |
|---|---|
| `None` | nothing |
| `ToggleMicMute` | mutes only the Windows microphone |
| `SmartToggleMic` | OS mute + the mute shortcut of the focused meeting app |
| `SmartToggleCamera` | the camera shortcut of the focused meeting app |
| `SmartShare` / `SmartChat` | the matching shortcut of the focused meeting app |
| `Hotkey` | any combination, `"value": "ctrl+alt+k"` |
| `Run` | starts a program, `"value"` is the path, `"arguments"` optional |
| `RunInActiveFolder` | like `Run`, but the working directory is the folder you are in: Total Commander's active panel when it is in front, the Explorer window in front, the desktop, the last Explorer window, then your profile. `"value": "wt.exe", "arguments": "-d ."` opens a terminal there |
| `FocusOrLaunch` | brings the program's main window to the front (`"value": "WindowsTerminal"`), or launches it when not running |
| `Macro` | runs `"steps"` in order — see below |

`Run` / `RunInActiveFolder` / macro `run` steps: `.ps1` goes through PowerShell (pwsh when
available), `.bat`/`.cmd` through cmd, executables directly (PATH is searched), documents
and URLs through the shell. `%ENV%` variables in `value` and `arguments` are expanded.

#### Environment variables for launched programs

Every program started by a button gets these, so a script knows who called it:

```
KB525C_BUTTON=share          mic | video | share | chat
KB525C_PRESS=press           press | double | ack
KB525C_LAYER=idle            meeting | idle
KB525C_ACTIVE_FOLDER=C:\...  (RunInActiveFolder only, otherwise empty)
KB525C_MIC_MUTED=0           1 when the Windows microphone is muted
KB525C_MEETING=0             1 while some app is using the microphone
```

Documents and URLs opened through the shell do not receive them.

#### Macro

```jsonc
"chat": { "press": { "kind": "Macro", "steps": [
  { "run": "wt.exe", "arguments": "-d ." },
  { "delay": 800 },
  { "text": "git status\n" }
] } }
```

Steps: `hotkey` (key combination), `text` (typed as Unicode, `\n` = Enter, `\t` = Tab),
`delay` (ms), `run` + `arguments`. Hotkeys and text go to whatever window is focused.

### Known limitations

- Shortcuts only reach the **focused** window. If the meeting window is not in front, the
  Mic button still mutes at the OS level, but the app UI does not follow. The log says so.
- **Google Meet has no screen-share shortcut**, so `share` is empty by default. Usable
  alternatives: `ctrl+alt+h` (raise hand) or `ctrl+alt+p` (participants).
- The Teams combinations are not verified on every version. If one does not work, fix it
  in the `apps.teams` section of the config without recompiling.
- Microphone usage is the meeting indicator. If a browser keeps the microphone open for
  another reason, the meeting layer stays active.
- Total Commander has no automation API; the active folder is read from its command-line
  prompt label (`C:path>`), with `cm_CopySrcPathToClip` (clipboard restored) as fallback when
  the command line is hidden. Archives and FTP/plugin panels are not real folders and are
  skipped.

---

## LED signals: `kb525c signal`

The button row is a programmable indicator. A signal is a sequence of frames that the tray
app layers *on top of* the current state, restoring the original when it ends.

```powershell
kb525c signal done                          # built-in pattern
kb525c signal "V---/-S--/--C-/---M@80x3"    # custom pattern: running light
kb525c signal attention --sticky --ack focus:WindowsTerminal
kb525c off                                  # stop all signals
kb525c status                               # what is it doing right now
kb525c patterns                             # available named patterns
kb525c press share                          # simulate a button press (test your bindings)
```

### Pattern language

```
FRAMES[@MS][xN]
```

- **FRAMES**: frames separated by `/`. One frame is **4 characters**, in order
  **V**ideo, **S**hare, **C**hat, **M**ic. Upper case = white, lower case = red (only `v`
  and `m` exist, because of the hardware), `-` = dark. Alias words: `all`, `off`, `red`.
  A raw LED byte is accepted too (`0x41`) for experiments; bits 0x40/0x80 do nothing.
- **@MS**: length of one frame in ms (default 150). `@0` = hold the frame.
- **xN**: how many times it runs (default 1). `x*` = forever → a **sticky** signal.

| Pattern | Effect |
|---|---|
| `VSCM/----@150x5` | the whole row flashes five times |
| `V---/-S--/--C-/---M@80x3` | running light left to right, three times |
| `v--m/----@250x*` | red alarm until acknowledged |
| `VSCM/v--m@120x4` | white/red alternation |
| `VSCM@0` | all white, held |

Built-in names: `done`, `error`, `attention`, `running`, `chase`, `pulse`, `red`, `all`.
Add your own in the `signals.presets` section of the config (they may override the built-ins).

### Behaviour

- **Transient** (finite) signals play one after another. A higher `--priority N` moves
  forward in the queue.
- Only one **sticky** signal (infinite, `x*` or `--sticky`) lives at a time. Transient
  signals play "on top" of it, then the sticky one resumes. It expires after
  `signals.stickyTimeoutMs` (default 10 minutes) or `--timeout <ms>`.
- **Acknowledging**: while anything is blinking — a sticky signal or a transient one still
  playing — pressing any collab button means "seen": the signal (and the queue) stops, a
  2-second popup shows the signal's `--label` (or its pattern) so you know what it was
  about, and the `--ack` action runs — `focus:<process name>` brings the window to the
  front, `run:<command>` starts, `hotkey:<combination>` sends. The button's **normal
  function does not fire** on that press; press again for it.
- Buttons are always live while a signal plays (so you can acknowledge), but in
  `MeetingsOnly` mode outside a meeting they perform no action.
- Tray menu → **Test signal**: the same without the CLI.

### `kb525c run` — running light while a command runs

```powershell
kb525c run -- dotnet build
kb525c run --label "tests" -- npm test
```

A running light plays while the command runs; at the end `done` or `error` by exit code.
`kb525c` **returns the command's exit code**, so it chains into scripts.
If the tray app is not running, the command still runs, just without signals.

For an already running process: `kb525c watch --pid 1234 --then done --error error`.

### Exit codes

`0` ok · `1` error · `2` the tray app is not running · `64` usage error

---

## External control: named pipe

`kb525c` is just a client; the tray app listens on the `\\.\pipe\KB525CControl` named pipe.
**One connection = one JSON line in, one JSON line out.** Only the logged-in user can
connect. This is the "plugin interface": talk to it from any language.

```jsonc
// request                                                     // response
{"cmd":"ping"}                                                 {"ok":true,"message":"pong"}
{"cmd":"status"}                                               {"ok":true,"status":{...}}
{"cmd":"patterns"}                                             {"ok":true,"patterns":{...}}
{"cmd":"signal","pattern":"done"}                              {"ok":true,"id":3,"message":"..."}
{"cmd":"signal","pattern":"attention","sticky":true,
 "ack":"focus:WindowsTerminal","label":"CI","timeoutMs":60000,
 "priority":1}                                                 {"ok":true,"id":4}
{"cmd":"off"}            // or {"cmd":"off","id":4}            {"ok":true,"message":"1 signal(s) stopped"}
{"cmd":"press","button":"share"}                               {"ok":true,"message":"Share pressed (Idle)"}
{"cmd":"reload"}                                               {"ok":true}
```

On error: `{"ok":false,"error":"..."}`.

PowerShell, without the CLI:

```powershell
$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'KB525CControl', 'InOut')
$p.Connect(1000)
$w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
$w.WriteLine('{"cmd":"signal","pattern":"done"}')
(New-Object System.IO.StreamReader($p)).ReadLine()
$p.Dispose()
```

Python:

```python
with open(r'\\.\pipe\KB525CControl', 'r+b', buffering=0) as p:
    p.write(b'{"cmd":"signal","pattern":"done"}\n')
    print(p.readline())
```

The tray app executable forwards simple commands to the running instance as well
(`KB525CControl.exe signal done`), but the full interface is `kb525c`.

---

## Claude Code integration

Claude Code hooks run a shell command on events. Two of them are useful here:
**Stop** (Claude finished responding) and **Notification** (Claude waits for approval or input).
`~/.claude/settings.json`:

```jsonc
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "timeout": 5,
          "command": "\"$LOCALAPPDATA/KB525CControl/kb525c.exe\" signal done --label \"Claude Code finished\" >/dev/null 2>&1 || true" } ] }
    ],
    "Notification": [
      { "matcher": "permission_prompt|idle_prompt",
        "hooks": [ { "type": "command", "timeout": 5,
          "command": "\"$LOCALAPPDATA/KB525CControl/kb525c.exe\" signal attention --sticky --ack focus:WindowsTerminal --label \"Claude Code is waiting\" >/dev/null 2>&1 || true" } ] }
    ]
  }
}
```

Result: Claude finishes → the row flashes three times. Claude asks → the row blinks slowly
until you press a button, which also brings Windows Terminal to the front.

Pressing a button *while* a signal plays only dismisses it (and shows its `--label` in the
popup); a press *after* a short signal has ended runs the button's normal function. If you
want the "finished" blink to wait for you too, add `--sticky` to the Stop hook as well — it
then runs until a button is pressed (or `signals.stickyTimeoutMs` passes). Any pattern name
works, e.g. a preset from the Signals page such as a left-right bounce
(`V---/-S--/--C-/---M/--C-/-S--@200x3`). Hook changes take effect in a new Claude Code session.

**The `|| true` matters**: Claude Code treats a hook exit code of `2` as a "blocking
error" — and `kb525c` returns exactly 2 when the tray app is not running. Without `|| true`
a stopped tray app would interfere with Claude Code.

The commands run in Git Bash (Claude Code's default on Windows when Git Bash is installed);
hence the `$LOCALAPPDATA` and `/dev/null` forms.

---

## Troubleshooting

Tray menu → **Log** opens a live, scrollable log window (Copy / Clear). It shows which
button was received and in which layer, which app was recognised, what was sent, which folder
`RunInActiveFolder` picked, and which signals arrived on the pipe.

`kb525c status` gives the state from the command line (`--json` for machine-readable output),
`kb525c press <button>` triggers a binding without touching the keyboard.

If the icon is grey and a balloon says the command channel is busy: `DPMService` is running.
If it says the keyboard is not detected: plug in the KB525C — a different Dell model is
reported with its PID.
