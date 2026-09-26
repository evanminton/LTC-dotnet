using System.Globalization;

namespace LinearTimecode;

/// <summary>
/// The frame rates defined by SMPTE ST 12-1 §1 (60, 59.94, 50, 48, 47.95, 30, 29.97, 25, 24 and 23.98 fps),
/// with drop-frame and non-drop-frame variants for the NTSC-related 30-frame counts.
/// </summary>
/// <remarks>
/// Above 30 fps the time address counts <em>frame pairs</em> (§12): one 80-bit LTC codeword spans two video frames,
/// so a 50 fps signal carries 25 codewords per second whose frame field runs 00–24.
/// </remarks>
public enum LtcFrameRate
{
    /// <summary>24/1.001 fps (≈23.976), 24-frame count, NTSC time.</summary>
    Fps23_98,
    /// <summary>24 fps, 24-frame count.</summary>
    Fps24,
    /// <summary>25 fps, 25-frame count.</summary>
    Fps25,
    /// <summary>30/1.001 fps (≈29.97), 30-frame count, non-drop (uncompensated).</summary>
    Fps29_97,
    /// <summary>30/1.001 fps (≈29.97), 30-frame count, drop-frame (NTSC time compensated).</summary>
    Fps29_97Drop,
    /// <summary>30 fps, 30-frame count.</summary>
    Fps30,
    /// <summary>48/1.001 fps (≈47.95) progressive, frame pairs, 24-frame count.</summary>
    Fps47_95,
    /// <summary>48 fps progressive, frame pairs, 24-frame count.</summary>
    Fps48,
    /// <summary>50 fps progressive, frame pairs, 25-frame count.</summary>
    Fps50,
    /// <summary>60/1.001 fps (≈59.94) progressive, frame pairs, 30-frame count, non-drop.</summary>
    Fps59_94,
    /// <summary>60/1.001 fps (≈59.94) progressive, frame pairs, 30-frame count, drop-frame.</summary>
    Fps59_94Drop,
    /// <summary>60 fps progressive, frame pairs, 30-frame count.</summary>
    Fps60,
}

/// <summary>
/// Which of the three flag layouts in ST 12-1 Table 3 a codeword uses. The layout follows the frame count
/// (24, 25 or 30 addresses per second), not the video rate.
/// </summary>
public enum TimecodeBase
{
    /// <summary>24-frame count (23.98, 24, 47.95, 48 fps).</summary>
    Base24 = 24,
    /// <summary>25-frame count (25, 50 fps).</summary>
    Base25 = 25,
    /// <summary>30-frame count (29.97, 30, 59.94, 60 fps).</summary>
    Base30 = 30,
}

/// <summary>Properties of <see cref="LtcFrameRate"/>.</summary>
public static class LtcFrameRateExtensions
{
    /// <summary>All rates, slowest first.</summary>
    public static IReadOnlyList<LtcFrameRate> All { get; } = Enum.GetValues<LtcFrameRate>();

    /// <summary>Addresses per second in the frame field: 24, 25 or 30.</summary>
    public static TimecodeBase Base(this LtcFrameRate rate) => rate switch
    {
        LtcFrameRate.Fps23_98 or LtcFrameRate.Fps24 or LtcFrameRate.Fps47_95 or LtcFrameRate.Fps48 => TimecodeBase.Base24,
        LtcFrameRate.Fps25 or LtcFrameRate.Fps50 => TimecodeBase.Base25,
        _ => TimecodeBase.Base30,
    };

    /// <summary>Number of frame addresses per second (24, 25 or 30).</summary>
    public static int FramesPerSecond(this LtcFrameRate rate) => (int)rate.Base();

    /// <summary>True for the drop-frame (NTSC time compensated) counting mode of §5.2.2.</summary>
    public static bool IsDropFrame(this LtcFrameRate rate) => rate is LtcFrameRate.Fps29_97Drop or LtcFrameRate.Fps59_94Drop;

    /// <summary>True when the frame rate is an integer divided by 1.001 ("NTSC time", §5.1.2).</summary>
    public static bool IsNtscTime(this LtcFrameRate rate) => rate is LtcFrameRate.Fps23_98 or LtcFrameRate.Fps29_97 or LtcFrameRate.Fps29_97Drop
        or LtcFrameRate.Fps47_95 or LtcFrameRate.Fps59_94 or LtcFrameRate.Fps59_94Drop;

    /// <summary>True above 30 fps, where each time address labels a frame pair (§12).</summary>
    public static bool IsFramePair(this LtcFrameRate rate) => rate >= LtcFrameRate.Fps47_95;

    /// <summary>Video frames per LTC codeword: 1, or 2 for frame-pair rates.</summary>
    public static int VideoFramesPerCodeword(this LtcFrameRate rate) => rate.IsFramePair() ? 2 : 1;

    /// <summary>Video frame rate as an exact fraction (numerator).</summary>
    public static int VideoRateNumerator(this LtcFrameRate rate) => rate.FramesPerSecond() * rate.VideoFramesPerCodeword() * (rate.IsNtscTime() ? 1000 : 1);

    /// <summary>Video frame rate as an exact fraction (denominator: 1 or 1001).</summary>
    public static int RateDenominator(this LtcFrameRate rate) => rate.IsNtscTime() ? 1001 : 1;

    /// <summary>Codeword (time address) rate as an exact fraction, numerator. Denominator is <see cref="RateDenominator"/>.</summary>
    public static int CodewordRateNumerator(this LtcFrameRate rate) => rate.FramesPerSecond() * (rate.IsNtscTime() ? 1000 : 1);

    /// <summary>Video frames per second as a double, e.g. 29.97002997.</summary>
    public static double VideoFrameRate(this LtcFrameRate rate) => (double)rate.VideoRateNumerator() / rate.RateDenominator();

    /// <summary>LTC codewords per second (Ff in §9.4), e.g. 29.97002997 for both 29.97 and 59.94 fps.</summary>
    public static double CodewordRate(this LtcFrameRate rate) => (double)rate.CodewordRateNumerator() / rate.RateDenominator();

    /// <summary>Nominal bit rate Fe = 80 × Ff (§9.4), in bits per second.</summary>
    public static double BitRate(this LtcFrameRate rate) => 80.0 * rate.CodewordRate();

    /// <summary>Duration of one codeword.</summary>
    public static TimeSpan CodewordDuration(this LtcFrameRate rate) => TimeSpan.FromSeconds(1.0 / rate.CodewordRate());

    /// <summary>Duration of one bit cell (1/Fe).</summary>
    public static TimeSpan BitPeriod(this LtcFrameRate rate) => TimeSpan.FromSeconds(1.0 / rate.BitRate());

    /// <summary>Number of time addresses in 24 hours (drop-frame omits 2 × 54 per hour).</summary>
    public static int AddressesPerDay(this LtcFrameRate rate) => rate.IsDropFrame() ? 2_589_408 : 86_400 * rate.FramesPerSecond();

    /// <summary>Short display name, e.g. "29.97 DF".</summary>
    public static string DisplayName(this LtcFrameRate rate) => rate switch
    {
        LtcFrameRate.Fps23_98 => "23.98",
        LtcFrameRate.Fps24 => "24",
        LtcFrameRate.Fps25 => "25",
        LtcFrameRate.Fps29_97 => "29.97 NDF",
        LtcFrameRate.Fps29_97Drop => "29.97 DF",
        LtcFrameRate.Fps30 => "30",
        LtcFrameRate.Fps47_95 => "47.95",
        LtcFrameRate.Fps48 => "48",
        LtcFrameRate.Fps50 => "50",
        LtcFrameRate.Fps59_94 => "59.94 NDF",
        LtcFrameRate.Fps59_94Drop => "59.94 DF",
        LtcFrameRate.Fps60 => "60",
        _ => rate.ToString(),
    };

    /// <summary>Token used by <see cref="Parse"/> and the CLI, e.g. "29.97df".</summary>
    public static string Token(this LtcFrameRate rate) => rate.DisplayName().Replace(" NDF", "", StringComparison.Ordinal).Replace(" DF", "df", StringComparison.Ordinal);

    /// <summary>One-line description.</summary>
    public static string Description(this LtcFrameRate rate)
    {
        string frac = rate.IsNtscTime() ? $"{rate.VideoFramesPerCodeword() * rate.FramesPerSecond()}/1.001" : $"{rate.VideoFramesPerCodeword() * rate.FramesPerSecond()}";
        string count = rate.IsDropFrame() ? "drop-frame count (frames 00 and 01 omitted each minute except every tenth)" :
            rate.Base() == TimecodeBase.Base30 && rate.IsNtscTime() ? "non-drop count (runs ≈3.6 s/hour slow against real time)" :
            $"frames 00–{rate.FramesPerSecond() - 1}";
        string pair = rate.IsFramePair() ? "; one codeword per frame pair" : "";
        return FormattableString.Invariant($"{frac} fps, {count}{pair}; {rate.CodewordRate():0.###} codewords/s, {rate.BitRate():0.#} bit/s.");
    }

    /// <summary>Parses "23.976", "23.98", "24", "25", "29.97", "29.97df", "30", "47.95", "48", "50", "59.94", "59.94df", "60" (also "df"/"ndf" suffix variants and enum names).</summary>
    public static LtcFrameRate Parse(string text) =>
        TryParse(text, out var r) ? r : throw new FormatException($"Unknown frame rate '{text}'. Use one of: {string.Join(", ", All.Select(a => a.Token()))}.");

    /// <summary>Tries to parse a frame-rate token.</summary>
    public static bool TryParse(string? text, out LtcFrameRate rate)
    {
        rate = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("fps", "", StringComparison.Ordinal);
        if (char.IsLetter(text.Trim()[0]) && Enum.TryParse(text.Trim(), true, out rate) && Enum.IsDefined(rate)) return true;

        bool drop = false;
        if (s.EndsWith("ndf", StringComparison.Ordinal)) s = s[..^3];
        else if (s.EndsWith("df", StringComparison.Ordinal)) { drop = true; s = s[..^2]; }
        else if (s.EndsWith("d", StringComparison.Ordinal)) { drop = true; s = s[..^1]; }
        s = s.TrimEnd('-', '_');

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return false;

        LtcFrameRate? r = v switch
        {
            > 23.9 and < 23.99 => LtcFrameRate.Fps23_98,
            24 => LtcFrameRate.Fps24,
            25 => LtcFrameRate.Fps25,
            > 29.9 and < 29.99 => drop ? LtcFrameRate.Fps29_97Drop : LtcFrameRate.Fps29_97,
            30 => LtcFrameRate.Fps30,
            > 47.9 and < 47.99 => LtcFrameRate.Fps47_95,
            48 => LtcFrameRate.Fps48,
            50 => LtcFrameRate.Fps50,
            > 59.9 and < 59.99 => drop ? LtcFrameRate.Fps59_94Drop : LtcFrameRate.Fps59_94,
            60 => LtcFrameRate.Fps60,
            _ => null,
        };
        if (r is null) return false;
        if (drop && !r.Value.IsDropFrame()) return false;
        rate = r.Value;
        return true;
    }

    /// <summary>The rate with the same timing and the given counting mode (only 29.97 and 59.94 have both).</summary>
    public static LtcFrameRate WithDropFrame(this LtcFrameRate rate, bool dropFrame) => (rate, dropFrame) switch
    {
        (LtcFrameRate.Fps29_97, true) => LtcFrameRate.Fps29_97Drop,
        (LtcFrameRate.Fps29_97Drop, false) => LtcFrameRate.Fps29_97,
        (LtcFrameRate.Fps59_94, true) => LtcFrameRate.Fps59_94Drop,
        (LtcFrameRate.Fps59_94Drop, false) => LtcFrameRate.Fps59_94,
        _ => rate,
    };
}
