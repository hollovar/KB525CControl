namespace KB525CControl;

/// <summary>
/// Plays LED signals on the keyboard, layered on top of the base state (meeting LEDs).
///
/// Two kinds of signal:
///   - transient: a pattern with a finite repeat count; they play one after another
///   - sticky: an infinite pattern; it runs until someone stops it, a collab button press
///     acknowledges it, or its timeout expires. Only one sticky signal lives at a time;
///     transient signals play "on top" of it, then the sticky one resumes.
///
/// When nothing is playing, the base state goes out to the device. This class is the ONLY
/// writer towards Kb525cDevice.Apply, so the meeting logic and the signals never race.
/// </summary>
public sealed class SignalEngine : IDisposable
{
    public sealed class Signal
    {
        public int Id { get; init; }
        public LedPattern Pattern { get; init; } = null!;
        public string? Label { get; init; }
        public int Priority { get; init; }
        /// <summary>Action to run on acknowledgement, e.g. "focus:WindowsTerminal".</summary>
        public string? OnAck { get; init; }
        /// <summary>Maximum lifetime of a sticky signal; 0 = unlimited.</summary>
        public int TimeoutMs { get; init; }
        public DateTime StartedAt { get; } = DateTime.Now;
        public bool Sticky => Pattern.IsInfinite;
        internal bool Announced;

        public override string ToString() =>
            $"#{Id} {(string.IsNullOrWhiteSpace(Label) ? Pattern.Source : Label)}";
    }

    readonly Kb525cDevice _device;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly CancellationTokenSource _cts = new();
    readonly List<Signal> _queue = new();
    Thread? _thread;

    bool _baseEnabled;
    CollabLed _baseLeds;
    Signal? _sticky;
    Signal? _current;
    int _nextId = 1;
    int _gen;

    public event Action<string>? Log;

    /// <summary>Something changed (a signal started or ended). For UI refresh.</summary>
    public event Action? StateChanged;

    public SignalEngine(Kb525cDevice device) => _device = device;

    public Signal? StickySignal { get { lock (_gate) return _sticky; } }
    public Signal? Playing { get { lock (_gate) return _current ?? _sticky; } }
    public int QueuedCount { get { lock (_gate) return _queue.Count; } }

    /// <summary>The LED state most recently sent to the device (for log / status).</summary>
    public CollabLed LastLeds { get; private set; }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "KB525C signals" };
        _thread.Start();
    }

    /// <summary>The base state (what the meeting logic wants). Visible only when no signal plays.</summary>
    public void SetBase(bool enabled, CollabLed leds)
    {
        lock (_gate)
        {
            if (_baseEnabled == enabled && _baseLeds == leds) return;
            _baseEnabled = enabled;
            _baseLeds = leds;
            BumpLocked();
        }
    }

    /// <summary>
    /// New signal. A sticky (infinite) pattern replaces any earlier sticky signal, unless
    /// that one has a higher priority - then null is returned.
    /// </summary>
    public Signal? Play(LedPattern pattern, string? label = null, int priority = 0,
        string? onAck = null, int timeoutMs = 0)
    {
        lock (_gate)
        {
            var sig = new Signal
            {
                Id = _nextId++,
                Pattern = pattern,
                Label = label,
                Priority = priority,
                OnAck = onAck,
                TimeoutMs = timeoutMs
            };

            if (sig.Sticky)
            {
                if (_sticky is not null && _sticky.Priority > priority)
                {
                    Log?.Invoke($"signal dropped (lower priority than {_sticky}): {sig}");
                    return null;
                }
                if (_sticky is not null)
                    Log?.Invoke($"sticky signal replaced: {_sticky} -> {sig}");
                _sticky = sig;
            }
            else
            {
                // Higher priority moves forward in the queue; equal priority is FIFO.
                var idx = _queue.Count;
                while (idx > 0 && _queue[idx - 1].Priority < priority) idx--;
                _queue.Insert(idx, sig);
            }

            BumpLocked();
            return sig;
        }
    }

    /// <summary>Stops one signal (id) or all of them (null). Returns how many were stopped.</summary>
    public int Cancel(int? id)
    {
        lock (_gate)
        {
            var n = 0;
            if (id is null)
            {
                n = _queue.Count + (_current is null ? 0 : 1) + (_sticky is null ? 0 : 1);
                _queue.Clear();
                _current = null;
                _sticky = null;
            }
            else
            {
                n += _queue.RemoveAll(s => s.Id == id);
                if (_current?.Id == id) { _current = null; n++; }
                if (_sticky?.Id == id) { _sticky = null; n++; }
            }
            if (n > 0) BumpLocked();
            return n;
        }
    }

    /// <summary>
    /// A button was pressed while something was blinking: the press is the acknowledgement.
    /// A sticky signal is dismissed (and its onAck action returned through the signal); a
    /// transient signal that is still playing is stopped together with the queue. False
    /// when nothing was playing.
    /// </summary>
    public bool TryAck(out Signal? acknowledged)
    {
        lock (_gate)
        {
            acknowledged = _sticky ?? _current;
            if (acknowledged is null) return false;

            Log?.Invoke($"signal acknowledged by button: {acknowledged}");
            _sticky = null;
            _current = null;
            _queue.Clear();
            BumpLocked();
            return true;
        }
    }

    // --- player thread ---------------------------------------------------------------

    void BumpLocked()
    {
        _gen++;
        _wake.Set();
    }

    void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Signal? s;
            bool isSticky;
            lock (_gate)
            {
                ExpireStickyLocked();
                if (_current is null && _queue.Count > 0)
                {
                    _current = _queue[0];
                    _queue.RemoveAt(0);
                }
                s = _current ?? _sticky;
                isSticky = s is not null && ReferenceEquals(s, _sticky);
            }

            if (s is null)
            {
                ApplyBase();
                StateChanged?.Invoke();
                _wake.WaitOne();
                continue;
            }

            if (!s.Announced)
            {
                s.Announced = true;
                Log?.Invoke($"signal start: {s} [{s.Pattern.Source}]{(isSticky ? " (sticky)" : "")}");
                StateChanged?.Invoke();
            }

            var completed = PlayOnce(s, isSticky);

            if (completed)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_current, s)) _current = null;
                }
                Log?.Invoke($"signal end: {s}");
            }
        }
    }

    void ApplyBase()
    {
        bool en; CollabLed leds;
        lock (_gate) { en = _baseEnabled; leds = _baseLeds; }
        Apply(en, leds);
    }

    void Apply(bool enabled, CollabLed leds)
    {
        LastLeds = enabled ? leds : CollabLed.None;
        _device.Apply(enabled, leds);
    }

    /// <summary>Plays the signal. True when it ran to completion; false when interrupted or sticky.</summary>
    bool PlayOnce(Signal s, bool isSticky)
    {
        int gen;
        lock (_gate) gen = _gen;

        var cycles = s.Pattern.Repeat;
        for (var c = 0; cycles < 0 || c < cycles; c++)
        {
            foreach (var frame in s.Pattern.Frames)
            {
                // Buttons are enabled while a signal plays so that acknowledging works.
                Apply(true, frame.Leds);

                var wait = frame.Ms <= 0 ? Timeout.Infinite : frame.Ms;
                if (isSticky && s.TimeoutMs > 0)
                {
                    var remaining = s.TimeoutMs - (int)(DateTime.Now - s.StartedAt).TotalMilliseconds;
                    if (remaining <= 0) return false;
                    wait = wait == Timeout.Infinite ? remaining : Math.Min(wait, remaining);
                }

                _wake.WaitOne(wait);
                if (_cts.IsCancellationRequested) return false;

                lock (_gate)
                {
                    if (_gen == gen) continue;
                    gen = _gen;

                    // Something changed. Leave if this signal is no longer current;
                    // keep going if only the base state changed.
                    if (isSticky)
                    {
                        if (!ReferenceEquals(_sticky, s) || _current is not null || _queue.Count > 0)
                            return false;
                    }
                    else if (!ReferenceEquals(_current, s))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    void ExpireStickyLocked()
    {
        if (_sticky is null || _sticky.TimeoutMs <= 0) return;
        if ((DateTime.Now - _sticky.StartedAt).TotalMilliseconds < _sticky.TimeoutMs) return;
        Log?.Invoke($"sticky signal expired: {_sticky}");
        _sticky = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        try { _thread?.Join(1000); } catch { }
        _cts.Dispose();
        _wake.Dispose();
    }
}
