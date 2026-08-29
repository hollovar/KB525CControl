using Microsoft.Win32;

namespace KB525CControl;

/// <summary>
/// Tells whether any application is currently using the microphone or the camera.
///
/// Windows tracks this under the CapabilityAccessManager ConsentStore. Every app has a
/// LastUsedTimeStart and a LastUsedTimeStop value; when Stop is zero, the app is using the
/// device RIGHT NOW.
///
/// This is app-independent: Teams, Google Meet, Zoom, Discord all show up the same way.
/// No window-title watching and no process-name list needed.
/// </summary>
public sealed class MediaUsageMonitor : IDisposable
{
    const string ConsentRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    readonly System.Windows.Forms.Timer _timer = new();

    /// <summary>State changed. (microphone in use, camera in use)</summary>
    public event Action<bool, bool>? Changed;

    public bool MicInUse { get; private set; }
    public bool CameraInUse { get; private set; }

    /// <summary>Processes to ignore (e.g. our own measurement tools).</summary>
    public HashSet<string> Ignored { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MediaUsageMonitor(int pollMs = 1000)
    {
        _timer.Interval = pollMs;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        Poll();
        _timer.Start();
    }

    public void Poll()
    {
        var mic = IsInUse("microphone");
        var cam = IsInUse("webcam");

        if (mic == MicInUse && cam == CameraInUse) return;

        MicInUse = mic;
        CameraInUse = cam;
        Changed?.Invoke(mic, cam);
    }

    bool IsInUse(string capability)
    {
        // Packaged (Store) and classic desktop apps live in separate branches.
        return ScanBranch($@"{ConsentRoot}\{capability}")
               || ScanBranch($@"{ConsentRoot}\{capability}\NonPackaged");
    }

    bool ScanBranch(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null) return false;

            foreach (var name in key.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsIgnored(name)) continue;

                using var sub = key.OpenSubKey(name);
                if (sub is null) continue;

                // LastUsedTimeStop stays zero while the app is using the device.
                if (sub.GetValue("LastUsedTimeStop") is not long stop) continue;
                if (sub.GetValue("LastUsedTimeStart") is not long start) continue;

                if (stop == 0 && start > 0) return true;
            }
        }
        catch
        {
            // One unreadable key must not stop the scan.
        }
        return false;
    }

    bool IsIgnored(string subKeyName)
    {
        if (Ignored.Count == 0) return false;
        // NonPackaged key names use '#' in place of '\'.
        var normalized = subKeyName.Replace('#', '\\');
        return Ignored.Any(i => normalized.EndsWith(i, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
