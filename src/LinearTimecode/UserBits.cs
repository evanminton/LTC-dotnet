using System.Globalization;
using System.Text;

namespace LinearTimecode;

/// <summary>
/// The eight 4-bit binary groups ("user bits", ST 12-1 §8.4, §9.2.4) — 32 bits of user data per codeword.
/// </summary>
/// <remarks>
/// <see cref="Value"/> holds binary group 1 in bits 0–3, group 2 in bits 4–7, … group 8 in bits 28–31, so
/// <c>Value.ToString("X8")</c> reads group 8 first — the conventional display order, and the order in which
/// §8.4.2 stores characters (first character in groups 7/8).
/// </remarks>
public readonly record struct UserBits(uint Value)
{
    public static UserBits Empty => default;

    /// <summary>Binary group 1–8 (0–15).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is not 1–8.</exception>
    public int this[int group] => Group(group);

    /// <summary>Gets binary group 1–8.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is not 1–8.</exception>
    public int Group(int group)
    {
        if (group is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(group), "Binary groups are numbered 1–8.");
        return (int)((Value >> ((group - 1) * 4)) & 0xF);
    }

    /// <summary>Returns a copy with binary group 1–8 set.</summary>
    public UserBits WithGroup(int group, int value)
    {
        if (group is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(group), "Binary groups are numbered 1–8.");
        if (value is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(value), "A binary group holds 0–15.");
        int shift = (group - 1) * 4;
        return new UserBits((Value & ~(0xFu << shift)) | ((uint)value << shift));
    }

    /// <summary>Builds from groups 1..8 in that order.</summary>
    public static UserBits FromGroups(ReadOnlySpan<int> groups)
    {
        if (groups.Length != 8) throw new ArgumentException("Exactly eight binary groups are required.", nameof(groups));
        var ub = default(UserBits);
        for (int i = 0; i < 8; i++) ub = ub.WithGroup(i + 1, groups[i]);
        return ub;
    }

    /// <summary>Groups 1..8 in that order.</summary>
    public int[] ToGroups()
    {
        var g = new int[8];
        for (int i = 0; i < 8; i++) g[i] = this[i + 1];
        return g;
    }

    /// <summary>
    /// Four 8-bit characters as defined in §8.4.2 (use with <see cref="BinaryGroupFlags.EightBitCharacters"/>).
    /// Shorter text is padded with spaces; characters above 0xFF are rejected.
    /// </summary>
    public static UserBits FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4) throw new ArgumentException("At most four characters fit in the binary groups.", nameof(text));
        string s = text.PadRight(4);
        uint v = 0;
        for (int i = 0; i < 4; i++)
        {
            if (s[i] > 0xFF) throw new ArgumentException($"'{s[i]}' is not an 8-bit character.", nameof(text));
            v = (v << 8) | s[i];
        }
        return new UserBits(v);
    }

    /// <summary>The four §8.4.2 characters (first from groups 7/8). Non-printable bytes become '.'.</summary>
    public string ToText()
    {
        var sb = new StringBuilder(4);
        for (int i = 3; i >= 0; i--)
        {
            char c = (char)((Value >> (i * 8)) & 0xFF);
            sb.Append(c is (>= ' ' and <= '~') or >= '\u00A0' ? c : '.'); // printable ASCII and Latin-1
        }
        return sb.ToString();
    }

    /// <summary>Parses eight hex digits in display order (group 8 first). Spaces, ':' and '-' are ignored.</summary>
    public static UserBits Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        string s = string.Concat(hex.Where(c => !char.IsWhiteSpace(c) && c is not ':' and not '-' and not '.'));
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length is 0 or > 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            throw new FormatException($"'{hex}' is not 1–8 hex digits.");
        return new UserBits(v);
    }

    /// <summary>Eight hex digits, group 8 first, e.g. "12345678".</summary>
    public override string ToString() => Value.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Display as "12 34 56 78".</summary>
    public string ToDisplayString()
    {
        string h = ToString();
        return $"{h[..2]} {h[2..4]} {h[4..6]} {h[6..]}";
    }

    /// <summary>True when every group is a decimal digit 0–9 (a common use: a date or reel number as BCD).</summary>
    public bool IsBcd
    {
        get
        {
            for (int g = 1; g <= 8; g++) if (this[g] > 9) return false;
            return true;
        }
    }
}
