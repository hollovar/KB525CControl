using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KB525CControl.Ipc;

/// <summary>
/// Protocol between the tray app and the outside world: a named pipe, one JSON per line.
///
///   \\.\pipe\KB525CControl
///   request:  {"cmd":"signal","pattern":"done"}\n
///   response: {"ok":true,"id":3}\n
///
/// One connection = one request + one response. Usable from any language that can write
/// to a named pipe (PowerShell, Python, Node, C#...). The kb525c.exe CLI does the same.
///
/// This file is also linked into the kb525c CLI project - do not use Windows-only APIs here.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "KB525CControl";
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

/// <summary>
/// Commands:
///   ping      - is the app alive
///   status    - state (keyboard, meeting, mute, signals)
///   patterns  - available named patterns
///   signal    - start a signal: pattern (name or pattern text), label, priority, ack, timeoutMs, sticky
///   off       - stop a signal: id (no id = all)
///   press     - simulate a button press: button (mic, video, share, chat)
///   reload    - reload config.json
/// </summary>
public sealed class IpcRequest
{
    public string Cmd { get; set; } = "";

    /// <summary>Pattern name ("done") or pattern text ("VSCM/----@150x3").</summary>
    public string? Pattern { get; set; }

    /// <summary>Human-readable label for the log and the status.</summary>
    public string? Label { get; set; }

    public int? Priority { get; set; }

    /// <summary>Action to run on acknowledgement: "focus:WindowsTerminal", "run:...", "hotkey:...".</summary>
    public string? Ack { get; set; }

    /// <summary>Maximum lifetime of a sticky signal in ms. 0 = unlimited.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>True: the pattern is made infinite (sticky) regardless of its repeat count.</summary>
    public bool? Sticky { get; set; }

    /// <summary>off: which signal.</summary>
    public int? Id { get; set; }

    /// <summary>press: which button to simulate (mic, video, share, chat).</summary>
    public string? Button { get; set; }

    /// <summary>log: number of most recent lines (default 40, max 500).</summary>
    public int? Tail { get; set; }
}

public sealed class IpcSignalInfo
{
    public int Id { get; set; }
    public string? Label { get; set; }
    public string Pattern { get; set; } = "";
    public bool Sticky { get; set; }
    public int Priority { get; set; }
    public string? Ack { get; set; }
    public string StartedAt { get; set; } = "";
}

public sealed class IpcStatus
{
    public string AppVersion { get; set; } = "";
    public bool Connected { get; set; }
    public string? DeviceError { get; set; }
    public string Mode { get; set; } = "";
    /// <summary>Which layer is live right now: "meeting", "idle" or "off".</summary>
    public string Layer { get; set; } = "";
    public bool ButtonsActive { get; set; }
    public bool MicInUse { get; set; }
    public bool CameraInUse { get; set; }
    public bool? Muted { get; set; }
    /// <summary>The LED state most recently sent to the device, in pattern form (e.g. "VSCM").</summary>
    public string Leds { get; set; } = "";
    public string? ForegroundApp { get; set; }
    public IpcSignalInfo? Playing { get; set; }
    public IpcSignalInfo? Sticky { get; set; }
    public int Queued { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int? Id { get; set; }
    public IpcStatus? Status { get; set; }
    public Dictionary<string, string>? Patterns { get; set; }

    public static IpcResponse Success(string? message = null, int? id = null) =>
        new() { Ok = true, Message = message, Id = id };

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
}
