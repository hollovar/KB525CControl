# LED signals and the `kb525c` command line

`kb525c.exe` is a thin client: it sends one JSON request to the tray app over the named pipe
`\\.\pipe\KB525CControl` and prints the answer. The tray app does everything else. If the tray app
is not running, every command exits with code **2**.

## Command reference

```
kb525c signal <pattern> [--sticky] [--label <text>] [--ack <action>] [--timeout <ms>] [--priority <n>]
kb525c off [<id>]                     stop all signals, or one by id
kb525c status [--json]                what the app is doing right now
kb525c patterns                       built-in and preset pattern names
kb525c ping                           is the app running
kb525c reload                         reload config.json
kb525c settings                       open the settings window
kb525c log [N]                        last N log lines of the tray app (default 40)
kb525c press <mic|video|share|chat>   simulate a button press (test your bindings)

kb525c run [--label X] [--running P] [--done P] [--error P] -- <command> [arg...]
kb525c watch --pid <n> [--running P] [--then P] [--error P]
```

`signal` options:

| Option | Meaning |
|---|---|
| `--sticky` | runs forever, until a collab button acknowledges it, `off` is sent, or the timeout passes |
| `--label <text>` | shown in the popup when the signal is acknowledged, so you know what it was about |
| `--ack <action>` | runs on acknowledgement: `focus:<process name>`, `run:<command>`, `hotkey:<combination>` |
| `--timeout <ms>` | maximum lifetime of a sticky signal (default `signals.stickyTimeoutMs`, 10 minutes) |
| `--priority <n>` | higher numbers move forward in the transient queue |

### Exit codes

`0` ok · `1` error (the app answered `ok:false`) · `2` the tray app is not running · `64` usage error

`kb525c run` is the exception: it returns **the wrapped command's exit code**, so it chains into
scripts unchanged.

## Pattern language

```
FRAMES[@MS][xN]
```

- **FRAMES**: frames separated by `/`. One frame is **4 characters**, in order **V**ideo,
  **S**hare, **C**hat, **M**ic. Upper case = white, lower case = red (only `v` and `m` exist —
  Share and Chat have no red in hardware), `-` = dark. Alias words: `all`, `off`, `red`. A raw LED
  byte is accepted too (`0x41`) for experiments; bits 0x40/0x80 do nothing.
- **@MS**: length of one frame in milliseconds (default 150). `@0` = hold the frame.
- **xN**: how many times the sequence runs (default 1). `x*` = forever → a **sticky** signal.

| Pattern | Effect |
|---|---|
| `VSCM/----@150x5` | the whole row flashes five times |
| `V---/-S--/--C-/---M@80x3` | running light left to right, three times |
| `V---/-S--/--C-/---M/--C-/-S--@200x3` | **bounce**: left to right and back, three times |
| `v--m/----@250x*` | red alarm until acknowledged |
| `VSCM/v--m@120x4` | white/red alternation |
| `VSCM@0` | all white, held |

Built-in names: `done`, `error`, `attention`, `running`, `chase`, `pulse`, `red`, `all`.
`kb525c patterns` lists them with their definitions. Add your own under `signals.presets` in the
config (Settings → Signals → *Named presets*, with a *Test selected* button); they may override the
built-ins.

## Behaviour

- A signal is played **on top of** the current LED state; when it ends, the state is restored
  (including the red Mic while muted).
- **Transient** (finite) signals play one after another. A higher `--priority` moves forward in
  the queue.
- Only one **sticky** signal (infinite, `x*` or `--sticky`) lives at a time; a new one replaces
  it unless the old one has a higher `--priority` (then the new one is dropped and logged).
  Transient signals play "on top" of it, then the sticky one resumes. It expires after
  `signals.stickyTimeoutMs` or `--timeout`.
- **Acknowledging**: while anything is blinking — a sticky signal or a transient one still
  playing — pressing any collab button means "seen": the signal (and the queue) stops, a
  2-second popup shows the signal's `--label` (or its pattern), and the `--ack` action runs. The
  button's **normal function does not fire** on that press; press again for it.
- Buttons are always live while a signal plays (so you can acknowledge), but in `MeetingsOnly`
  mode outside a meeting they perform no action.
- Tray menu → **Test signal**: the same without the CLI. **Stop all signals** is there too.

## `kb525c run` — a running light while a command runs

```powershell
kb525c run -- dotnet build
kb525c run --label "tests" -- npm test
kb525c run --running chase --done all --error red -- .\deploy.ps1
```

The `running` pattern plays while the command runs; at the end `done` or `error` is played
according to the exit code, and the exit code is returned. If the tray app is not running, the
command still runs, only without signals.

For a process that is already running: `kb525c watch --pid 1234 --then done --error error`.

## Examples

```powershell
# a long build, sticky result that waits for you, press brings the terminal back
kb525c run --label "release build" -- .\tools\publish.ps1; `
  kb525c signal $(if ($LASTEXITCODE -eq 0) { 'done' } else { 'error' }) --sticky --ack focus:WindowsTerminal

# CI watcher: red alarm when a remote job fails
if (-not (gh run watch --exit-status)) { kb525c signal red --sticky --label "CI failed" }

# a pomodoro timer: all white for 25 minutes, then a bounce
kb525c signal "VSCM@0" --sticky --timeout 1500000; Start-Sleep 1500; kb525c signal bounce --sticky
```

The same requests from PowerShell or Python without the CLI are shown in
[pipe-protocol.md](pipe-protocol.md).
