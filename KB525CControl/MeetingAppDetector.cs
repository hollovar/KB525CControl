using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KB525CControl;

/// <summary>
/// Detects which meeting application is in the foreground so that the RIGHT shortcut
/// can be sent - every platform names the same action differently.
///
/// Returns only the app IDENTIFIER; the actual shortcuts live in the "apps" section of
/// config.json so they can be fixed without recompiling.
///
/// Limitation: a shortcut only reaches the focused window, so the meeting window must be
/// in front.
/// </summary>
public static class MeetingAppDetector
{
    public const string Meet = "meet";
    public const string Teams = "teams";
    public const string Zoom = "zoom";

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    /// <summary>Identifier of the meeting app in the foreground, or null.</summary>
    public static string? Detect()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        string process;
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch
        {
            return null;
        }

        if (process.Contains("teams", StringComparison.OrdinalIgnoreCase)) return Teams;
        if (process.Equals("Zoom", StringComparison.OrdinalIgnoreCase)) return Zoom;

        if (Browsers.Any(b => process.Contains(b, StringComparison.OrdinalIgnoreCase)))
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString();

            if (title.Contains("Meet", StringComparison.OrdinalIgnoreCase)) return Meet;
            if (title.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return Teams;
            if (title.Contains("Zoom", StringComparison.OrdinalIgnoreCase)) return Zoom;
        }

        return null;
    }

    public static string DisplayName(string key) => key switch
    {
        Meet => "Google Meet",
        Teams => "Microsoft Teams",
        Zoom => "Zoom",
        _ => key
    };
}
