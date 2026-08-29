using System.Runtime.InteropServices;

namespace KB525CControl;

/// <summary>
/// Sends a key combination to the focused window via SendInput.
///
/// Used for actions that have no better route (e.g. Google Meet: Ctrl+D mutes,
/// Ctrl+E toggles the camera). Limitation: only the focused window receives it,
/// so the meeting window has to be in front.
/// </summary>
public static class Hotkeys
{
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // The largest member of the union is MOUSEINPUT (32 bytes on x64); its size matters.
        [FieldOffset(0)] public MOUSEPAD pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEPAD
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B, ["lwin"] = 0x5B,
        ["tab"] = 0x09, ["enter"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["backspace"] = 0x08, ["delete"] = 0x2E,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C
    };

    static ushort? ToVirtualKey(string token)
    {
        token = token.Trim();
        if (token.Length == 0) return null;
        if (Named.TryGetValue(token, out var vk)) return vk;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        if (token.Length is 2 or 3 && (token[0] is 'f' or 'F')
            && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
            return (ushort)(0x70 + fn - 1);

        return null;
    }

    /// <summary>True when every token of the combination is a known key.</summary>
    public static bool IsValid(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return keys.Length > 0 && keys.All(k => ToVirtualKey(k) is not null);
    }

    /// <summary>
    /// Sends a combination such as "ctrl+shift+m": holds the modifiers, presses the last
    /// key, then releases everything in reverse order.
    /// </summary>
    public static bool Send(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToVirtualKey)
            .ToList();

        if (keys.Count == 0 || keys.Any(k => k is null)) return false;

        var vks = keys.Select(k => k!.Value).ToList();
        var inputs = new List<INPUT>();

        foreach (var vk in vks) inputs.Add(Key(vk, false));
        for (var i = vks.Count - 1; i >= 0; i--) inputs.Add(Key(vks[i], true));

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        return sent == arr.Length;
    }

    const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>Types text into the focused window as Unicode input. '\n' sends Enter, '\t' sends Tab.</summary>
    public static bool TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            if (ch == '\t') { inputs.Add(Key(0x09, false)); inputs.Add(Key(0x09, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        return sent == arr.Length;
    }

    static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0u) }
        }
    };

    static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        // Win and the arrow keys are extended keys.
        if (vk is 0x5B or >= 0x25 and <= 0x28) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
        };
    }
}
