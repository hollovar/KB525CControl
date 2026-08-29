# Contributing

Thanks for taking an interest. This is a small, one-developer project; the notes below keep
contributions quick to review.

## Reporting a problem

Open an issue with the *Bug report* template. The two things that answer most questions:

```powershell
kb525c status --json     # version, keyboard, mode, layer, LEDs, mic/camera, foreground app
kb525c log 80            # what the tray app saw: buttons, layers, folders, signals, errors
```

For meeting-shortcut problems, name the application and its version; the shortcut tables live in
the config (`apps` section) and can usually be fixed without code.

## Building

- Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download).
- `dotnet build KB525CControl.slnx -c Release` — both projects.
- `dotnet run --project KB525CControl` starts the tray app from source (stop the installed
  instance first: only one may run, it holds the keyboard's command channel).
- `tools\publish.ps1` — the self-contained release executables in `dist\`.

The keyboard's command channel is exclusive, so **`DPMService` must be disabled** on the
development machine as well (`install.ps1` does that, or
`Set-Service DPMService -StartupType Disabled; Stop-Service DPMService` as administrator).

## Testing without pressing keys

`kb525c press <button>` simulates a press through the pipe and exercises the full action path
(layer selection, folder detection, launch, popup, log). Tray menu → *Test signal* and
`kb525c signal <pattern>` exercise the LED engine. A KB525C is needed for anything that touches
the hardware; everything else runs without it (the icon is grey, actions still fire).

## Code

- C# 13 / .NET 10, nullable enabled, implicit usings. Windows Forms without a designer: the
  windows are built in code, keep it that way.
- The pipe protocol (`KB525CControl/Ipc/PipeProtocol.cs`) is compiled into both the tray app and
  the CLI as a linked file. Change it in one place; keep responses backwards compatible where
  reasonable.
- Only the documented *collaboration keys* feature report (`0x13`) is sent to the keyboard.
  Please do not add probing or firmware-related commands; one of the device's interfaces is a
  firmware-update path.
- Match the existing style: small classes with a summary comment saying what they are for, and
  log lines that tell the user what was decided and why.

## Pull requests

- One topic per PR, with a line in `CHANGELOG.md` under *Unreleased*.
- Describe how you tested it (real keyboard or `kb525c press`).
- The CI workflow builds both projects on `windows-latest`; it has to pass.

## Releasing (maintainer notes)

1. Bump `<Version>` in both `.csproj` files, move the *Unreleased* entries in `CHANGELOG.md`
   under the new version with today's date, commit.
2. Tag and push: `git tag v0.6.0 && git push origin main v0.6.0`.
3. The `build` workflow publishes the self-contained executables, zips them as
   `KB525CControl-<version>-win-x64.zip` and creates the GitHub release with install instructions
   and auto-generated notes. Check the release page, edit the notes if needed.

A local dry run of the same package: `tools\publish.ps1`, then `Compress-Archive dist\*`.

## Licence

By contributing you agree that your contribution is licensed under the MIT License of this
repository.
