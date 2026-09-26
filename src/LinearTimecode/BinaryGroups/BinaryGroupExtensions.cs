namespace LinearTimecode.BinaryGroups;

/// <summary>Reads and writes ST 309 and ST 262 payloads on <see cref="LtcFrame"/>s.</summary>
public static class BinaryGroupExtensions
{
    /// <summary>True for BGF 100 and 110 (date and time zone, ST 12-1 Table 1).</summary>
    public static bool CarriesDateTimeZone(this BinaryGroupFlags flags) => flags is BinaryGroupFlags.DateTimeZone or BinaryGroupFlags.ClockTimeDateTimeZone;

    /// <summary>True for BGF 101 and 111 (page/line, ST 12-1 Table 1).</summary>
    public static bool CarriesPageLine(this BinaryGroupFlags flags) => flags is BinaryGroupFlags.PageLine or BinaryGroupFlags.ClockTimePageLine;

    /// <summary>The frame's ST 309 date and time zone, or null when the flags don't say so or the digits are invalid.</summary>
    public static DateTimeZone? GetDateTimeZone(this LtcFrame frame) =>
        frame.BinaryGroupFlags.CarriesDateTimeZone() && DateTimeZone.TryFromUserBits(frame.UserBits, out var v) ? v : null;

    /// <summary>
    /// Returns a copy carrying <paramref name="value"/> with BGF 100, or 110 when <paramref name="clockTime"/> (the time
    /// address is referenced to a precision clock, ST 309 Table 3).
    /// </summary>
    public static LtcFrame WithDateTimeZone(this LtcFrame frame, DateTimeZone value, bool clockTime = false) => frame with
    {
        UserBits = value.ToUserBits(),
        BinaryGroupFlags = clockTime ? BinaryGroupFlags.ClockTimeDateTimeZone : BinaryGroupFlags.DateTimeZone,
    };

    /// <summary>The frame's ST 262 page/line content, or null when the flags don't say so.</summary>
    public static PageLineFrame? GetPageLine(this LtcFrame frame) =>
        frame.BinaryGroupFlags.CarriesPageLine() ? PageLineFrame.FromUserBits(frame.UserBits) : null;

    /// <summary>Returns a copy carrying <paramref name="value"/> with BGF 101, or 111 when <paramref name="clockTime"/>.</summary>
    public static LtcFrame WithPageLine(this LtcFrame frame, PageLineFrame value, bool clockTime = false) => frame with
    {
        UserBits = value.ToUserBits(),
        BinaryGroupFlags = clockTime ? BinaryGroupFlags.ClockTimePageLine : BinaryGroupFlags.PageLine,
    };

    /// <summary>
    /// ST 309 §5.4: moves the date carried by <paramref name="frame"/> by <paramref name="days"/>. Frames without a
    /// valid ST 309 date are returned unchanged.
    /// </summary>
    public static LtcFrame RollDate(this LtcFrame frame, int days)
    {
        if (frame.GetDateTimeZone() is not { } dtz) return frame;
        try
        {
            return frame with { UserBits = dtz.AddDays(days).ToUserBits() };
        }
        catch (ArgumentOutOfRangeException)
        {
            return frame;
        }
    }
}
