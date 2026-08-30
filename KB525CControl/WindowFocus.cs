using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KB525CControl;

/// <summary>
/// Brings the main window of a running program to the front. Windows does not let a
/// background process simply steal focus; the usual workarounds are used
/// (AttachThreadInput, then SwitchToThisWindow as a fallback).
///
/// The same restriction applies to programs the tray app starts: the new window is created
/// but not activated, because the creator (this app, woken by a HID report - not by "user
/// input" in Windows' eyes) has no right to hand over the foreground. Snapshot / WaitForNewWindow
/// exist for that: take a snapshot before the launch, wait for a top-level window that was not
/// there, then Force it.
/// </summary>
public static class WindowFocus
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const int SW_RESTORE = 9;
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x00000080;
    const long WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_CLOAKED = 14;

    /// <summary>A few common programs whose process name differs from the launch command.</summary>
    static readonly Dictionary<string, string> LaunchAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsTerminal"] = "wt.exe",
        ["Code"] = "code",
        ["ms-teams"] = "ms-teams:",
        ["Teams"] = "ms-teams:",
    };

    /// <summary>The reverse: launch command -> the process that ends up owning the window.</summary>
    static readonly Dictionary<string, string> ProcessAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wt"] = "WindowsTerminal",
    };

    /// <summary>The process name to look for after launching <paramref name="command"/> (wt -> WindowsTerminal).</summary>
    public static string ProcessNameFor(string command)
    {
        var name = Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));
        return ProcessAlias.TryGetValue(name, out var alias) ? alias : name;
    }

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

    /// <summary>
    /// Brings the window to the front, or launches the program when it is not running.
    /// <paramref name="launched"/> tells which one happened, so the caller can follow a
    /// launch up with <see cref="WaitForNewWindow"/>.
    /// </summary>
    public static bool FocusOrLaunch(string value, out string what, out bool launched)
    {
        launched = false;
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
            launched = true;
            return true;
        }
        catch (Exception ex)
        {
            what = $"not running and could not be launched ({launch}): {ex.Message}";
            return false;
        }
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    public static bool StillExists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

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

    // --- new-window detection after a launch ---------------------------------------

    /// <summary>The visible top-level windows right now; take it just before starting a program.</summary>
    public static HashSet<IntPtr> Snapshot()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    /// <summary>
    /// Waits (blocking - call it off the UI thread) until a visible, activatable top-level
    /// window shows up that was not in <paramref name="before"/>. The app's own windows (OSD),
    /// tool windows, owned dialogs, no-activate and cloaked windows do not count. Returns
    /// IntPtr.Zero after <paramref name="timeoutMs"/>.
    /// </summary>
    public static IntPtr WaitForNewWindow(HashSet<IntPtr> before, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var found = FirstNewWindow(before);
            if (found != IntPtr.Zero)
            {
                // Give the program a moment: a splash screen may be followed by the real window.
                Thread.Sleep(300);
                var again = FirstNewWindow(before);
                return again != IntPtr.Zero ? again : found;
            }
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    static IntPtr FirstNewWindow(HashSet<IntPtr> before)
    {
        var ownPid = (uint)Environment.ProcessId;
        var titled = IntPtr.Zero;
        var untitled = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (before.Contains(h) || !IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == ownPid) return true;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            // EnumWindows walks the Z-order from the top: the first titled newcomer wins.
            if (GetWindowTextLength(h) > 0) { titled = h; return false; }
            if (untitled == IntPtr.Zero) untitled = h;
            return true;
        }, IntPtr.Zero);
        return titled != IntPtr.Zero ? titled : untitled;
    }
}
