using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KB525CControl;

/// <summary>
/// Finds the folder the user is "in":
///   1. Total Commander in the foreground: the folder of its active (source) panel.
///   2. The Explorer window in the foreground.
///   3. The Desktop when the desktop is focused.
///   4. The most recently opened Explorer window.
///   5. The user profile.
///
/// Explorer windows come from the Shell.Application COM object (late bound), which lists
/// every open window with its handle and folder. Total Commander has no such API; see
/// <see cref="TotalCommander"/>.
/// </summary>
public static class ActiveFolder
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    /// <summary>Returns a folder that exists, and says where it came from.</summary>
    public static string Detect(out string source)
    {
        var fg = GetForegroundWindow();

        var tc = TotalCommander.ActiveFolderIfInFront(fg, out var tcSource);
        if (tc is not null)
        {
            source = tcSource;
            return tc;
        }

        var cls = new StringBuilder(64);
        if (fg != IntPtr.Zero) GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW")
        {
            source = "desktop";
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        string? fallback = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic windows = shell.Windows();
                int count = windows.Count;

                // Enumerate from the end: the most recently opened window comes last.
                for (var i = count - 1; i >= 0; i--)
                {
                    try
                    {
                        dynamic w = windows.Item(i);
                        if (w is null) continue;

                        var path = FolderOf(w);
                        if (path is null) continue;

                        var hwnd = (IntPtr)Convert.ToInt64(w.HWND);
                        if (hwnd == fg)
                        {
                            source = "Explorer window in front";
                            return path;
                        }
                        fallback ??= path;
                    }
                    catch
                    {
                        // Not an Explorer window (e.g. Internet Explorer) or already closed.
                    }
                }
            }
        }
        catch
        {
            // Shell automation unavailable - fall through.
        }

        if (fallback is not null)
        {
            source = "last Explorer window";
            return fallback;
        }

        source = "user profile";
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    static string? FolderOf(dynamic window)
    {
        dynamic doc = window.Document;
        if (doc is null) return null;
        dynamic folder = doc.Folder;
        if (folder is null) return null;
        string? path = folder.Self.Path;
        return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
    }
}

/// <summary>
/// Reads the active panel's folder out of a running Total Commander.
///
/// Total Commander has no automation API, but its command-line prompt (the label left of
/// the command line, e.g. "c:\Users\me\Documents>") always shows the SOURCE panel's
/// folder. We read that child window's text. When the command line is hidden we fall
/// back to the documented "send a cm_ command" interface (WM_USER+51) with
/// cm_CopySrcPathToClip, restoring the clipboard afterwards.
/// </summary>
public static class TotalCommander
{
    const uint WM_GETTEXT = 0x000D;
    const uint WM_USER = 0x0400;
    const int CM_COPYSRCPATHTOCLIP = 2029;   // TOTALCMD.INC: cm_CopySrcPathToClip

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The active panel's folder when the foreground window belongs to Total Commander
    /// (its main window, Lister, a dialog...), otherwise null.
    /// </summary>
    public static string? ActiveFolderIfInFront(IntPtr foreground, out string source)
    {
        source = "";
        if (foreground == IntPtr.Zero) return null;

        Process proc;
        try
        {
            GetWindowThreadProcessId(foreground, out var pid);
            if (pid == 0) return null;
            proc = Process.GetProcessById((int)pid);
            if (!proc.ProcessName.StartsWith("totalcmd", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch
        {
            return null;
        }

        var main = proc.MainWindowHandle;
        if (main == IntPtr.Zero) return null;

        var fromPrompt = ReadPromptFolder(main);
        if (fromPrompt is not null)
        {
            source = "Total Commander (active panel)";
            return fromPrompt;
        }

        var fromClipboard = ReadViaClipboard(main);
        if (fromClipboard is not null)
        {
            source = "Total Commander (active panel, via cm_CopySrcPathToClip)";
            return fromClipboard;
        }

        return null;
    }

    /// <summary>The folder shown by the command-line prompt label ("C:\path>").</summary>
    static string? ReadPromptFolder(IntPtr main)
    {
        string? found = null;
        var buf = new StringBuilder(4096);

        EnumChildWindows(main, (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            buf.Clear();
            SendMessage(h, WM_GETTEXT, (IntPtr)buf.Capacity, buf);
            var text = buf.ToString();
            if (text.Length < 3 || text[^1] != '>') return true;

            var path = text[..^1];
            if (LooksLikePath(path) && Directory.Exists(path))
            {
                found = Path.GetFullPath(path);
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    static bool LooksLikePath(string s) =>
        (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':') || s.StartsWith(@"\\");

    /// <summary>Asks Total Commander to copy the source path, then puts the clipboard back.</summary>
    static string? ReadViaClipboard(IntPtr main)
    {
        try
        {
            IDataObject? previous = null;
            try { previous = Clipboard.GetDataObject(); } catch { }

            Clipboard.Clear();
            SendMessage(main, WM_USER + 51, (IntPtr)CM_COPYSRCPATHTOCLIP, IntPtr.Zero);

            string? path = null;
            for (var i = 0; i < 10 && path is null; i++)
            {
                Thread.Sleep(20);
                var text = Clipboard.GetText()?.Trim();
                if (!string.IsNullOrEmpty(text) && Directory.Exists(text)) path = Path.GetFullPath(text);
            }

            try
            {
                if (previous is not null) Clipboard.SetDataObject(previous, true);
                else Clipboard.Clear();
            }
            catch { }

            return path;
        }
        catch
        {
            return null;
        }
    }
}
