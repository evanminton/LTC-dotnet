namespace LinearTimecode;

/// <summary>
/// The three binary group flags BGF2 BGF1 BGF0 (ST 12-1 §8.3.3, §8.4, Table 1). They say what the binary groups
/// ("user bits") contain and whether the time address is referenced to a time-of-day clock.
/// </summary>
/// <remarks>Numeric value = BGF2×4 + BGF1×2 + BGF0.</remarks>
public enum BinaryGroupFlags : byte
{
    /// <summary>000 — time address reference unspecified; binary groups unspecified (§8.4.1).</summary>
    Unspecified = 0b000,
    /// <summary>001 — 8-bit character set (ISO/IEC 646 or 2022), four characters (§8.4.2).</summary>
    EightBitCharacters = 0b001,
    /// <summary>010 — time address is clock time; binary groups unspecified (§8.4.3).</summary>
    ClockTime = 0b010,
    /// <summary>011 — reserved; shall not be used (§8.4.4).</summary>
    Reserved = 0b011,
    /// <summary>100 — date and time zone per SMPTE ST 309; time address reference unspecified (§8.4.5).</summary>
    DateTimeZone = 0b100,
    /// <summary>101 — page/line multiplex system per SMPTE ST 262 (§8.4.6).</summary>
    PageLine = 0b101,
    /// <summary>110 — clock time, with date and time zone per SMPTE ST 309 (§8.4.7).</summary>
    ClockTimeDateTimeZone = 0b110,
    /// <summary>111 — clock time, with page/line multiplex per SMPTE ST 262 (§8.4.8).</summary>
    ClockTimePageLine = 0b111,
}

/// <summary>Properties of <see cref="BinaryGroupFlags"/>.</summary>
public static class BinaryGroupFlagsExtensions
{
    public static IReadOnlyList<BinaryGroupFlags> All { get; } = Enum.GetValues<BinaryGroupFlags>();

    public static bool Bgf0(this BinaryGroupFlags f) => ((int)f & 1) != 0;
    public static bool Bgf1(this BinaryGroupFlags f) => ((int)f & 2) != 0;
    public static bool Bgf2(this BinaryGroupFlags f) => ((int)f & 4) != 0;

    /// <summary>Builds the value from the three flag bits.</summary>
    public static BinaryGroupFlags FromBits(bool bgf2, bool bgf1, bool bgf0) => (BinaryGroupFlags)((bgf2 ? 4 : 0) | (bgf1 ? 2 : 0) | (bgf0 ? 1 : 0));

    /// <summary>True when the time address is referenced to an external time-of-day clock (§8.5).</summary>
    public static bool IsClockTime(this BinaryGroupFlags f) => f is BinaryGroupFlags.ClockTime or BinaryGroupFlags.ClockTimeDateTimeZone or BinaryGroupFlags.ClockTimePageLine;

    /// <summary>True for the reserved combination 011.</summary>
    public static bool IsReserved(this BinaryGroupFlags f) => f == BinaryGroupFlags.Reserved;

    /// <summary>"BGF2 BGF1 BGF0" as three digits, e.g. "110".</summary>
    public static string BitPattern(this BinaryGroupFlags f) => $"{(f.Bgf2() ? 1 : 0)}{(f.Bgf1() ? 1 : 0)}{(f.Bgf0() ? 1 : 0)}";

    /// <summary>Time address reference column of Table 1.</summary>
    public static string TimeReference(this BinaryGroupFlags f) => f.IsReserved() ? "Reserved" : f.IsClockTime() ? "Clock time" : "Unspecified";

    /// <summary>Binary group column of Table 1.</summary>
    public static string BinaryGroupContent(this BinaryGroupFlags f) => f switch
    {
        BinaryGroupFlags.EightBitCharacters => "8-bit codes",
        BinaryGroupFlags.Reserved => "Reserved",
        BinaryGroupFlags.DateTimeZone or BinaryGroupFlags.ClockTimeDateTimeZone => "Date and time zone",
        BinaryGroupFlags.PageLine or BinaryGroupFlags.ClockTimePageLine => "Page/line",
        _ => "Unspecified",
    };

    /// <summary>Short display name.</summary>
    public static string DisplayName(this BinaryGroupFlags f) => $"{f.TimeReference()} / {f.BinaryGroupContent()}";

    /// <summary>Spec section.</summary>
    public static string Section(this BinaryGroupFlags f) => f switch
    {
        BinaryGroupFlags.Unspecified => "§8.4.1",
        BinaryGroupFlags.EightBitCharacters => "§8.4.2",
        BinaryGroupFlags.ClockTime => "§8.4.3, §8.5",
        BinaryGroupFlags.Reserved => "§8.4.4",
        BinaryGroupFlags.DateTimeZone => "§8.4.5",
        BinaryGroupFlags.PageLine => "§8.4.6",
        BinaryGroupFlags.ClockTimeDateTimeZone => "§8.4.7, §8.5",
        _ => "§8.4.8, §8.5",
    };

    /// <summary>Longer description.</summary>
    public static string Description(this BinaryGroupFlags f) => f switch
    {
        BinaryGroupFlags.Unspecified => "Time address reference undefined; the 32 binary-group bits may be assigned in any manner.",
        BinaryGroupFlags.EightBitCharacters => "Four ISO/IEC 646 or 2022 characters: 1st in groups 7/8, then 5/6, 3/4, 1/2 (low nibble in the lower group). 7-bit codes have bit 8 = 0.",
        BinaryGroupFlags.ClockTime => "Time address referenced to an external time-of-day clock; binary groups unrestricted.",
        BinaryGroupFlags.Reserved => "Reserved for future definition by SMPTE and shall not be used.",
        BinaryGroupFlags.DateTimeZone => "Binary groups carry date and time zone per SMPTE ST 309; time address reference undefined.",
        BinaryGroupFlags.PageLine => "Binary groups carry the SMPTE ST 262 page/line multiplex (control codes, text, production data).",
        BinaryGroupFlags.ClockTimeDateTimeZone => "Time address is clock time; binary groups carry date and time zone per SMPTE ST 309.",
        _ => "Time address is clock time; binary groups carry the SMPTE ST 262 page/line multiplex.",
    };
}
