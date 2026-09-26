using System.Globalization;

namespace LinearTimecode.BinaryGroups;

/// <summary>How the date is written in binary groups 1–6 (ST 309 §5.1.1, MJD flag = BG 8 bit 3).</summary>
public enum DateFormat
{
    /// <summary>MJD flag 0: six BCD digits YYMMDD; the time address is <em>local</em> time (UTC + time zone offset).</summary>
    Yymmdd,
    /// <summary>MJD flag 1: six BCD digits of the Modified Julian Date; the time address is <em>UTC</em>.</summary>
    ModifiedJulianDate,
}

/// <summary>
/// Date and time zone carried in the binary groups, per SMPTE ST 309:2012 (use with
/// <see cref="BinaryGroupFlags.DateTimeZone"/> 100 or <see cref="BinaryGroupFlags.ClockTimeDateTimeZone"/> 110).
/// </summary>
/// <remarks>
/// <para>Layout (ST 309 Tables 1, 4, 5):</para>
/// <list type="bullet">
/// <item>BG 7 bits 0–3 and BG 8 bits 0–1: time zone code TZ-0…TZ-5 (Table 2, <see cref="TimeZoneCode"/>).</item>
/// <item>BG 8 bit 2: DST flag. BG 8 bit 3: MJD flag.</item>
/// <item>YYMMDD: BG 1 day units, BG 2 day tens, BG 3 month units, BG 4 month tens, BG 5 year units, BG 6 year tens.</item>
/// <item>MJD: BG 1 units … BG 6 hundred-thousands.</item>
/// </list>
/// <para>The date increments at the time-address midnight rollover (§5.4); <see cref="Audio.LtcGenerator"/> does this automatically.</para>
/// </remarks>
public sealed record DateTimeZone
{
    /// <summary>MJD 0 is 17 November 1858.</summary>
    public static readonly DateOnly MjdEpoch = new(1858, 11, 17);

    /// <summary>Two-digit years at or above this pivot are read as 19xx, below as 20xx. Default 70.</summary>
    public static int CenturyPivot { get; set; } = 70;

    /// <summary>Creates a value from its parts.</summary>
    public DateTimeZone(DateOnly date, TimeZoneCode timeZone, DateFormat format = DateFormat.Yymmdd, bool daylightSaving = false)
    {
        if (format == DateFormat.Yymmdd && (date.Year < 1900 + CenturyPivot || date.Year > 1999 + CenturyPivot))
            throw new ArgumentOutOfRangeException(nameof(date), $"YYMMDD covers {1900 + CenturyPivot}–{1999 + CenturyPivot} with the current century pivot; use the MJD format for other years.");
        if (format == DateFormat.ModifiedJulianDate && (date < MjdEpoch || ToMjd(date) > 999_999))
            throw new ArgumentOutOfRangeException(nameof(date), "Date is outside the 6-digit MJD range.");
        Date = date;
        TimeZone = timeZone;
        Format = format;
        DaylightSaving = daylightSaving;
    }

    /// <summary>The date. For YYMMDD it is the local date; for MJD it is the UTC date.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Time zone code (offset in effect).</summary>
    public TimeZoneCode TimeZone { get; init; }

    /// <summary>Date format; also decides whether the time address is local time or UTC.</summary>
    public DateFormat Format { get; init; }

    /// <summary>DST flag: daylight saving time is in effect (§5.1.2).</summary>
    public bool DaylightSaving { get; init; }

    /// <summary>The Modified Julian Date of <see cref="Date"/>.</summary>
    public int Mjd => ToMjd(Date);

    /// <summary>True when the time address carries UTC (MJD format); false when it carries local time.</summary>
    public bool TimeAddressIsUtc => Format == DateFormat.ModifiedJulianDate;

    public static int ToMjd(DateOnly date) => date.DayNumber - MjdEpoch.DayNumber;
    public static DateOnly FromMjd(int mjd) => DateOnly.FromDayNumber(MjdEpoch.DayNumber + mjd);

    /// <summary>
    /// Builds the ST 309 value for an instant: YYMMDD carries the local date (time address = local time),
    /// MJD carries the UTC date (time address = UTC).
    /// </summary>
    public static DateTimeZone ForInstant(DateTimeOffset instant, DateFormat format = DateFormat.Yymmdd, bool daylightSaving = false)
    {
        var tz = TimeZoneCode.FromOffset(instant.Offset) ?? throw new ArgumentException($"ST 309 has no time zone code for offset {instant.Offset}.", nameof(instant));
        var date = format == DateFormat.ModifiedJulianDate ? DateOnly.FromDateTime(instant.UtcDateTime) : DateOnly.FromDateTime(instant.DateTime);
        return new DateTimeZone(date, tz, format, daylightSaving);
    }

    /// <summary>Encodes into the 32 user bits.</summary>
    public UserBits ToUserBits()
    {
        int[] digits = Format == DateFormat.Yymmdd
            ? [Date.Day % 10, Date.Day / 10, Date.Month % 10, Date.Month / 10, Date.Year % 10, Date.Year / 10 % 10]
            : [.. Enumerable.Range(0, 6).Select(i => Mjd / (int)Math.Pow(10, i) % 10)];
        int tz = TimeZone.Code;
        int bg7 = tz & 0xF;
        int bg8 = (tz >> 4) | (DaylightSaving ? 4 : 0) | (Format == DateFormat.ModifiedJulianDate ? 8 : 0);
        return UserBits.FromGroups([.. digits, bg7, bg8]);
    }

    /// <summary>Decodes user bits; throws <see cref="FormatException"/> when the digits are not a valid date.</summary>
    public static DateTimeZone FromUserBits(UserBits bits) =>
        TryFromUserBits(bits, out var v, out string? error) ? v! : throw new FormatException(error);

    /// <summary>Tries to decode user bits as ST 309.</summary>
    public static bool TryFromUserBits(UserBits bits, out DateTimeZone? value) => TryFromUserBits(bits, out value, out _);

    /// <summary>Tries to decode user bits as ST 309, returning why not.</summary>
    public static bool TryFromUserBits(UserBits bits, out DateTimeZone? value, out string? error)
    {
        value = null;
        error = null;
        var tz = new TimeZoneCode(bits[7] | ((bits[8] & 3) << 4));
        bool dst = (bits[8] & 4) != 0;
        bool mjd = (bits[8] & 8) != 0;
        for (int g = 1; g <= 6; g++)
        {
            if (bits[g] > 9) { error = $"Binary group {g} = {bits[g]:X} is not a BCD digit."; return false; }
        }

        if (mjd)
        {
            int n = 0;
            for (int g = 6; g >= 1; g--) n = n * 10 + bits[g];
            value = new DateTimeZone(FromMjd(n), tz, DateFormat.ModifiedJulianDate, dst);
            return true;
        }

        int day = bits[2] * 10 + bits[1], month = bits[4] * 10 + bits[3], yy = bits[6] * 10 + bits[5];
        int year = yy >= CenturyPivot ? 1900 + yy : 2000 + yy;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            error = $"{yy:00}-{month:00}-{day:00} (YYMMDD) is not a valid date.";
            return false;
        }
        value = new DateTimeZone(new DateOnly(year, month, day), tz, DateFormat.Yymmdd, dst);
        return true;
    }

    /// <summary>
    /// Combines this date with a time address into an instant. YYMMDD: the address is local time at the zone offset;
    /// MJD: the address is UTC and the result is expressed at the zone offset. Frames become fractions of a second
    /// using the address label (frames ÷ frames-per-second). Returns null when the zone is not an offset.
    /// </summary>
    public DateTimeOffset? ToDateTimeOffset(Timecode timecode)
    {
        if (TimeZone.Offset is not { } offset) return null;
        var time = new TimeSpan(0, timecode.Hours, timecode.Minutes, timecode.Seconds) +
                   TimeSpan.FromTicks(TimeSpan.TicksPerSecond * timecode.Frames / timecode.Rate.FramesPerSecond());
        var dt = Date.ToDateTime(TimeOnly.MinValue) + time;
        return TimeAddressIsUtc
            ? new DateTimeOffset(dt, TimeSpan.Zero).ToOffset(offset)
            : new DateTimeOffset(dt, offset);
    }

    /// <summary>The same value one day later/earlier (the §5.4 midnight rollover).</summary>
    public DateTimeZone AddDays(int days) => this with { Date = Date.AddDays(days) };

    /// <summary>Problems with this value against ST 309.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        switch (TimeZone.Kind)
        {
            case TimeZoneCodeKind.Reserved: issues.Add($"Time zone code {TimeZone.Hex} is reserved and shall not be used."); break;
            case TimeZoneCodeKind.Deprecated: issues.Add($"Time zone code {TimeZone.Hex} (time precision) is deprecated."); break;
        }
        return issues;
    }

    /// <summary>E.g. "2026-09-26 (YYMMDD, local) UTC-07:00 DST".</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Date:yyyy-MM-dd} ({(Format == DateFormat.Yymmdd ? "YYMMDD, time is local" : $"MJD {Mjd}, time is UTC")}) {TimeZone.DisplayName}{(DaylightSaving ? " DST" : "")}");
}
