using System.Globalization;
using System.Text;

namespace LinearTimecode.BinaryGroups;

/// <summary>What a page/line directory index is assigned to (ST 262 §3.2, Figure 2).</summary>
public enum DirectoryCategory
{
    /// <summary>Pages 0–1 lines 0–9 and page 2 lines 0–3: auxiliary time address, hours 00–23 (SMPTE RP 169).</summary>
    AuxiliaryTimeAddress,
    /// <summary>Pages 0–1 lines 10–15 and page 2 lines 4–15: media address data other than time address.</summary>
    MediaAddress,
    /// <summary>Pages 3–14 (192 lines): user application dialects.</summary>
    Application,
    /// <summary>Page 15: control data — real-time commands, with checksum and highest encoding priority.</summary>
    Control,
}

/// <summary>
/// The SMPTE ST 262 directory index: binary group 8 = page (0–15), binary group 7 = line (0–15) (§3.1.3).
/// It is also binary byte 4.
/// </summary>
public readonly record struct DirectoryIndex : IComparable<DirectoryIndex>
{
    public DirectoryIndex(int page, int line)
    {
        if (page is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(page), "Pages are 0–15.");
        if (line is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(line), "Lines are 0–15.");
        Page = page;
        Line = line;
    }

    public int Page { get; }
    public int Line { get; }

    /// <summary>Binary byte 4: page in the high nibble (BG 8), line in the low nibble (BG 7).</summary>
    public byte Byte => (byte)((Page << 4) | Line);

    public static DirectoryIndex FromByte(byte b) => new(b >> 4, b & 0xF);

    /// <summary>Directory index carrying auxiliary time address hours 00–23 (page = tens, line = units).</summary>
    public static DirectoryIndex ForAuxiliaryHours(int hours) =>
        hours is >= 0 and <= 23 ? new(hours / 10, hours % 10) : throw new ArgumentOutOfRangeException(nameof(hours));

    public DirectoryCategory Category => (Page, Line) switch
    {
        (0 or 1, <= 9) or (2, <= 3) => DirectoryCategory.AuxiliaryTimeAddress,
        (0 or 1 or 2, _) => DirectoryCategory.MediaAddress,
        (15, _) => DirectoryCategory.Control,
        _ => DirectoryCategory.Application,
    };

    /// <summary>Auxiliary time address hours for this index, or null.</summary>
    public int? AuxiliaryHours => Category == DirectoryCategory.AuxiliaryTimeAddress ? Page * 10 + Line : null;

    /// <summary>Human-readable meaning.</summary>
    public string Description => Category switch
    {
        DirectoryCategory.AuxiliaryTimeAddress => $"Auxiliary time address, hour {AuxiliaryHours:00} (SMPTE RP 169)",
        DirectoryCategory.MediaAddress => "Media address data (other than time address)",
        DirectoryCategory.Control => "Control data: 2-byte real-time command, checksum in byte 1",
        _ => $"Application data (application page {Page - 3 + 1} of 12)",
    };

    /// <summary>ST 262 §3.4.5: higher page wins, then higher line. Positive when this has priority over <paramref name="other"/>.</summary>
    public int CompareTo(DirectoryIndex other) => Byte.CompareTo(other.Byte);

    /// <summary>Parses "15.3", "15/3", "p15 l3" or a hex byte "F3".</summary>
    public static DirectoryIndex Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim().ToLowerInvariant().Replace("page", "", StringComparison.Ordinal).Replace("line", "", StringComparison.Ordinal)
            .Replace("p", "", StringComparison.Ordinal).Replace("l", " ", StringComparison.Ordinal);
        string[] parts = s.Split(['.', '/', ',', ':', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int p) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int l))
            return new DirectoryIndex(p, l);
        string hex = s.Replace("0x", "", StringComparison.Ordinal);
        if (parts.Length == 1 && hex.Length is 1 or 2 && byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return FromByte(b);
        throw new FormatException($"'{text}' is not a directory index (page.line, e.g. 15.3, or a hex byte).");
    }

    public override string ToString() => $"{Page}.{Line}";
}

/// <summary>Which ST 262 message format a page/line frame uses (§3.4).</summary>
public enum PageLineFormat
{
    /// <summary>3-byte message in binary bytes 1–3 (§3.4.2). A dialect may put a checksum in byte 1.</summary>
    SingleFrame,
    /// <summary>Control code: checksum of bytes 2–4 in byte 1, 2-byte command in bytes 2–3 (§3.4.1).</summary>
    ControlCode,
    /// <summary>Prefix or suffix frame of a message string: checksum in byte 1, specifier in bytes 2–3 (§3.4.3).</summary>
    PrefixOrSuffix,
    /// <summary>Message frame of a message string: data in bytes 1–3 (§3.4.3).</summary>
    MessageData,
}

/// <summary>
/// One frame's binary groups in the SMPTE ST 262 page/line system: three data bytes plus the directory index.
/// Binary byte n = BG (2n−1) low nibble + BG 2n high nibble (§3.1.2); byte 4 is the directory index.
/// Use with <see cref="BinaryGroupFlags.PageLine"/> (101) or <see cref="BinaryGroupFlags.ClockTimePageLine"/> (111).
/// </summary>
public readonly record struct PageLineFrame(DirectoryIndex Index, byte Byte1, byte Byte2, byte Byte3)
{
    /// <summary>Binary byte 4 (the directory index).</summary>
    public byte Byte4 => Index.Byte;

    /// <summary>Binary byte 1–4.</summary>
    public byte this[int binaryByte] => binaryByte switch
    {
        1 => Byte1,
        2 => Byte2,
        3 => Byte3,
        4 => Byte4,
        _ => throw new ArgumentOutOfRangeException(nameof(binaryByte), "Binary bytes are numbered 1–4."),
    };

    public UserBits ToUserBits() => new((uint)(Byte1 | (Byte2 << 8) | (Byte3 << 16) | (Byte4 << 24)));

    public static PageLineFrame FromUserBits(UserBits bits) => new(
        DirectoryIndex.FromByte((byte)(bits.Value >> 24)),
        (byte)bits.Value, (byte)(bits.Value >> 8), (byte)(bits.Value >> 16));

    /// <summary>
    /// ST 262 §3.3.3 checksum: the two's complement of the least significant byte of the sum of <paramref name="bytes"/>.
    /// Adding it to those bytes gives 0 modulo 256.
    /// </summary>
    public static byte Checksum(params ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (byte b in bytes) sum += b;
        return (byte)(-sum & 0xFF);
    }

    /// <summary>True when byte 1 is the checksum of bytes 2, 3 and 4 (all four sum to 0 mod 256).</summary>
    public bool HasValidChecksum => (Byte1 + Byte2 + Byte3 + Byte4) % 256 == 0;

    /// <summary>Returns a copy with byte 1 replaced by the checksum of bytes 2–4.</summary>
    public PageLineFrame WithChecksum() => this with { Byte1 = Checksum(Byte2, Byte3, Byte4) };

    /// <summary>A control code frame on page 15 (§3.4.1).</summary>
    public static PageLineFrame ControlCode(int line, byte command1, byte command2) =>
        new PageLineFrame(new DirectoryIndex(15, line), 0, command1, command2).WithChecksum();

    /// <summary>A single-frame message (§3.4.2), optionally with a checksum in byte 1 (then only bytes 2–3 carry data).</summary>
    public static PageLineFrame SingleFrame(DirectoryIndex index, byte b1, byte b2, byte b3, bool checksum = false)
    {
        var f = new PageLineFrame(index, b1, b2, b3);
        return checksum ? f.WithChecksum() : f;
    }

    /// <summary>
    /// Auxiliary time address per SMPTE RP 169 (ST 262 §3.2.1): hours in the directory index (page = tens, line = units),
    /// frames/seconds/minutes in BG 1–6 with the drop frame and color frame flags in BG 2. See <see cref="BinaryGroups.AuxiliaryTimeAddress"/>.
    /// </summary>
    public static PageLineFrame ForAuxiliaryTimeAddress(Timecode timecode, bool colorFrame = false) =>
        new AuxiliaryTimeAddress(timecode, colorFrame).ToPageLineFrame();

    /// <summary>Reads an RP 169 auxiliary time address; null when the index is not one or the digits are invalid.</summary>
    public AuxiliaryTimeAddress? GetAuxiliaryTimeAddress(LtcFrameRate rate) =>
        BinaryGroups.AuxiliaryTimeAddress.FromUserBits(ToUserBits(), rate);

    /// <summary>Plain-language description of the frame.</summary>
    public string Describe(LtcFrameRate rate = LtcFrameRate.Fps30)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Directory {Index} (page {Index.Page}, line {Index.Line}): {Index.Description}. ");
        sb.Append(CultureInfo.InvariantCulture, $"Bytes 1–4 = {Byte1:X2} {Byte2:X2} {Byte3:X2} {Byte4:X2}");
        switch (Index.Category)
        {
            case DirectoryCategory.AuxiliaryTimeAddress:
                var aux = GetAuxiliaryTimeAddress(rate);
                sb.Append(aux is { } a ? $"; auxiliary time {a.Timecode}{(a.DropFrame ? " (DF flag)" : "")}{(a.ColorFrame ? " (CF flag)" : "")}" : "; not a valid RP 169 auxiliary time address");
                break;
            case DirectoryCategory.Control:
                sb.Append(CultureInfo.InvariantCulture, $"; command {Byte2:X2} {Byte3:X2}; checksum {(HasValidChecksum ? "OK" : $"BAD (expected {Checksum(Byte2, Byte3, Byte4):X2})")}");
                break;
            default:
                sb.Append($"; as text \"{Printable(Byte1)}{Printable(Byte2)}{Printable(Byte3)}\"");
                if (HasValidChecksum) sb.Append("; byte 1 is a valid checksum of bytes 2–4");
                break;
        }
        return sb.ToString();
    }

    private static char Printable(byte b) => b is >= 0x20 and < 0x7F ? (char)b : '.';

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"[{Index}] {Byte1:X2} {Byte2:X2} {Byte3:X2}");
}

/// <summary>
/// ST 262 message strings (§3.4.3): prefix frame(s), message frame(s) and suffix frame(s), each identified by its own
/// directory index (assigned by the application dialect). At most 256 frames.
/// </summary>
public sealed record PageLineMessageLayout(DirectoryIndex Prefix, DirectoryIndex Message, DirectoryIndex Suffix)
{
    public const int MaxFrames = 256;

    /// <summary>Default layout used by this library's tools: application page 3, lines 0 (prefix), 1 (message), 2 (suffix).</summary>
    public static PageLineMessageLayout Default { get; } = new(new(3, 0), new(3, 1), new(3, 2));

    /// <summary>
    /// Encodes <paramref name="data"/> as a message string. The prefix and suffix carry a checksum and a 2-byte specifier
    /// (by default: message ID and the number of message frames). Message frames carry 3 data bytes each, null-filled.
    /// </summary>
    public IReadOnlyList<PageLineFrame> Encode(ReadOnlySpan<byte> data, byte messageId = 0, (byte, byte)? prefixSpecifier = null, (byte, byte)? suffixSpecifier = null)
    {
        int messageFrames = (data.Length + 2) / 3;
        if (messageFrames + 2 > MaxFrames) throw new ArgumentException($"A message string is limited to {MaxFrames} frames ({(MaxFrames - 2) * 3} bytes).", nameof(data));
        byte count = (byte)messageFrames;
        var (p1, p2) = prefixSpecifier ?? (messageId, count);
        var (s1, s2) = suffixSpecifier ?? (messageId, count);

        var frames = new List<PageLineFrame>(messageFrames + 2)
        {
            new PageLineFrame(Prefix, 0, p1, p2).WithChecksum(),
        };
        Span<byte> chunk = stackalloc byte[3];
        for (int i = 0; i < messageFrames; i++)
        {
            chunk.Clear();
            data.Slice(3 * i, Math.Min(3, data.Length - 3 * i)).CopyTo(chunk);
            frames.Add(new PageLineFrame(Message, chunk[0], chunk[1], chunk[2]));
        }
        frames.Add(new PageLineFrame(Suffix, 0, s1, s2).WithChecksum());
        return frames;
    }

    /// <summary>Encodes text (ISO/IEC 646, 8-bit) as a message string.</summary>
    public IReadOnlyList<PageLineFrame> EncodeText(string text, byte messageId = 0) =>
        Encode(Encoding.Latin1.GetBytes(text), messageId);

    /// <summary>
    /// Reassembles complete messages from a sequence of frames (e.g. the user bits of consecutive codewords).
    /// Frames with other directory indexes are skipped; a prefix restarts collection; a suffix completes a message.
    /// </summary>
    public IReadOnlyList<PageLineMessage> Decode(IEnumerable<PageLineFrame> frames)
    {
        var result = new List<PageLineMessage>();
        List<byte>? data = null;
        PageLineFrame prefix = default;
        foreach (var f in frames)
        {
            if (f.Index == Prefix) { data = []; prefix = f; }
            else if (f.Index == Message && data is not null) { data.Add(f.Byte1); data.Add(f.Byte2); data.Add(f.Byte3); }
            else if (f.Index == Suffix && data is not null)
            {
                int end = data.Count;
                while (end > 0 && data[end - 1] == 0) end--; // strip null fill
                result.Add(new PageLineMessage(prefix, f, [.. data.Take(end)]));
                data = null;
            }
        }
        return result;
    }
}

/// <summary>A reassembled ST 262 message string.</summary>
public sealed record PageLineMessage(PageLineFrame PrefixFrame, PageLineFrame SuffixFrame, byte[] Data)
{
    /// <summary>Message ID from the default specifier (prefix byte 2).</summary>
    public byte MessageId => PrefixFrame.Byte2;

    public bool ChecksumsValid => PrefixFrame.HasValidChecksum && SuffixFrame.HasValidChecksum;

    public string Text => Encoding.Latin1.GetString(Data);

    public override string ToString() => $"Message {MessageId}: \"{Text}\"{(ChecksumsValid ? "" : " (checksum error)")}";
}
