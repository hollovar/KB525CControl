using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace KB525CControl.Ipc;

/// <summary>
/// Named pipe server inside the tray app. Every connection reads one JSON line, calls the
/// handler, writes one JSON line back and disconnects.
///
/// Only the logged-in user can connect (the pipe ACL is restricted to them).
/// </summary>
public sealed class PipeServer : IDisposable
{
    readonly Func<IpcRequest, IpcResponse> _handler;
    readonly CancellationTokenSource _cts = new();

    public event Action<string>? Log;

    /// <summary>Null when the pipe exists; otherwise the reason it does not.</summary>
    public string? LastError { get; private set; }

    public PipeServer(Func<IpcRequest, IpcResponse> handler) => _handler = handler;

    public void Start() => Task.Run(AcceptLoop);

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = Create();
                if (LastError is not null)
                {
                    LastError = null;
                    Log?.Invoke("pipe: available");
                }
            }
            catch (Exception ex)
            {
                var msg = $"pipe could not be created: {ex.Message}";
                if (LastError != msg) { LastError = msg; Log?.Invoke(msg); }
                try { await Task.Delay(5000, _cts.Token); } catch { return; }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                server.Dispose();
                Log?.Invoke($"pipe: connection error: {ex.Message}");
                try { await Task.Delay(500, _cts.Token); } catch { return; }
                continue;
            }

            _ = Task.Run(() => Handle(server));
        }
    }

    NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User
                 ?? throw new InvalidOperationException("no user SID");
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    async Task Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(3000);

                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync(timeout.Token);
                if (line is null) return;

                IpcResponse response;
                IpcRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<IpcRequest>(line, PipeProtocol.Json);
                }
                catch (JsonException ex)
                {
                    response = IpcResponse.Fail("malformed JSON: " + ex.Message);
                    await WriteAsync(pipe, response, timeout.Token);
                    return;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Cmd))
                    response = IpcResponse.Fail("missing cmd field");
                else
                {
                    try { response = _handler(request); }
                    catch (Exception ex) { response = IpcResponse.Fail(ex.Message); }
                }

                await WriteAsync(pipe, response, timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log?.Invoke($"pipe: handler error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    static async Task WriteAsync(NamedPipeServerStream pipe, IpcResponse response, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, PipeProtocol.Json) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
        // Wait until the client has read it, otherwise disconnecting may discard the response.
        try { pipe.WaitForPipeDrain(); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
