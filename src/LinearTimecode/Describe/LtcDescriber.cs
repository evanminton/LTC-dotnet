using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using LinearTimecode.BinaryGroups;

namespace LinearTimecode.Describe;

/// <summary>Explains codewords, frames and time addresses in plain language.</summary>
public static class LtcDescriber
{
    /// <summary>
    /// Explains any of: a time code ("01:00:00;00"), 8/10 hex bytes, or a 64/80-digit bit string.
    /// Returns the reason instead when the input can't be read.
    /// </summary>
    public static string Explain(string input, LtcFrameRate rate = LtcFrameRate.Fps30) =>
        TryExplain(input, rate, out string? text, out string? error) ? text : error;

    /// <summary>
    /// Explains any of: a time code ("01:00:00;00"), 8/10 hex bytes, or a 64/80-digit bit string. Returns false with the
    /// reason when the input is none of these.
    /// </summary>
    public static bool TryExplain(string? input, LtcFrameRate rate, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? error)
    {
        text = error = null;
        if (string.IsNullOrWhiteSpace(input)) { error = "Enter a time code, 10 hex bytes or 80 bits."; return false; }
        string s = input.Trim();
        if (Timecode.TryParse(s, rate, out var tc, out string? tcError)) { text = Explain(new LtcFrame(tc)); return true; }
        try
        {
            text = Explain(LtcCodeword.Parse(s), rate);
            return true;
        }
        catch (FormatException ex)
        {
            error = s.Contains(':') || s.Contains(';') ? tcError ?? ex.Message : ex.Message;
            return false;
        }
    }

    /// <summary>Explains a frame and the codeword it encodes to.</summary>
    public static string Explain(LtcFrame frame) => Explain(frame.ToCodeword(), frame.Rate);

    /// <summary>Field-by-field explanation of a codeword read with the flag layout of <paramref name="rate"/>.</summary>
    public static string Explain(LtcCodeword cw, LtcFrameRate rate)
    {
        var frame = LtcFrame.FromCodeword(cw, rate);
        var b = rate.Base();
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        sb.AppendLine($"Time code      {frame.Timecode}  ({frame.Rate.DisplayName()} fps — {frame.Rate.Description()})");
        sb.AppendLine($"Bytes          {cw.ToHex()}");
        sb.AppendLine($"Bits 0→79      {cw.ToBitString()}");
        sb.AppendLine();
        sb.AppendLine("Time address (BCD, Table 2)");
        sb.AppendLine($"  Hours        {cw.HourTens}{cw.HourUnits}   bits 56–57 tens={cw.HourTens}, 48–51 units={cw.HourUnits}");
        sb.AppendLine($"  Minutes      {cw.MinuteTens}{cw.MinuteUnits}   bits 40–42 tens={cw.MinuteTens}, 32–35 units={cw.MinuteUnits}");
        sb.AppendLine($"  Seconds      {cw.SecondTens}{cw.SecondUnits}   bits 24–26 tens={cw.SecondTens}, 16–19 units={cw.SecondUnits}");
        sb.AppendLine($"  Frames       {cw.FrameTens}{cw.FrameUnits}   bits 8–9 tens={cw.FrameTens}, 0–3 units={cw.FrameUnits}{(rate.IsFramePair() ? "  (frame pair)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"Flags ({(int)b}-frame layout, Table 3)");
        if (LtcBits.DropFrameFlag(b) >= 0)
            sb.AppendLine($"  Drop frame   bit {LtcBits.DropFrameFlag(b),2} = {Bit(cw.DropFrameFlag(b))}  {(cw.DropFrameFlag(b) ? "drop-frame counting" : "non-drop counting")}");
        else
            sb.AppendLine($"  (bit 10)     unassigned = {Bit(cw[10])}{(cw[10] ? "  ← should be 0" : "")}");
        if (LtcBits.ColorFrameFlag(b) >= 0)
            sb.AppendLine($"  Color frame  bit 11 = {Bit(cw.ColorFrameFlag(b))}  {(cw.ColorFrameFlag(b) ? $"color framed: {ColorFraming.Describe(frame.Timecode)}" : "no color framing")}");
        else
            sb.AppendLine($"  (bit 11)     unassigned = {Bit(cw[11])}{(cw[11] ? "  ← should be 0" : "")}");
        int pc = LtcBits.PolarityCorrection(b);
        sb.AppendLine($"  Polarity     bit {pc,2} = {Bit(cw[pc])}  codeword has {(cw.HasEvenZeroCount ? "an even" : "an odd")} number of zeros ({cw.ZeroCount + 3}){(cw.HasEvenZeroCount ? " — sync polarity stable" : "")}");
        var bgf = cw.GetBinaryGroupFlags(b);
        sb.AppendLine($"  BGF2/1/0     bits {LtcBits.Bgf2(b)}/{LtcBits.Bgf1(b)}/{LtcBits.Bgf0(b)} = {bgf.BitPattern()}  {bgf.DisplayName()}: {bgf.Description()}");
        sb.AppendLine();
        sb.AppendLine("Binary groups (user bits, Table 4)");
        sb.Append(UserBitsDescriber.Explain(cw.UserBits, bgf, rate));
        if (frame.GetDateTimeZone() is { } dtz && frame.Timecode.IsValid && dtz.ToDateTimeOffset(frame.Timecode) is { } instant)
            sb.AppendLine(inv, $"  Instant      {instant:yyyy-MM-dd HH:mm:ss.fff zzz}  (UTC {instant.UtcDateTime:yyyy-MM-dd HH:mm:ss.fff})");
        sb.AppendLine();
        sb.AppendLine($"Sync word      bits 64–79 = 0011111111111101");
        sb.AppendLine();
        sb.AppendLine(inv, $"Timing         codeword {rate.CodewordDuration().TotalMilliseconds:0.###} ms, bit {rate.BitPeriodMicroseconds():0.#} µs, half-bit {rate.BitPeriodMicroseconds() / 2:0.#} µs");
        if (frame.Timecode.IsValid)
            sb.AppendLine(inv, $"Elapsed        {frame.Timecode.ToTimeSpan():hh\\:mm\\:ss\\.fff} real time since 00:00:00:00 (address #{frame.Timecode.TotalFrames})");

        var issues = frame.Validate();
        if (!cw.HasEvenZeroCount) issues = [.. issues, "Polarity correction not applied (odd number of zeros); allowed, but sync polarity will vary."];
        if (issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes");
            foreach (var i in issues) sb.AppendLine($"  • {i}");
        }
        return sb.ToString();
    }

    /// <summary>A table of all 80 bits with their meaning and value.</summary>
    public static string BitTable(LtcCodeword cw, TimecodeBase layout)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 80; i++) sb.AppendLine(CultureInfo.InvariantCulture, $"{i,2}  {Bit(cw[i])}  {LtcBits.NameOf(i, layout)}");
        return sb.ToString();
    }

    private static char Bit(bool b) => b ? '1' : '0';
}
