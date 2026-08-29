# Integrations

The LED row is an indicator any program can drive. This page collects working recipes.

## Claude Code

[Claude Code](https://claude.com/claude-code) runs shell commands on events through *hooks* in
`~/.claude/settings.json`. Two events are worth a blink:

| Event | When | Suggested signal |
|---|---|---|
| `Stop` | Claude finished responding | a sticky bounce, label *Claude Code finished* |
| `Notification` with matcher `permission_prompt\|idle_prompt` | Claude waits for a permission or for your input | a sticky bounce, label *Claude Code is waiting*, `--ack focus:WindowsTerminal` |

### Setup

1. **Have a pattern.** The example uses a preset named `bounce`:
   `V---/-S--/--C-/---M/--C-/-S--@200x3` — a light running left, right and back, three times.
   Define it in *Settings → Signals → Named presets* (there is a *Test selected* button), or in
   `config.json`:

   ```jsonc
   "signals": { "presets": { "bounce": "V---/-S--/--C-/---M/--C-/-S--@200x3" } }
   ```

   Any built-in name (`attention`, `done`, `chase`…) works just as well; a preset is simply easier
   to change later without touching the hooks.

2. **Add the hooks.** *Settings → Integration* shows the hooks currently in effect and has a
   *Copy template* button with this text:

   ```jsonc
   {
     "hooks": {
       "Stop": [
         { "hooks": [ { "type": "command", "timeout": 5,
             "command": "\"$LOCALAPPDATA/KB525CControl/kb525c.exe\" signal bounce --sticky --label \"Claude Code finished\" >/dev/null 2>&1 || true" } ] }
       ],
       "Notification": [
         { "matcher": "permission_prompt|idle_prompt",
           "hooks": [ { "type": "command", "timeout": 5,
             "command": "\"$LOCALAPPDATA/KB525CControl/kb525c.exe\" signal bounce --sticky --ack focus:WindowsTerminal --label \"Claude Code is waiting\" >/dev/null 2>&1 || true" } ] }
       ]
     }
   }
   ```

   If `settings.json` already has other hooks, merge the two arrays into the existing `"hooks"`
   object rather than adding a second one.

3. **Restart Claude Code** — hooks are read when a session starts.

4. **Test** without Claude: `kb525c signal bounce --sticky --label test`, press a collab button.

### What happens

- Claude finishes → the row bounces until you press a button. Pressing shows *Claude Code
  finished* in the popup and the LEDs return to their normal state. That press does **not** run
  the button's own function; press again for that.
- Claude asks for a permission or waits for input → same bounce, and the acknowledging press also
  brings Windows Terminal to the front (`--ack focus:WindowsTerminal`).
- Two events in a row (Claude asks, then finishes) → the newer sticky signal replaces the older.
- If the tray app is not running, `kb525c` exits with 2 and `|| true` turns that into success, so
  Claude Code is not affected.

### Variations

- **Not sticky.** Drop `--sticky` from the Stop hook: three bounces, then silence, no
  acknowledgement needed. A press *after* the signal ended runs the button's normal function.
- **Another terminal.** `--ack focus:<process name>` — `Code` for VS Code, `alacritty`,
  `WezTerm`, `ConEmu64`… `kb525c status` shows the foreground process name, handy for finding it.
- **Different patterns per event.** `signal done` for Stop and `signal attention --sticky` for
  Notification, so the shape tells you which one it was even before you press.
- **Longer / shorter patience.** `--timeout 1800000` (30 min) on the Notification hook, or
  `signals.stickyTimeoutMs` globally.

### Why the command looks like that

- The hook runs in **Git Bash** (Claude Code's default shell on Windows when Git Bash is
  installed), hence `$LOCALAPPDATA` in POSIX form and `/dev/null`.
- **`|| true` is required.** Claude Code treats hook exit code `2` as a *blocking error*, and
  `kb525c` returns exactly 2 when the tray app is not running.
- `"timeout": 5` keeps a hung pipe from ever stalling Claude.

### Running Claude Code from a button

The Chat button in the author's idle layer starts Claude Code in the folder in front:
`RunInActiveFolder`, program `wt.exe`, arguments `-d . claude`. Going through Windows Terminal
matters: a console program started directly from the tray app gets a plain console host without
`WT_SESSION`, and Claude Code then runs without colours. The tray app also scrubs inherited
`CLAUDE*` environment variables so the launched session starts clean.

## Build and test scripts

```powershell
kb525c run --label build -- dotnet build          # running light, then done / error
kb525c run --label tests -- npm test
```

`kb525c run` returns the wrapped command's exit code, so it drops into existing scripts without
changing their behaviour. For something already running: `kb525c watch --pid <n> --then done`.

## Anything with a shell

The tray app answers on a named pipe with one JSON line — see
[pipe-protocol.md](pipe-protocol.md) for PowerShell, Python, Node and C# snippets. Useful
starting points:

- **Task Scheduler / cron-like reminders**: `kb525c signal "VSCM@0" --sticky --timeout 60000` —
  all white for a minute.
- **Monitoring**: `kb525c signal red --sticky --label "disk 95%"` from a watchdog script.
- **Presence**: `kb525c status --json | jq .meeting` tells other tools whether you are in a call.

## Scripts started by a button

Every program a button launches receives `KB525C_BUTTON`, `KB525C_PRESS`, `KB525C_LAYER`,
`KB525C_ACTIVE_FOLDER`, `KB525C_MIC_MUTED` and `KB525C_MEETING` in its environment, so one script
can serve several bindings. Details in [configuration.md](configuration.md#environment-variables-for-launched-programs).
