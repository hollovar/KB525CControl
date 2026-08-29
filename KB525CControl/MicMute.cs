using NAudio.CoreAudioApi;

namespace KB525CControl;

/// <summary>
/// Mutes the Windows default communications microphone.
///
/// This mutes at the OS level, not inside the application. The upside: it works with EVERY
/// meeting app (Teams, Google Meet, Zoom, Discord) and needs no window focus. The downside:
/// the meeting app still shows you as "unmuted", the others simply hear silence.
/// </summary>
public sealed class MicMute : IDisposable
{
    MMDeviceEnumerator? _enumerator;

    MMDevice? GetDevice()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when muted. Null when there is no capture device.</summary>
    public bool? IsMuted
    {
        get
        {
            using var dev = GetDevice();
            try { return dev?.AudioEndpointVolume.Mute; } catch { return null; }
        }
    }

    /// <summary>Toggles mute and returns the new state.</summary>
    public bool? Toggle()
    {
        using var dev = GetDevice();
        if (dev is null) return null;
        try
        {
            var next = !dev.AudioEndpointVolume.Mute;
            dev.AudioEndpointVolume.Mute = next;
            return next;
        }
        catch
        {
            return null;
        }
    }

    public void Set(bool muted)
    {
        using var dev = GetDevice();
        if (dev is null) return;
        try { dev.AudioEndpointVolume.Mute = muted; } catch { }
    }

    public void Dispose()
    {
        try { _enumerator?.Dispose(); } catch { }
        _enumerator = null;
    }
}
