using System.Globalization;

namespace LinearTimecode;

/// <summary>
/// An SMPTE ST 12-1 time address — hours, minutes, seconds and frames — at a given <see cref="LtcFrameRate"/>.
/// </summary>
/// <remarks>
/// <para>
/// For frame-pair rates (47.95, 48, 50, 59.94, 60) <see cref="Frames"/> counts frame pairs, i.e. one address per
/// LTC codeword, exactly as carried in the codeword (§12.1).
/// </para>
/// <para>
/// Arithmetic (<see cref="AddFrames"/>, <see cref="TotalFrames"/>, <see cref="FromTotalFrames"/>) works on
/// time-address counts, is exact for drop-frame and wraps at 24 hours.
/// </para>
/// </remarks>
public readonly struct Timecode : IEquatable<Timecode>, IComparable<Timecode>
{
    private const int DropFramesPerTenMinutes = 17_982; // 10 × 1800 − 9 × 2
    private const int DropFramesPerMinute = 1_798;      // 1800 − 2

    /// <summary>Creates a validated time address.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A field is out of range, or the address is skipped by drop-frame counting.</exception>
    public Timecode(int hours, int minutes, int seconds, int frames, LtcFrameRate rate = LtcFrameRate.Fps30)
    {
        Hours = hours; Minutes = minutes; Seconds = seconds; Frames = frames; Rate = rate;
        string? error = Validate();
        if (error is not null) throw new ArgumentOutOfRangeException(null, error);
    }

    private Timecode(int hours, int minutes, int seconds, int frames, LtcFrameRate rate, bool _)
    {
        Hours = hours; Minutes = minutes; Seconds = seconds; Frames = frames; Rate = rate;
    }

    /// <summary>Creates a time address without validating it — used for decoded codewords, which may carry anything.</summary>
    public static Timecode CreateUnchecked(int hours, int minutes, int seconds, int frames, LtcFrameRate rate) => new(hours, minutes, seconds, frames, rate, false);

    public int Hours { get; }
    public int Minutes { get; }
    public int Seconds { get; }
    /// <summary>Frame number (frame-pair number above 30 fps).</summary>
    public int Frames { get; }
    public LtcFrameRate Rate { get; }

    /// <summary>00:00:00:00 at the given rate.</summary>
    public static Timecode Zero(LtcFrameRate rate) => new(0, 0, 0, 0, rate, false);

    /// <summary>True when every field is in range and the address is not skipped by drop-frame counting.</summary>
    public bool IsValid => Validate() is null;

    /// <summary>Returns why this address is invalid, or null.</summary>
    public string? Validate()
    {
        if (!Enum.IsDefined(Rate)) return $"Unknown frame rate {(int)Rate}.";
        if (Hours is < 0 or > 23) return $"Hours {Hours} out of range 0–23.";
        if (Minutes is < 0 or > 59) return $"Minutes {Minutes} out of range 0–59.";
        if (Seconds is < 0 or > 59) return $"Seconds {Seconds} out of range 0–59.";
        int fps = Rate.FramesPerSecond();
        if (Frames < 0 || Frames >= fps) return $"Frames {Frames} out of range 0–{fps - 1} at {Rate.DisplayName()}.";
        if (IsDropFrameSkipped(Minutes, Seconds, Frames) && Rate.IsDropFrame())
            return $"{Minutes:00}:{Seconds:00};{Frames:00} does not exist in drop-frame counting (frames 00 and 01 are omitted at the start of each minute except 00, 10, 20, 30, 40 and 50).";
        return null;
    }

    private static bool IsDropFrameSkipped(int minutes, int seconds, int frames) => seconds == 0 && frames < 2 && minutes % 10 != 0;

    /// <summary>Number of addresses elapsed since 00:00:00:00 (drop-frame aware).</summary>
    public int TotalFrames
    {
        get
        {
            int fps = Rate.FramesPerSecond();
            int totalMinutes = Hours * 60 + Minutes;
            int n = ((totalMinutes * 60) + Seconds) * fps + Frames;
            if (Rate.IsDropFrame()) n -= 2 * (totalMinutes - totalMinutes / 10);
            return n;
        }
    }

    /// <summary>Builds the address that is <paramref name="totalFrames"/> addresses after midnight (wraps at 24 hours; negative counts wrap backwards).</summary>
    public static Timecode FromTotalFrames(long totalFrames, LtcFrameRate rate)
    {
        int perDay = rate.AddressesPerDay();
        long n = totalFrames % perDay;
        if (n < 0) n += perDay;
        int fps = rate.FramesPerSecond();

        if (rate.IsDropFrame())
        {
            long tens = n / DropFramesPerTenMinutes;
            long rem = n % DropFramesPerTenMinutes;
            n += 18 * tens;
            if (rem >= 2) n += 2 * ((rem - 2) / DropFramesPerMinute);
        }

        int frames = (int)(n % fps); n /= fps;
        int seconds = (int)(n % 60); n /= 60;
        int minutes = (int)(n % 60); n /= 60;
        return new Timecode((int)n, minutes, seconds, frames, rate, false);
    }

    /// <summary>Adds (or subtracts) a number of addresses, wrapping at 24 hours.</summary>
    public Timecode AddFrames(long frames) => FromTotalFrames(TotalFrames + frames, Rate);

    /// <summary>The next address in the count.</summary>
    public Timecode Next() => AddFrames(1);

    /// <summary>The previous address in the count.</summary>
    public Timecode Previous() => AddFrames(-1);

    /// <summary>Real time elapsed since 00:00:00:00, using the exact codeword rate (e.g. 30/1.001).</summary>
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks((long)Math.Round((double)TotalFrames * Rate.RateDenominator() * TimeSpan.TicksPerSecond / Rate.CodewordRateNumerator()));

    /// <summary>The address reached after <paramref name="elapsed"/> real time from 00:00:00:00 (rounded to the nearest address).</summary>
    public static Timecode FromTimeSpan(TimeSpan elapsed, LtcFrameRate rate) =>
        FromTotalFrames((long)Math.Round(elapsed.Ticks * (double)rate.CodewordRateNumerator() / rate.RateDenominator() / TimeSpan.TicksPerSecond), rate);

    /// <summary>
    /// The address whose <em>label</em> matches a wall-clock time of day (§8.5 clock time), e.g. for a generator slaved
    /// to a time-of-day clock. For drop-frame this is the nearest existing address; for non-drop NTSC rates the label
    /// is used as-is (it will drift from the clock by 3.6 s/hour while running).
    /// </summary>
    public static Timecode FromTimeOfDay(TimeSpan timeOfDay, LtcFrameRate rate)
    {
        long ticks = timeOfDay.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        var t = TimeSpan.FromTicks(ticks);
        int fps = rate.FramesPerSecond();
        int frames = (int)(t.Ticks % TimeSpan.TicksPerSecond * fps / TimeSpan.TicksPerSecond);
        if (rate.IsDropFrame() && IsDropFrameSkipped(t.Minutes, t.Seconds, frames)) frames = 2;
        return new Timecode(t.Hours, t.Minutes, t.Seconds, frames, rate, false);
    }

    /// <summary>
    /// The first video frame's index for this address. Equal to <see cref="TotalFrames"/> except at frame-pair rates, where
    /// each address labels two video frames (§12.1, Figure 9).
    /// </summary>
    public long VideoFrameIndex => (long)TotalFrames * Rate.VideoFramesPerCodeword();

    /// <summary>Same address and flags at a different rate label (no conversion; fields must be valid for the new rate).</summary>
    public Timecode WithRate(LtcFrameRate rate) => new(Hours, Minutes, Seconds, Frames, rate);

    /// <summary>Converts through real elapsed time to another rate (nearest address).</summary>
    public Timecode ConvertTo(LtcFrameRate rate) => FromTimeSpan(ToTimeSpan(), rate);

    /// <summary>Formats as HH:MM:SS:FF, or HH:MM:SS;FF for drop-frame.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Hours:00}:{Minutes:00}:{Seconds:00}{(Rate.IsDropFrame() ? ';' : ':')}{Frames:00}");

    /// <summary>
    /// Parses "HH:MM:SS:FF". A ';', ',' or '.' before the frames (e.g. "01:00:00;00") selects drop-frame when the rate has a
    /// drop-frame variant; the other separators must be ':' or the same character. Hours, minutes and seconds may be
    /// omitted from the left ("10:00" = 00:00:10:00).
    /// </summary>
    public static Timecode Parse(string text, LtcFrameRate rate = LtcFrameRate.Fps30) =>
        TryParse(text, rate, out var tc, out string? error) ? tc : throw new FormatException(error);

    /// <summary>Tries to parse a time address.</summary>
    public static bool TryParse(string? text, LtcFrameRate rate, out Timecode timecode) => TryParse(text, rate, out timecode, out _);

    /// <summary>Tries to parse a time address, returning the reason on failure.</summary>
    public static bool TryParse(string? text, LtcFrameRate rate, out Timecode timecode, out string? error)
    {
        timecode = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "Empty time code."; return false; }
        string s = text.Trim();

        // The separator before the frames selects drop-frame; the others must be ':' or the same character.
        char last = '\0';
        foreach (char c in s)
            if (c is ':' or ';' or ',' or '.')
            {
                if (last is not ('\0' or ':') && c != last) { error = $"'{text}' mixes separators; only the one before the frames may be ';', ',' or '.'."; return false; }
                last = c;
            }
        if (last is ';' or ',' or '.') rate = rate.WithDropFrame(true);

        string[] parts = s.Split([':', ';', ',', '.'], StringSplitOptions.None);
        if (parts.Length is < 1 or > 4) { error = $"'{text}' is not HH:MM:SS:FF."; return false; }

        var values = new int[4];
        int offset = 4 - parts.Length;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[offset + i]))
            {
                error = $"'{parts[i]}' in '{text}' is not a number.";
                return false;
            }
        }

        var tc = new Timecode(values[0], values[1], values[2], values[3], rate, false);
        error = tc.Validate();
        if (error is not null) return false;
        timecode = tc;
        return true;
    }

    public bool Equals(Timecode other) => Hours == other.Hours && Minutes == other.Minutes && Seconds == other.Seconds && Frames == other.Frames && Rate == other.Rate;
    public override bool Equals(object? obj) => obj is Timecode t && Equals(t);
    public override int GetHashCode() => HashCode.Combine(Hours, Minutes, Seconds, Frames, Rate);

    /// <summary>Orders by address (rates are not converted).</summary>
    public int CompareTo(Timecode other) => TotalFrames.CompareTo(other.TotalFrames);

    public static bool operator ==(Timecode a, Timecode b) => a.Equals(b);
    public static bool operator !=(Timecode a, Timecode b) => !a.Equals(b);
    public static bool operator <(Timecode a, Timecode b) => a.CompareTo(b) < 0;
    public static bool operator >(Timecode a, Timecode b) => a.CompareTo(b) > 0;
    public static bool operator <=(Timecode a, Timecode b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Timecode a, Timecode b) => a.CompareTo(b) >= 0;
    public static Timecode operator +(Timecode a, long frames) => a.AddFrames(frames);
    public static Timecode operator -(Timecode a, long frames) => a.AddFrames(-frames);
}
