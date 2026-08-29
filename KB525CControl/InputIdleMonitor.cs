using System.Runtime.InteropServices;

namespace KB525CControl;

/// <summary>
/// Tells whether the user has been away from the mouse and keyboard for longer than a
/// threshold. Drives the LED sleep of the idle layer.
///
/// The source is user32!GetLastInputInfo - the session-wide timestamp of the last mouse,
/// keyboard or touch input. No hook is installed and nothing about the input itself is
/// seen, only its time. Events the OS does not count as input (a collab button press, a
/// LED signal ending) are reported through Touch(), so they restart the countdown too.
/// </summary>
public sealed class InputIdleMonitor : IDisposable
{
    readonly System.Windows.Forms.Timer _timer = new();
    DateTime _lastTouch = DateTime.Now;
    bool _idle;

    /// <summary>The "idle for longer than the threshold" state flipped.</summary>
    public event Action<bool>? Changed;

    int _thresholdMs;

    /// <summary>Idle threshold in ms; 0 or less disables the monitor (never idle). A change re-evaluates at once.</summary>
    public int ThresholdMs
    {
        get => _thresholdMs;
        set
        {
            if (_thresholdMs == value) return;
            _thresholdMs = value;
            Poll();
        }
    }

    public bool IsIdle => _idle;

    public InputIdleMonitor(int pollMs = 1000)
    {
        _timer.Interval = pollMs;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    /// <summary>Time since the last input from any source.</summary>
    public TimeSpan IdleFor
    {
        get
        {
            var os = OsIdle();
            var touch = DateTime.Now - _lastTouch;
            return os < touch ? os : touch;
        }
    }

    /// <summary>Something happened that counts as activity (button press, signal finished).</summary>
    public void Touch()
    {
        _lastTouch = DateTime.Now;
        Poll();
    }

    public void Poll()
    {
        var idle = ThresholdMs > 0 && IdleFor.TotalMilliseconds >= ThresholdMs;
        if (idle == _idle) return;
        _idle = idle;
        Changed?.Invoke(idle);
    }

    static TimeSpan OsIdle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both are 32-bit tick counts; unchecked subtraction survives the 49-day wrap.
        var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
