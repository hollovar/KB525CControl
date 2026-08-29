using System.Text;

namespace KB525CControl;

/// <summary>One frame: which LEDs are lit and for how long. Ms = 0: hold until stopped.</summary>
public sealed record LedFrame(CollabLed Leds, int Ms);

/// <summary>
/// LED pattern language. A pattern is a sequence of frames with a repeat count:
///
///   FRAMES[@MS][xN]
///
/// FRAMES: frames separated by '/'. A frame is 4 characters, in order
/// Video, Share, Chat, Mic:
///   upper case = white (V S C M), lower case = red (only v and m exist),
///   '-' '.' '_' = dark. Alias words: all (VSCM), off/none (----), red (v--m).
/// @MS: length of one frame in milliseconds (default 150). @0 = hold the frame.
/// xN: how many times the sequence runs (default 1). x* / x∞ / xinf = forever (sticky).
///
/// Examples:
///   VSCM/----@150x5              the whole row blinks 5 times
///   V---/-S--/--C-/---M@80x3     running light
///   v--m/----@250x*              red alarm until acknowledged
///   VSCM@0                       all white, held
/// </summary>
public sealed class LedPattern
{
    public IReadOnlyList<LedFrame> Frames { get; }

    /// <summary>Number of repetitions; -1 = forever.</summary>
    public int Repeat { get; }

    /// <summary>The original (resolved) pattern text.</summary>
    public string Source { get; }

    public bool IsInfinite => Repeat < 0;

    public int DefaultFrameMs => 150;

    LedPattern(IReadOnlyList<LedFrame> frames, int repeat, string source)
    {
        Frames = frames;
        Repeat = repeat;
        Source = source;
    }

    /// <summary>Built-in named patterns. User presets (config) may override them.</summary>
    public static readonly IReadOnlyDictionary<string, string> Builtin =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["done"] = "VSCM/----@150x3",
            ["error"] = "v--m/----@200x5",
            ["attention"] = "VSCM/----@400x*",
            ["running"] = "V---/-S--/--C-/---M@120x*",
            ["chase"] = "V---/-S--/--C-/---M@80x3",
            ["pulse"] = "VSCM/----@500x4",
            ["red"] = "v--m@0",
            ["all"] = "VSCM@0",
        };

    /// <summary>
    /// Resolves a pattern name or pattern text. The name is looked up in the user presets
    /// first, then in the built-ins; if neither matches, the text itself is parsed.
    /// </summary>
    public static bool TryParse(string text, IReadOnlyDictionary<string, string>? presets,
        out LedPattern? pattern, out string? error)
    {
        pattern = null;
        error = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) { error = "empty pattern"; return false; }

        if (presets is not null)
        {
            // Case-insensitive lookup regardless of the dictionary's comparer (JSON deserialization resets it).
            var userDef = presets.FirstOrDefault(p => p.Key.Equals(text, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(userDef))
                return TryParseLiteral(userDef, out pattern, out error);
        }
        if (Builtin.TryGetValue(text, out var builtinDef))
            return TryParseLiteral(builtinDef, out pattern, out error);

        return TryParseLiteral(text, out pattern, out error);
    }

    static bool TryParseLiteral(string text, out LedPattern? pattern, out string? error)
    {
        pattern = null;
        error = null;

        var s = text.Replace(" ", "");
        var repeat = 1;
        var ms = 150;

        // trailing xN
        var xi = s.LastIndexOfAny(new[] { 'x', 'X' });
        if (xi > 0 && xi > s.LastIndexOf('@'))
        {
            var rep = s[(xi + 1)..];
            s = s[..xi];
            if (rep is "*" or "∞" || rep.Equals("inf", StringComparison.OrdinalIgnoreCase)
                || rep.Equals("forever", StringComparison.OrdinalIgnoreCase))
                repeat = -1;
            else if (!int.TryParse(rep, out repeat) || repeat < 1)
            {
                error = $"invalid repeat count: '{rep}'";
                return false;
            }
        }

        // @MS
        var ai = s.LastIndexOf('@');
        if (ai >= 0)
        {
            var msText = s[(ai + 1)..];
            s = s[..ai];
            if (!int.TryParse(msText, out ms) || ms < 0)
            {
                error = $"invalid frame length: '{msText}'";
                return false;
            }
        }

        var tokens = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { error = "no frames"; return false; }

        var frames = new List<LedFrame>();
        foreach (var tok in tokens)
        {
            if (!TryParseFrame(tok, out var leds))
            {
                error = $"invalid frame: '{tok}' (4 characters: V S C M, lower case = red, '-' = dark)";
                return false;
            }
            frames.Add(new LedFrame(leds, ms));
        }

        if (ms == 0)
        {
            // Held state: only the first frame matters, forever.
            frames = new List<LedFrame> { frames[0] };
            repeat = -1;
        }

        pattern = new LedPattern(frames, repeat, text);
        return true;
    }

    static bool TryParseFrame(string tok, out CollabLed leds)
    {
        leds = CollabLed.None;

        switch (tok.ToLowerInvariant())
        {
            case "all": leds = CollabLed.AllWhite; return true;
            case "off": case "none": leds = CollabLed.None; return true;
            case "red": leds = CollabLed.VideoRed | CollabLed.MicRed; return true;
        }

        // Raw LED byte for experiments, e.g. "0x40". Bits 0x40/0x80 have no effect on the KB525C.
        if (tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(tok[2..], System.Globalization.NumberStyles.HexNumber, null, out var raw))
        {
            leds = (CollabLed)raw;
            return true;
        }

        if (tok.Length != 4) return false;

        // position: 0 Video, 1 Share, 2 Chat, 3 Mic
        for (var i = 0; i < 4; i++)
        {
            var c = tok[i];
            if (c is '-' or '.' or '_') continue;

            switch (i)
            {
                case 0:
                    if (c == 'V') leds |= CollabLed.VideoWhite;
                    else if (c == 'v') leds |= CollabLed.VideoRed;
                    else return false;
                    break;
                case 1:
                    if (c is 'S' or 's') leds |= CollabLed.ShareWhite; else return false;
                    break;
                case 2:
                    if (c is 'C' or 'c') leds |= CollabLed.ChatWhite; else return false;
                    break;
                case 3:
                    if (c == 'M') leds |= CollabLed.MicWhite;
                    else if (c == 'm') leds |= CollabLed.MicRed;
                    else return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>The same pattern with a different repeat count (-1 = forever, i.e. sticky).</summary>
    public LedPattern WithRepeat(int repeat)
    {
        var frames = Frames.Select(f => f.Ms <= 0 && repeat >= 0 ? f with { Ms = DefaultFrameMs } : f).ToList();
        return new LedPattern(frames, repeat, Source + (repeat < 0 ? " (sticky)" : $" x{repeat}"));
    }

    /// <summary>Back to text (for the log).</summary>
    public override string ToString() => Source;

    public static string Describe(CollabLed leds)
    {
        var sb = new StringBuilder(4);
        sb.Append(leds.HasFlag(CollabLed.VideoRed) ? 'v' : leds.HasFlag(CollabLed.VideoWhite) ? 'V' : '-');
        sb.Append(leds.HasFlag(CollabLed.ShareWhite) ? 'S' : '-');
        sb.Append(leds.HasFlag(CollabLed.ChatWhite) ? 'C' : '-');
        sb.Append(leds.HasFlag(CollabLed.MicRed) ? 'm' : leds.HasFlag(CollabLed.MicWhite) ? 'M' : '-');
        var unknown = (byte)leds & 0xC0;
        if (unknown != 0) sb.Append($"+0x{unknown:X2}");
        return sb.ToString();
    }
}
