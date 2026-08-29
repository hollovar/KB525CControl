using KB525CControl.Ipc;

namespace KB525CControl;

static class Program
{
    static Mutex? _single;

    [STAThread]
    static void Main(string[] args)
    {
        // With arguments it does not run as a tray app; it forwards the command to the
        // running instance instead. (The full command-line interface is kb525c.exe.)
        if (args.Length > 0)
        {
            Forward(args);
            return;
        }

        // Only one instance may run, otherwise two would send LED commands.
        _single = new Mutex(true, @"Local\KB525CControl", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("KB525C Control is already running.", "KB525C Control",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ScrubInheritedEnvironment();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());

        _single.ReleaseMutex();
        _single.Dispose();
    }

    /// <summary>
    /// Every program a button launches inherits this process's environment. When the tray
    /// app itself was started from a Claude Code terminal (install.ps1, a dev restart), the
    /// CLAUDE_* session markers would make a launched claude.exe believe it is a child
    /// session (transcript saving off). Drop them so launched programs start clean.
    /// </summary>
    static void ScrubInheritedEnvironment()
    {
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
        {
            if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable(key, null);
        }
    }

    /// <summary>
    ///   KB525CControl.exe signal &lt;pattern&gt; [--sticky]
    ///   KB525CControl.exe off
    ///   KB525CControl.exe reload
    /// </summary>
    static void Forward(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        IpcRequest? req = cmd switch
        {
            "signal" when args.Length > 1 => new IpcRequest
            {
                Cmd = "signal",
                Pattern = args[1],
                Sticky = args.Contains("--sticky", StringComparer.OrdinalIgnoreCase) ? true : null
            },
            "off" => new IpcRequest { Cmd = "off" },
            "reload" => new IpcRequest { Cmd = "reload" },
            _ => null
        };

        if (req is null) return;
        try { PipeClient.Send(req); } catch { }
    }
}
