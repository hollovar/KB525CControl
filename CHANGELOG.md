# Changelog

All notable changes to KB525C Control are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed
- **A busy command channel is reported as busy.** Dell Peripheral Core 2.4 holds the command
  channel so tightly that it disappears from the device list altogether; the app then blamed
  "missing vendor interfaces (unsupported firmware?)". It now says the channel is busy and points
  at `DPMService`, as it does for older Dell software.

## [0.6.1] — 2026-08-30

### Fixed
- **Launched programs come to the front.** A program started from a button (`Run`,
  `RunInActiveFolder`, `FocusOrLaunch`) opened behind the current window or only flashed on the
  taskbar, because Windows does not let a background process hand over the foreground. The app
  now waits for the new window (up to 6 s) and brings it to the front itself; when no new window
  appears (single-instance program, terminal tab in an existing window) it falls back to the
  program's process name. See *Known limitations and workarounds* in `docs/configuration.md`.
- **Settings: the mouse wheel no longer changes values by accident.** Wheeling over (or with the
  focus on) a drop-down or a numeric field scrolls the page instead of silently changing the
  setting. An open drop-down list still scrolls with the wheel.

## [0.6.0] — 2026-08-29

### Added
- **LED sleep** for the idle layer (`idle.sleepAfterSeconds`, *Idle buttons* page): the LEDs go
  dark after N seconds without mouse or keyboard input (on by default, 300 s). A signal or a
  button press wakes them; the waking press does not run the button's action. `kb525c status`
  shows `ledsAsleep`.

### Changed
- Public repository: restructured README, reference documentation under `docs/`, CI build and
  release workflow, issue template.

## [0.5.0] — 2026-08-29

First public release, extracted from the private reverse-engineering repository.

### Added
- **Settings window** (tray menu → *Settings…*, `kb525c settings`) with seven pages and inline
  explanations; save/load whole configurations to a file; reset to defaults.
- **Total Commander** support for `RunInActiveFolder`: the source panel of the active TC window,
  read from its command-line prompt with a clipboard fallback.
- **Signal acknowledgement**: a button press while a signal plays stops it, shows its `--label`
  in the popup and runs the `--ack` action (`focus:` / `run:` / `hotkey:`).
- `kb525c log [N]`, `kb525c settings`, `kb525c status --json`.
- *Integration* settings page showing the Claude Code hooks currently in effect, with a copyable
  template.
- Tray menu font size (`menuFontSize`), editor override for *Open config.json*.
- `tools/publish.ps1` split from the install script: self-contained single-file executables
  collected in `dist\`.

### Changed
- Configuration migrated from `onlyDuringMeetings` to the three-way `mode`
  (`Layered` / `MeetingsOnly` / `Always`); old files are converted automatically.

## Before 0.5.0 (private development)

In order of appearance:

- Console prototype: enabling the collaboration keys, reading button events, driving the LEDs.
- First tray application: meeting detection through Windows microphone usage; smart mute /
  camera / share / chat actions for Google Meet, Microsoft Teams and Zoom; install script that
  disables `DPMService`.
- Idle layer with launchers, macros and double press; `RunInActiveFolder`; `FocusOrLaunch`;
  on-screen popup; live log window; tray icon mirroring the four LEDs; keyboard detection with
  hot-plug, wrong-model and busy-channel diagnostics.
- LED **signal engine** with a pattern language, sticky signals, priorities and a queue;
  **named-pipe IPC** (`\\.\pipe\KB525CControl`) and the `kb525c` command-line client, including
  `kb525c run` / `kb525c watch`; application text English only.

[Unreleased]: https://github.com/hollovar/KB525CControl/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/hollovar/KB525CControl/releases/tag/v0.5.0
