using HidSharp;

namespace KB525CControl;

/// <summary>Which collaboration button was pressed. Values are the bitmask in input report byte[5].</summary>
public enum CollabButton : byte
{
    None = 0x00,
    Video = 0x02,
    Share = 0x04,
    Chat = 0x08,
    Mic = 0x10
}

/// <summary>
/// LED state bits. Only Video and Mic are toggles and therefore have a red state;
/// Share and Chat are action buttons with a white bit only.
/// </summary>
[Flags]
public enum CollabLed : byte
{
    None = 0x00,
    VideoWhite = 0x01,
    VideoRed = 0x02,
    ShareWhite = 0x04,
    ChatWhite = 0x08,
    MicWhite = 0x10,
    MicRed = 0x20,

    AllWhite = VideoWhite | ShareWhite | ChatWhite | MicWhite
}

/// <summary>
/// Drives the Dell KB525C collaboration buttons over its vendor HID interfaces.
///
///   command : MI_02&amp;Col02, SET_FEATURE, report ID 0x08
///             08 13 00 0F 05 EN 00 00 00 LD 00 ...   EN = 0xFF buttons on / 0x00 off, LD = LED bitmask
///   event   : MI_02&amp;Col01, input report ID 0x01
///             01 0C 00 01 91 MM 00 ...               MM = button bitmask, 0x00 = release
///
/// Handles hot-plug: if the device disappears it keeps retrying, and restores the last
/// LED state when it comes back.
/// </summary>
public sealed class Kb525cDevice : IDisposable
{
    public const int VendorId = 0x413C;
    public const int ProductId = 0x2123;

    const byte FeatureReportId = 0x08;
    const byte CmdCollabKeys = 0x13;

    const string FeaturePathMarker = "mi_02&col02";
    const string InputPathMarker = "mi_02&col01";

    readonly object _gate = new();
    readonly CancellationTokenSource _cts = new();

    HidStream? _featureStream;
    HidStream? _inputStream;
    int _featureLength;

    Thread? _readThread;
    Thread? _watchThread;

    bool _enabled;
    CollabLed _leds = CollabLed.None;

    /// <summary>A collaboration button was pressed. Releases do not raise this event.</summary>
    public event Action<CollabButton>? ButtonPressed;

    /// <summary>Device availability changed (plugged in / unplugged).</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// The connection error changed: a message when something prevents the connection
    /// (keyboard missing, wrong model, channel busy), null when the error cleared.
    /// </summary>
    public event Action<string?>? ErrorChanged;

    /// <summary>Diagnostic message.</summary>
    public event Action<string>? Log;

    public bool IsConnected { get; private set; }

    /// <summary>The last error that prevented connecting. Null when everything is fine.</summary>
    public string? LastError { get; private set; }

    public void Start()
    {
        _watchThread = new Thread(WatchLoop) { IsBackground = true, Name = "KB525C watch" };
        _watchThread.Start();
    }

    /// <summary>Enables or disables the collaboration buttons and sets the LEDs.</summary>
    public void Apply(bool enabled, CollabLed leds)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _leds = enabled ? leds : CollabLed.None;
            SendCurrentLocked();
        }
    }

    void SendCurrentLocked()
    {
        var stream = _featureStream;
        if (stream is null || _featureLength <= 0) return;

        var buf = new byte[_featureLength];
        buf[0] = FeatureReportId;
        buf[1] = CmdCollabKeys;
        buf[2] = 0x00;
        buf[3] = 0x0F;
        buf[4] = 0x05;
        buf[5] = _enabled ? (byte)0xFF : (byte)0x00;
        buf[9] = _enabled ? (byte)_leds : (byte)0x00;

        try
        {
            stream.SetFeature(buf);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"SET_FEATURE failed: {ex.GetType().Name}: {ex.Message}");
            DropConnectionLocked();
        }
    }

    // --- connection and watching ------------------------------------------------

    void WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            bool connected;
            lock (_gate) connected = _featureStream is not null && _inputStream is not null;

            if (!connected) TryConnect();

            try { Task.Delay(2000, _cts.Token).Wait(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (AggregateException) { return; }
        }
    }

    void TryConnect()
    {
        List<HidDevice> all;
        try { all = DeviceList.Local.GetHidDevices().ToList(); }
        catch (Exception ex)
        {
            SetError($"HID enumeration failed: {ex.Message}");
            return;
        }

        var devices = all.Where(d => d.VendorID == VendorId && d.ProductID == ProductId).ToList();

        if (devices.Count == 0)
        {
            // Tell the user whether it is missing altogether or simply a different Dell model.
            var otherDell = all.Where(d => d.VendorID == VendorId)
                .Select(d => d.ProductID).Distinct().OrderBy(p => p).ToList();

            SetError(otherDell.Count == 0
                ? "Dell KB525C not detected. Plug in the keyboard."
                : "A Dell device is connected, but it is not a KB525C (PID "
                  + string.Join(", ", otherDell.Select(p => $"0x{p:X4}")) + ").");
            return;
        }

        var featureDev = devices.FirstOrDefault(d =>
            d.DevicePath.Contains(FeaturePathMarker, StringComparison.OrdinalIgnoreCase));
        var inputDev = devices.FirstOrDefault(d =>
            d.DevicePath.Contains(InputPathMarker, StringComparison.OrdinalIgnoreCase));

        // Almost certainly Dell Peripheral Manager, which opens the interface exclusively.
        const string busy =
            "The command channel is busy. Disable the DPMService (Dell Peripheral Manager) service.";

        if (featureDev is null && inputDev is not null)
        {
            // Both collections belong to the same USB interface, so the command collection
            // exists whenever the event collection does. It is absent from the list only
            // because an exclusive owner makes even the attribute query fail with a sharing
            // violation, and HidSharp skips devices it cannot query. Newer Dell Peripheral
            // Manager builds hold it this way.
            SetError(busy);
            return;
        }

        if (featureDev is null || inputDev is null)
        {
            SetError("KB525C found, but its vendor interfaces are missing (unsupported firmware?).");
            return;
        }

        if (!featureDev.TryOpen(out var featureStream))
        {
            SetError(busy);
            return;
        }

        if (!inputDev.TryOpen(out var inputStream))
        {
            featureStream.Dispose();
            SetError("The event channel could not be opened.");
            return;
        }

        int featureLength;
        try { featureLength = featureDev.GetMaxFeatureReportLength(); }
        catch { featureLength = 32; }

        inputStream.ReadTimeout = Timeout.Infinite;

        bool hadError;
        lock (_gate)
        {
            _featureStream = featureStream;
            _inputStream = inputStream;
            _featureLength = featureLength;
            hadError = LastError is not null;
            LastError = null;
            // Restore the previous state (e.g. after re-plugging).
            SendCurrentLocked();
        }

        IsConnected = true;
        ConnectionChanged?.Invoke(true);
        if (hadError) ErrorChanged?.Invoke(null);
        Log?.Invoke("KB525C connected.");

        _readThread = new Thread(() => ReadLoop(inputStream, inputDev))
        { IsBackground = true, Name = "KB525C read" };
        _readThread.Start();
    }

    void ReadLoop(HidStream stream, HidDevice device)
    {
        int max;
        try { max = device.GetMaxInputReportLength(); } catch { max = 32; }
        var buf = new byte[Math.Max(8, max)];

        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = stream.Read(buf, 0, buf.Length); }
            catch
            {
                lock (_gate) DropConnectionLocked();
                return;
            }

            // 01 0C 00 01 91 MM ...
            if (n < 6 || buf[0] != 0x01 || buf[4] != 0x91) continue;

            var mask = buf[5];
            if (mask == 0) continue;   // release

            if (Enum.IsDefined(typeof(CollabButton), mask))
                ButtonPressed?.Invoke((CollabButton)mask);
            else
                Log?.Invoke($"unknown button bitmask: 0x{mask:X2}");
        }
    }

    void DropConnectionLocked()
    {
        try { _featureStream?.Dispose(); } catch { }
        try { _inputStream?.Dispose(); } catch { }
        _featureStream = null;
        _inputStream = null;

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
            Log?.Invoke("KB525C disconnected.");
        }
    }

    void SetError(string message)
    {
        if (LastError == message) return;
        LastError = message;
        Log?.Invoke(message);
        ErrorChanged?.Invoke(message);
    }

    /// <summary>Forces a reconnect, e.g. after re-plugging or waking from sleep.</summary>
    public void Reconnect()
    {
        lock (_gate) DropConnectionLocked();
    }

    public void Dispose()
    {
        // Always disable on exit, otherwise the LEDs stay lit.
        try
        {
            lock (_gate)
            {
                _enabled = false;
                _leds = CollabLed.None;
                SendCurrentLocked();
            }
        }
        catch { }

        _cts.Cancel();
        lock (_gate) DropConnectionLocked();
        _cts.Dispose();
    }
}
