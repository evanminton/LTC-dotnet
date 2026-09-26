namespace LinearTimecode;

/// <summary>
/// Color frame identification (ST 12-1 §5.3 NTSC, §6.3 PAL). When the color frame flag is set, the time address bears
/// a fixed relationship to the composite color sequence.
/// </summary>
public static class ColorFraming
{
    /// <summary>
    /// NTSC (§5.3): even frame units identify color fields I and II (color frame A); odd frame units identify fields III and IV (color frame B).
    /// </summary>
    public static NtscColorFrame Ntsc(Timecode timecode) => (timecode.Frames % 10) % 2 == 0 ? NtscColorFrame.FieldsIandII : NtscColorFrame.FieldsIIIandIV;

    /// <summary>
    /// PAL arithmetic relationship (§6.3.2): (S + P) mod 4, where S is the seconds and P the frames value.
    /// 1 → fields 1–2, 2 → fields 3–4, 3 → fields 5–6, 0 → fields 7–8.
    /// </summary>
    public static PalFieldPair Pal(Timecode timecode) => ((timecode.Seconds + timecode.Frames) % 4) switch
    {
        1 => PalFieldPair.Fields1And2,
        2 => PalFieldPair.Fields3And4,
        3 => PalFieldPair.Fields5And6,
        _ => PalFieldPair.Fields7And8,
    };

    /// <summary>
    /// PAL logical relationship (§6.3.1): (A|B) ^ C ^ D ^ E ^ F, which is 1 for fields 1–4 and 0 for fields 5–8.
    /// A = frame 1's bit, B = second 1's bit, C = frame 2's bit, D = frame 10's bit, E = second 2's bit, F = second 10's bit.
    /// </summary>
    public static bool PalLogical(Timecode timecode)
    {
        int fu = timecode.Frames % 10, ft = timecode.Frames / 10;
        int su = timecode.Seconds % 10, st = timecode.Seconds / 10;
        bool a = (fu & 1) != 0, b = (su & 1) != 0, c = (fu & 2) != 0, d = (ft & 1) != 0, e = (su & 2) != 0, f = (st & 1) != 0;
        return (a | b) ^ c ^ d ^ e ^ f;
    }

    /// <summary>
    /// True when a PAL address is consistent with the given color field (1–8), per §6.3.2.
    /// </summary>
    public static bool IsConsistent(Timecode timecode, int palField) => palField is >= 1 and <= 8 && (int)Pal(timecode) == (palField + 1) / 2;

    /// <summary>Human-readable description of the color frame an address identifies at its rate.</summary>
    public static string Describe(Timecode timecode) => timecode.Rate.Base() switch
    {
        TimecodeBase.Base30 => Ntsc(timecode) == NtscColorFrame.FieldsIandII ? "NTSC color fields I & II (even frame units)" : "NTSC color fields III & IV (odd frame units)",
        TimecodeBase.Base25 => $"PAL color fields {Pal(timecode) switch { PalFieldPair.Fields1And2 => "1 & 2", PalFieldPair.Fields3And4 => "3 & 4", PalFieldPair.Fields5And6 => "5 & 6", _ => "7 & 8" }}",
        _ => "No color framing in 24-frame systems",
    };
}

/// <summary>NTSC four-field color sequence (§5.3).</summary>
public enum NtscColorFrame
{
    /// <summary>Even frame units: color fields I and II.</summary>
    FieldsIandII,
    /// <summary>Odd frame units: color fields III and IV.</summary>
    FieldsIIIandIV,
}

/// <summary>PAL eight-field color sequence field pair (§6.3). Numeric value = 1–4.</summary>
public enum PalFieldPair
{
    Fields1And2 = 1,
    Fields3And4 = 2,
    Fields5And6 = 3,
    Fields7And8 = 4,
}
