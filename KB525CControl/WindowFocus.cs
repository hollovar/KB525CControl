using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KB525CControl;

/// <summary>
/// Brings the main window of a running program to the front. Windows does not let a
/// background process simply steal focus; the usual workarounds are used
/// (AttachThreadInput, then SwitchToThisWindow as a fallback).
/// </summary>
public static class WindowFocus
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int SW_RESTORE = 9;

    /// <summary>A few common programs whose process name differs from the launch command.</summary>
    static readonly Dictionary<string, string> LaunchAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsTerminal"] = "wt.exe",
        ["Code"] = "code",
        ["ms-teams"] = "ms-teams:",
        ["Teams"] = "ms-teams:",
    };

    /// <summary>
    /// Brings the main window of the program to the front, by process name
    /// (e.g. "WindowsTerminal", "Code", "chrome.exe" or a full path).
    /// False when no such process with a window is running.
    /// </summary>
    public static bool BringToFront(string processNameOrPath)
    {
        var name = Path.GetFileNameWithoutExtension(processNameOrPath.Trim());
        if (name.Length == 0) return false;

        Process[] procs;
        try { procs = Process.GetProcessesByName(name); }
        catch { return false; }

        try
        {
            var hwnd = procs
                .Select(p => { try { return p.MainWindowHandle; } catch { return IntPtr.Zero; } })
                .FirstOrDefault(h => h != IntPtr.Zero);
            return hwnd != IntPtr.Zero && Force(hwnd);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>Brings the window to the front, or launches the program when it is not running.</summary>
    public static bool FocusOrLaunch(string value, out string what)
    {
        if (BringToFront(value))
        {
            what = $"brought to front: {value}";
            return true;
        }

        var name = Path.GetFileNameWithoutExtension(value.Trim());
        var launch = LaunchAlias.TryGetValue(name, out var alias) ? alias : value;
        try
        {
            Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true });
            what = $"launched: {launch}";
            return true;
        }
        catch (Exception ex)
        {
            what = $"not running and could not be launched ({launch}): {ex.Message}";
            return false;
        }
    }

    public static bool Force(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != me && AttachThreadInput(fgThread, me, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, me, false);
        }

        if (GetForegroundWindow() == hwnd) return true;

        SwitchToThisWindow(hwnd, true);
        return GetForegroundWindow() == hwnd;
    }
}
