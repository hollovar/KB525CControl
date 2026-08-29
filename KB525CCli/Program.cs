using System.Diagnostics;
using System.Text.Json;
using KB525CControl.Ipc;

// kb525c - command-line client for the KB525C Control tray app.
//
// Talks to the running app over a named pipe. If the app is not running it exits with
// code 2, except for "run": that still executes the command, just without signals.

return Cli.Dispatch(args);

static class Cli
{
    const int ExitOk = 0;
    const int ExitError = 1;
    const int ExitNotRunning = 2;
    const int ExitUsage = 64;

    public static int Dispatch(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            Usage();
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        var cmd = args[0].ToLowerInvariant();
        var rest = args[1..];

        try
        {
            return cmd switch
            {
                "signal" => Signal(rest),
                "off" or "cancel" => Off(rest),
                "status" => Status(rest),
                "patterns" => Patterns(),
                "ping" => Ping(),
                "reload" => Simple(new IpcRequest { Cmd = "reload" }),
                "settings" => Simple(new IpcRequest { Cmd = "settings" }),
                "log" => Simple(new IpcRequest { Cmd = "log", Tail = rest.Length > 0 && int.TryParse(rest[0], out var n) ? n : null }),
                "press" => Press(rest),
                "run" => Run(rest),
                "watch" => Watch(rest),
                _ => Unknown(cmd)
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("KB525C Control is not running (no \\\\.\\pipe\\" + PipeProtocol.PipeName + ").");
            return ExitNotRunning;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("Connection error: " + ex.Message);
            return ExitNotRunning;
        }
    }

    // --- commands ----------------------------------------------------------------

    static int Signal(string[] args)
    {
        var o = Opts.Parse(args, flags: new[] { "sticky" });
        if (o.Positional.Count == 0)
        {
            Console.Error.WriteLine("Missing pattern. Example: kb525c signal done");
            return ExitUsage;
        }

        var req = new IpcRequest
        {
            Cmd = "signal",
            Pattern = o.Positional[0],
            Label = o.Get("label"),
            Ack = o.Get("ack"),
            Priority = o.GetInt("priority"),
            TimeoutMs = o.GetInt("timeout"),
            Sticky = o.Has("sticky") ? true : null
        };

        var r = PipeClient.Send(req);
        if (!r.Ok) return Fail(r);
        Console.WriteLine(r.Message ?? $"signal #{r.Id}");
        return ExitOk;
    }

    static int Off(string[] args)
    {
        var o = Opts.Parse(args);
        int? id = null;
        if (o.Positional.Count > 0)
        {
            if (!int.TryParse(o.Positional[0], out var parsed))
            {
                Console.Error.WriteLine("The id must be a number.");
                return ExitUsage;
            }
            id = parsed;
        }
        return Simple(new IpcRequest { Cmd = "off", Id = id });
    }

    static int Press(string[] args)
    {
        var o = Opts.Parse(args);
        if (o.Positional.Count == 0)
        {
            Console.Error.WriteLine("Missing button. Example: kb525c press share");
            return ExitUsage;
        }
        return Simple(new IpcRequest { Cmd = "press", Button = o.Positional[0] });
    }

    static int Status(string[] args)
    {
        var o = Opts.Parse(args, flags: new[] { "json" });
        var r = PipeClient.Send(new IpcRequest { Cmd = "status" });
        if (!r.Ok || r.Status is null) return Fail(r);

        var s = r.Status;
        if (o.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions(PipeProtocol.Json) { WriteIndented = true }));
            return ExitOk;
        }

        Console.WriteLine($"KB525C Control {s.AppVersion}");
        Console.WriteLine($"  keyboard   : {(s.Connected ? "connected" : s.DeviceError ?? "not available")}");
        Console.WriteLine($"  mode       : {s.Mode}");
        Console.WriteLine($"  layer      : {s.Layer}   buttons: {(s.ButtonsActive ? "active" : "inactive")}   LEDs: {s.Leds}");
        Console.WriteLine($"  microphone : {(s.MicInUse ? "in use" : "free")}, {(s.Muted switch { true => "muted", false => "live", null => "?" })}");
        Console.WriteLine($"  camera     : {(s.CameraInUse ? "in use" : "free")}");
        Console.WriteLine($"  foreground : {s.ForegroundApp ?? "-"}");
        Console.WriteLine($"  signal     : {Describe(s.Playing)}");
        Console.WriteLine($"  sticky     : {Describe(s.Sticky)}   queued: {s.Queued}");
        return ExitOk;
    }

    static string Describe(IpcSignalInfo? i) =>
        i is null ? "-" : $"#{i.Id} {(string.IsNullOrEmpty(i.Label) ? "" : i.Label + " ")}[{i.Pattern}]";

    static int Patterns()
    {
        var r = PipeClient.Send(new IpcRequest { Cmd = "patterns" });
        if (!r.Ok || r.Patterns is null) return Fail(r);
        foreach (var (name, def) in r.Patterns.OrderBy(p => p.Key))
            Console.WriteLine($"  {name,-12} {def}");
        return ExitOk;
    }

    static int Ping()
    {
        var r = PipeClient.Send(new IpcRequest { Cmd = "ping" });
        Console.WriteLine(r.Ok ? "running" : "error: " + r.Error);
        return r.Ok ? ExitOk : ExitError;
    }

    static int Simple(IpcRequest req)
    {
        var r = PipeClient.Send(req);
        if (!r.Ok) return Fail(r);
        if (r.Message is not null) Console.WriteLine(r.Message);
        return ExitOk;
    }

    /// <summary>
    /// kb525c run [--label X] [--running P] [--done P] [--error P] -- command arg...
    /// Running light while the command runs, then "done" or "error" by exit code.
    /// Returns the command's exit code.
    /// </summary>
    static int Run(string[] args)
    {
        var sep = Array.IndexOf(args, "--");
        var optArgs = sep < 0 ? Array.Empty<string>() : args[..sep];
        var command = sep < 0 ? args : args[(sep + 1)..];

        // Also accepted without "--": kb525c run dotnet build
        if (sep < 0)
        {
            var firstPositional = Array.FindIndex(args, a => !a.StartsWith("--"));
            if (firstPositional > 0)
            {
                optArgs = args[..firstPositional];
                command = args[firstPositional..];
            }
        }

        if (command.Length == 0)
        {
            Console.Error.WriteLine("Missing command. Example: kb525c run -- dotnet build");
            return ExitUsage;
        }

        var o = Opts.Parse(optArgs);
        var label = o.Get("label") ?? Shorten(string.Join(' ', command), 60);
        var runningPattern = o.Get("running") ?? "running";
        var donePattern = o.Get("done") ?? "done";
        var errorPattern = o.Get("error") ?? "error";

        var live = PipeClient.IsRunning();
        if (!live) Console.Error.WriteLine("(KB525C Control is not running, executing without signals)");

        int? runningId = null;
        if (live)
        {
            var r = TrySend(new IpcRequest { Cmd = "signal", Pattern = runningPattern, Label = label, Sticky = true });
            runningId = r?.Id;
        }

        // Ctrl+C: the child receives it and stops; we wait for it and signal "error".
        Console.CancelKeyPress += (_, e) => e.Cancel = true;

        var code = Exec(command);

        if (live)
        {
            if (runningId is not null) TrySend(new IpcRequest { Cmd = "off", Id = runningId });
            TrySend(new IpcRequest
            {
                Cmd = "signal",
                Pattern = code == 0 ? donePattern : errorPattern,
                Label = $"{label} -> {code}"
            });
        }
        return code;
    }

    static int Exec(string[] command)
    {
        var psi = new ProcessStartInfo { FileName = command[0], UseShellExecute = false };
        foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("did not start");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // .cmd/.bat or a shell built-in: go through cmd.
            var line = string.Join(' ', command.Select(QuoteForCmd));
            var shell = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c " + line,
                UseShellExecute = false
            };
            try
            {
                using var p = Process.Start(shell) ?? throw new InvalidOperationException("did not start");
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("The command could not be started: " + ex.Message);
                return 127;
            }
        }
    }

    static string QuoteForCmd(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// kb525c watch --pid N [--label X] [--running P] [--then P] [--error P]
    /// Waits for an already running process and signals when it exits.
    /// </summary>
    static int Watch(string[] args)
    {
        var o = Opts.Parse(args);
        var pid = o.GetInt("pid");
        if (pid is null)
        {
            Console.Error.WriteLine("Missing --pid. Example: kb525c watch --pid 1234 --then done");
            return ExitUsage;
        }

        Process proc;
        try { proc = Process.GetProcessById(pid.Value); }
        catch
        {
            Console.Error.WriteLine($"No process with id {pid}.");
            return ExitError;
        }

        string label;
        try { label = o.Get("label") ?? $"{proc.ProcessName} ({pid})"; }
        catch { label = o.Get("label") ?? $"pid {pid}"; }

        var running = o.Get("running");
        int? runningId = null;
        if (running is not null)
            runningId = TrySend(new IpcRequest { Cmd = "signal", Pattern = running, Label = label, Sticky = true })?.Id;

        proc.WaitForExit();

        int? code = null;
        try { code = proc.ExitCode; } catch { }

        if (runningId is not null) TrySend(new IpcRequest { Cmd = "off", Id = runningId });

        var pattern = code is null or 0 ? (o.Get("then") ?? "done") : (o.Get("error") ?? "error");
        var r = PipeClient.Send(new IpcRequest { Cmd = "signal", Pattern = pattern, Label = $"{label} -> {code?.ToString() ?? "?"}" });
        return r.Ok ? ExitOk : Fail(r);
    }

    // --- helpers -------------------------------------------------------------------

    static IpcResponse? TrySend(IpcRequest req)
    {
        try
        {
            var r = PipeClient.Send(req);
            if (!r.Ok) Console.Error.WriteLine("KB525C Control: " + r.Error);
            return r;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("KB525C Control not reachable: " + ex.Message);
            return null;
        }
    }

    static int Fail(IpcResponse r)
    {
        Console.Error.WriteLine("Error: " + (r.Error ?? "unknown"));
        return ExitError;
    }

    static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Usage();
        return ExitUsage;
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static void Usage()
    {
        Console.WriteLine("""
kb525c - command-line control of the KB525C Control tray app

  kb525c signal <pattern> [--sticky] [--label <text>] [--ack <action>] [--timeout <ms>] [--priority <n>]
        LED signal. <pattern> is a name (done, error, attention, running, chase, pulse, red, all)
        or pattern text, e.g. "VSCM/----@150x5" or "V---/-S--/--C-/---M@80x3".
        --sticky    runs forever until a collab button acknowledges it or "off" is sent
        --ack       on acknowledgement: focus:<process name> | run:<command> | hotkey:<combination>
        --timeout   maximum lifetime of a sticky signal in ms

  kb525c off [<id>]                  stop signal(s)
  kb525c status [--json]             state
  kb525c patterns                    named patterns
  kb525c ping                        is the app running
  kb525c reload                      reload config.json
  kb525c settings                    open the settings window
  kb525c log [N]                     last N log lines of the tray app (default 40)
  kb525c press <mic|video|share|chat>   simulate a button press (for testing bindings)

  kb525c run [--label X] [--running P] [--done P] [--error P] -- <command> [arg...]
        Running light while the command runs, then done/error by exit code.
        Returns the command's exit code.

  kb525c watch --pid <n> [--running P] [--then P] [--error P]
        Waits for an already running process and signals when it exits.

Pattern language: frames separated by '/', one frame is 4 characters (Video Share Chat Mic),
upper case = white, lower case = red (v, m), '-' = dark. @ms = frame length, xN = repeat, x* = forever.

Exit codes: 0 ok, 1 error, 2 the app is not running, 64 usage error.
""");
    }
}

/// <summary>Simple --key value / --flag / positional argument parser.</summary>
sealed class Opts
{
    public List<string> Positional { get; } = new();
    readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public static Opts Parse(string[] args, string[]? flags = null)
    {
        var o = new Opts();
        var flagSet = new HashSet<string>(flags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--") && a.Length > 2)
            {
                var key = a[2..];
                var eq = key.IndexOf('=');
                if (eq > 0) { o._named[key[..eq]] = key[(eq + 1)..]; continue; }
                if (flagSet.Contains(key)) { o._named[key] = ""; continue; }
                if (i + 1 < args.Length) { o._named[key] = args[++i]; continue; }
                o._named[key] = "";
            }
            else
            {
                o.Positional.Add(a);
            }
        }
        return o;
    }

    public bool Has(string key) => _named.ContainsKey(key);
    public string? Get(string key) => _named.TryGetValue(key, out var v) && v is { Length: > 0 } ? v : null;
    public int? GetInt(string key) => int.TryParse(Get(key), out var n) ? n : null;
}
