using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KB525CControl.Ipc;

/// <summary>
/// Sends a request to the running tray app. One connection, one line out, one line in.
/// Also linked into the kb525c CLI.
/// </summary>
public static class PipeClient
{
    /// <summary>
    /// Sends the request and waits for the response.
    /// TimeoutException: the app is not running (no such pipe).
    /// IOException: the connection dropped without a response.
    /// </summary>
    public static IpcResponse Send(IpcRequest request, int connectTimeoutMs = 1500)
    {
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(connectTimeoutMs);

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, PipeProtocol.Json) + "\n");
        pipe.Write(payload, 0, payload.Length);
        pipe.Flush();

        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var line = reader.ReadLine() ?? throw new IOException("no response from KB525C Control");
        return JsonSerializer.Deserialize<IpcResponse>(line, PipeProtocol.Json)
               ?? throw new IOException("empty response from KB525C Control");
    }

    /// <summary>Is the tray app running (quick check).</summary>
    public static bool IsRunning(int timeoutMs = 300)
    {
        try { return Send(new IpcRequest { Cmd = "ping" }, timeoutMs).Ok; }
        catch { return false; }
    }
}
