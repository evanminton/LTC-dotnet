using System.Globalization;
using System.Text;

namespace LinearTimecode;

/// <summary>
/// Bit positions of an 80-bit LTC codeword (ST 12-1 §9.2, Tables 2–5).
/// </summary>
public static class LtcBits
{
    public const int CodewordBits = 80;
    public const int DataBits = 64;

    // Table 2 – time address
    public const int FrameUnits = 0, FrameTens = 8;
    public const int SecondUnits = 16, SecondTens = 24;
    public const int MinuteUnits = 32, MinuteTens = 40;
    public const int HourUnits = 48, HourTens = 56;

    /// <summary>First bit of binary group 1–8 (Table 4): 4, 12, 20, … 60.</summary>
    public static int BinaryGroup(int group) => (group - 1) * 8 + 4;

    /// <summary>Sync word bits 64–79 (Table 5) as a 16-bit value, bit 64 in the LSB: 0 0 1 1 1 1 1 1 1 1 1 1 1 1 0 1.</summary>
    public const ushort SyncWord = 0xBFFC;

    /// <summary>The sync word as it is received when the code runs backwards (bit 79 first).</summary>
    public const ushort ReverseSyncWord = 0x3FFD;

    /// <summary>Drop-frame flag bit, or -1 when the layout leaves it unassigned (Table 3).</summary>
    public static int DropFrameFlag(TimecodeBase b) => b == TimecodeBase.Base30 ? 10 : -1;

    /// <summary>Color-frame flag bit, or -1 when unassigned (24-frame).</summary>
    public static int ColorFrameFlag(TimecodeBase b) => b == TimecodeBase.Base24 ? -1 : 11;

    /// <summary>Biphase mark polarity correction bit: 27, or 59 in 25-frame systems.</summary>
    public static int PolarityCorrection(TimecodeBase b) => b == TimecodeBase.Base25 ? 59 : 27;

    /// <summary>BGF0: 43, or 27 in 25-frame systems.</summary>
    public static int Bgf0(TimecodeBase b) => b == TimecodeBase.Base25 ? 27 : 43;

    /// <summary>BGF1: 58 in every system.</summary>
    public static int Bgf1(TimecodeBase b) => 58;

    /// <summary>BGF2: 59, or 43 in 25-frame systems.</summary>
    public static int Bgf2(TimecodeBase b) => b == TimecodeBase.Base25 ? 43 : 59;

    /// <summary>Human-readable name of bit 0–79 in the given layout.</summary>
    public static string NameOf(int bit, TimecodeBase b)
    {
        if (bit is < 0 or > 79) throw new ArgumentOutOfRangeException(nameof(bit));
        if (bit >= 64) return $"Sync word ({((SyncWord >> (bit - 64)) & 1)})";
        if (bit == DropFrameFlag(b)) return "Drop frame flag";
        if (bit == ColorFrameFlag(b)) return "Color frame flag";
        if (bit == PolarityCorrection(b)) return "Biphase mark polarity correction";
        if (bit == Bgf0(b)) return "Binary group flag BGF0";
        if (bit == Bgf1(b)) return "Binary group flag BGF1";
        if (bit == Bgf2(b)) return "Binary group flag BGF2";
        int slot = bit % 8;
        if (slot >= 4) return $"Binary group {bit / 8 + 1} (weight {1 << (slot - 4)})";
        (string field, int[] weights) = (bit / 8) switch
        {
            0 => ("Frame units", new[] { 1, 2, 4, 8 }),
            1 => ("Frame tens", new[] { 10, 20, 0, 0 }),
            2 => ("Second units", new[] { 1, 2, 4, 8 }),
            3 => ("Second tens", new[] { 10, 20, 40, 0 }),
            4 => ("Minute units", new[] { 1, 2, 4, 8 }),
            5 => ("Minute tens", new[] { 10, 20, 40, 0 }),
            6 => ("Hour units", new[] { 1, 2, 4, 8 }),
            _ => ("Hour tens", new[] { 10, 20, 0, 0 }),
        };
        int w = weights[slot];
        return w == 0 ? "Unassigned (0)" : $"{field} ({w})";
    }
}

/// <summary>
/// A raw 80-bit LTC codeword: 64 data bits (time address, flags, binary groups) plus the fixed sync word.
/// </summary>
/// <remarks>
/// <para>
/// Bit <c>n</c> of the codeword (0–63) is bit <c>n</c> of <see cref="Data"/>; bits 64–79 are always <see cref="LtcBits.SyncWord"/>.
/// </para>
/// <para>
/// Flag positions differ between 24/30- and 25-frame systems (Table 3), so the flag accessors take a <see cref="TimecodeBase"/>.
/// Use <see cref="LtcFrame"/> for a friendlier model.
/// </para>
/// </remarks>
public readonly record struct LtcCodeword(ulong Data)
{
    /// <summary>Bit 0–79. Bits 64–79 come from the sync word.</summary>
    public bool this[int bit] => GetBit(bit);

    /// <summary>Bit 0–79. Bits 64–79 come from the sync word.</summary>
    public bool GetBit(int bit) => bit switch
    {
        >= 0 and < 64 => ((Data >> bit) & 1) != 0,
        >= 64 and < 80 => ((LtcBits.SyncWord >> (bit - 64)) & 1) != 0,
        _ => throw new ArgumentOutOfRangeException(nameof(bit), "Codeword bits are numbered 0–79."),
    };

    /// <summary>Copy with data bit 0–63 set or cleared.</summary>
    public LtcCodeword WithBit(int bit, bool value)
    {
        if (bit is < 0 or > 63) throw new ArgumentOutOfRangeException(nameof(bit), "Only data bits 0–63 can be changed.");
        ulong mask = 1UL << bit;
        return new LtcCodeword(value ? Data | mask : Data & ~mask);
    }

    /// <summary>Reads <paramref name="count"/> bits starting at <paramref name="start"/>, LSB first.</summary>
    public int GetBits(int start, int count) => (int)((Data >> start) & ((1UL << count) - 1));

    /// <summary>Copy with <paramref name="count"/> bits at <paramref name="start"/> replaced.</summary>
    public LtcCodeword WithBits(int start, int count, int value)
    {
        ulong mask = ((1UL << count) - 1) << start;
        return new LtcCodeword((Data & ~mask) | (((ulong)value << start) & mask));
    }

    // ---- time address (Table 2) ----
    public int FrameUnits => GetBits(LtcBits.FrameUnits, 4);
    public int FrameTens => GetBits(LtcBits.FrameTens, 2);
    public int SecondUnits => GetBits(LtcBits.SecondUnits, 4);
    public int SecondTens => GetBits(LtcBits.SecondTens, 3);
    public int MinuteUnits => GetBits(LtcBits.MinuteUnits, 4);
    public int MinuteTens => GetBits(LtcBits.MinuteTens, 3);
    public int HourUnits => GetBits(LtcBits.HourUnits, 4);
    public int HourTens => GetBits(LtcBits.HourTens, 2);

    public int Frames => FrameTens * 10 + FrameUnits;
    public int Seconds => SecondTens * 10 + SecondUnits;
    public int Minutes => MinuteTens * 10 + MinuteUnits;
    public int Hours => HourTens * 10 + HourUnits;

    /// <summary>True when every BCD units digit is 0–9.</summary>
    public bool HasValidBcd => FrameUnits <= 9 && SecondUnits <= 9 && MinuteUnits <= 9 && HourUnits <= 9;

    /// <summary>The binary groups (user bits).</summary>
    public UserBits UserBits
    {
        get
        {
            uint v = 0;
            for (int g = 1; g <= 8; g++) v |= (uint)GetBits(LtcBits.BinaryGroup(g), 4) << ((g - 1) * 4);
            return new UserBits(v);
        }
    }

    // ---- flags (Table 3) ----
    public bool DropFrameFlag(TimecodeBase b) => LtcBits.DropFrameFlag(b) is var i and >= 0 && GetBit(i);
    public bool ColorFrameFlag(TimecodeBase b) => LtcBits.ColorFrameFlag(b) is var i and >= 0 && GetBit(i);
    public bool PolarityCorrectionBit(TimecodeBase b) => GetBit(LtcBits.PolarityCorrection(b));
    public BinaryGroupFlags GetBinaryGroupFlags(TimecodeBase b) =>
        BinaryGroupFlagsExtensions.FromBits(GetBit(LtcBits.Bgf2(b)), GetBit(LtcBits.Bgf1(b)), GetBit(LtcBits.Bgf0(b)));

    /// <summary>Number of logical zeros in bits 0–63.</summary>
    public int ZeroCount => 64 - System.Numerics.BitOperations.PopCount(Data);

    /// <summary>
    /// True when the whole 80-bit codeword contains an even number of zeros, i.e. the sync word always starts with the
    /// same polarity (§9.2.3). The sync word has three zeros, so bits 0–63 must hold an odd number.
    /// </summary>
    public bool HasEvenZeroCount => (ZeroCount + 3) % 2 == 0;

    /// <summary>Returns a copy whose polarity-correction bit is set so the codeword has an even number of zeros (§9.2.3).</summary>
    public LtcCodeword WithPolarityCorrection(TimecodeBase b)
    {
        int pc = LtcBits.PolarityCorrection(b);
        var cleared = WithBit(pc, false);
        // Count zeros in bits 0–63 excluding the correction bit itself.
        int zeros = cleared.ZeroCount - 1;
        return cleared.WithBit(pc, zeros % 2 == 1);
    }

    /// <summary>The 80 bits, bit 0 first.</summary>
    public bool[] ToBits()
    {
        var bits = new bool[80];
        for (int i = 0; i < 80; i++) bits[i] = GetBit(i);
        return bits;
    }

    /// <summary>Builds a codeword from 80 bits (bit 0 first). The sync word must be present.</summary>
    public static LtcCodeword FromBits(ReadOnlySpan<bool> bits)
    {
        if (bits.Length is not 64 and not 80) throw new ArgumentException("Expected 64 or 80 bits.", nameof(bits));
        ulong d = 0;
        for (int i = 0; i < 64; i++) if (bits[i]) d |= 1UL << i;
        if (bits.Length == 80)
        {
            for (int i = 64; i < 80; i++)
                if (bits[i] != (((LtcBits.SyncWord >> (i - 64)) & 1) != 0))
                    throw new FormatException("Bits 64–79 are not the LTC sync word 0011111111111101.");
        }
        return new LtcCodeword(d);
    }

    /// <summary>
    /// The codeword as 10 bytes, each byte holding eight consecutive bits LSB first (byte 0 = bits 0–7 … byte 9 = bits 72–79).
    /// This is the layout used by most LTC software, with the sync word ending in <c>FC BF</c>.
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new byte[10];
        for (int i = 0; i < 8; i++) b[i] = (byte)(Data >> (i * 8));
        b[8] = (byte)(LtcBits.SyncWord & 0xFF);
        b[9] = (byte)(LtcBits.SyncWord >> 8);
        return b;
    }

    /// <summary>Reads 8 bytes (data only) or 10 bytes (with sync word, which is checked).</summary>
    public static LtcCodeword FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not 8 and not 10) throw new ArgumentException("Expected 8 or 10 bytes.", nameof(bytes));
        ulong d = 0;
        for (int i = 0; i < 8; i++) d |= (ulong)bytes[i] << (i * 8);
        if (bytes.Length == 10 && (bytes[8] | (bytes[9] << 8)) != LtcBits.SyncWord)
            throw new FormatException($"Bytes 8–9 ({bytes[8]:X2} {bytes[9]:X2}) are not the LTC sync word FC BF.");
        return new LtcCodeword(d);
    }

    /// <summary>Hex bytes, e.g. "16 00 52 00 37 00 01 00 FC BF".</summary>
    public string ToHex() => string.Join(' ', ToBytes().Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Parses 8 or 10 hex bytes (any separators), or a string of 64/80 '0'/'1' characters (bit 0 first).</summary>
    public static LtcCodeword Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ',' and not '-' and not ':' and not '_'));
        if (compact.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) compact = compact[2..];
        if (compact.Length is 64 or 80 && compact.All(c => c is '0' or '1'))
            return FromBits([.. compact.Select(c => c == '1')]);
        if (compact.Length is 16 or 20 && compact.All(Uri.IsHexDigit))
            return FromBytes(Convert.FromHexString(compact));
        throw new FormatException("Expected 8 or 10 hex bytes, or 64/80 binary digits (bit 0 first).");
    }

    /// <summary>The 80 bits as '0'/'1', bit 0 first, grouped in fours.</summary>
    public string ToBitString(bool grouped = true)
    {
        var sb = new StringBuilder(100);
        for (int i = 0; i < 80; i++)
        {
            if (grouped && i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(GetBit(i) ? '1' : '0');
        }
        return sb.ToString();
    }

    public override string ToString() => ToHex();
}
