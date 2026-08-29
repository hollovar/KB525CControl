# Named-pipe protocol

`kb525c.exe` is just a client. The tray app listens on the named pipe
**`\\.\pipe\KB525CControl`**; only the logged-in user can connect. This is the "plugin
interface": talk to it from any language.

**One connection = one JSON line in, one JSON line out.** Write the request terminated by `\n`,
read one line, close.

## Requests and responses

```jsonc
// request                                                     // response
{"cmd":"ping"}                                                 {"ok":true,"message":"pong"}
{"cmd":"status"}                                               {"ok":true,"status":{...}}
{"cmd":"patterns"}                                             {"ok":true,"patterns":{"done":"VSCM/----@150x3", ...}}
{"cmd":"signal","pattern":"done"}                              {"ok":true,"id":3,"message":"..."}
{"cmd":"signal","pattern":"attention","sticky":true,
 "ack":"focus:WindowsTerminal","label":"CI","timeoutMs":60000,
 "priority":1}                                                 {"ok":true,"id":4}
{"cmd":"off"}            // or {"cmd":"off","id":4}            {"ok":true,"message":"1 signal(s) stopped"}
{"cmd":"press","button":"share"}                               {"ok":true,"message":"Share pressed (Idle)"}
{"cmd":"log","tail":40}                                        {"ok":true,"message":"line\nline\n..."}
{"cmd":"settings"}                                             {"ok":true}
{"cmd":"reload"}                                               {"ok":true}
```

On error: `{"ok":false,"error":"..."}`.

`signal` fields: `pattern` (name or pattern text, required), `sticky` (bool), `label`, `ack`
(`focus:<process>` | `run:<command>` | `hotkey:<combination>`), `timeoutMs`, `priority`.
The definitions live in `KB525CControl/Ipc/PipeProtocol.cs`, which the CLI compiles in as a linked
file — the protocol has one source of truth.

The `status` object contains the version, whether the keyboard is connected, the mode and the
current layer, whether the buttons are enabled, the LED frame (`"VSCM"` style) and `ledsAsleep`
(the idle-layer LED sleep is holding them dark), microphone and camera state, the meeting app in front, and the playing / sticky / queued signals. `kb525c status
--json` prints it verbatim.

## Samples

PowerShell:

```powershell
$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'KB525CControl', 'InOut')
$p.Connect(1000)
$w = New-Object System.IO.StreamWriter($p); $w.AutoFlush = $true
$w.WriteLine('{"cmd":"signal","pattern":"done"}')
(New-Object System.IO.StreamReader($p)).ReadLine()
$p.Dispose()
```

Python:

```python
with open(r'\\.\pipe\KB525CControl', 'r+b', buffering=0) as p:
    p.write(b'{"cmd":"signal","pattern":"done"}\n')
    print(p.readline())
```

Node.js:

```js
const net = require('net');
const s = net.connect('\\\\.\\pipe\\KB525CControl', () => s.write('{"cmd":"signal","pattern":"bounce","sticky":true}\n'));
s.on('data', d => { console.log(d.toString()); s.end(); });
```

C# (any .NET app can reference `PipeClient.cs` + `PipeProtocol.cs` directly):

```csharp
var r = PipeClient.Send(new IpcRequest { Cmd = "signal", Pattern = "done", Label = "build" });
Console.WriteLine(r.Ok ? $"signal #{r.Id}" : r.Error);
```

## The tray app executable as a client

`KB525CControl.exe` with arguments does not start a second instance; it forwards a few simple
commands to the running one and exits: `KB525CControl.exe signal done [--sticky]`,
`KB525CControl.exe off`, `KB525CControl.exe reload`. The full interface is `kb525c`.
