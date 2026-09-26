using System.Globalization;
using System.Text;
using LinearTimecode.BinaryGroups;

namespace LinearTimecode.Describe;

/// <summary>Explains the binary groups according to the binary group flags (ST 12-1 §8.4, ST 309, ST 262).</summary>
public static class UserBitsDescriber
{
    /// <summary>One-line meaning, e.g. "Date 2026-09-26, UTC-07:00 DST" or "Page/line 15.3 control 01 02".</summary>
    public static string Summary(UserBits bits, BinaryGroupFlags flags, LtcFrameRate rate = LtcFrameRate.Fps30)
    {
        if (flags.CarriesDateTimeZone())
            return DateTimeZone.TryFromUserBits(bits, out var d, out string? err) ? $"Date {d}" : $"ST 309: {err}";
        if (flags.CarriesPageLine())
        {
            var pl = PageLineFrame.FromUserBits(bits);
            return pl.Index.Category switch
            {
                DirectoryCategory.AuxiliaryTimeAddress => pl.GetAuxiliaryTimeAddress(rate) is { } aux ? $"Aux time {aux} (RP 169)" : $"Page/line {pl.Index} aux time (invalid)",
                DirectoryCategory.Control => string.Create(CultureInfo.InvariantCulture, $"Page/line {pl.Index} control {pl.Byte2:X2} {pl.Byte3:X2}{(pl.HasValidChecksum ? "" : " (checksum error)")}"),
                _ => string.Create(CultureInfo.InvariantCulture, $"Page/line {pl.Index} {pl.Index.Category} {pl.Byte1:X2} {pl.Byte2:X2} {pl.Byte3:X2}"),
            };
        }
        if (flags == BinaryGroupFlags.EightBitCharacters) return $"Text \"{bits.ToText()}\"";
        return $"Raw {bits.ToDisplayString()}";
    }

    /// <summary>Multi-line explanation of the binary groups in the mode the flags select.</summary>
    public static string Explain(UserBits bits, BinaryGroupFlags flags, LtcFrameRate rate = LtcFrameRate.Fps30)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"  Hex          {bits.ToDisplayString()}   (group 8 … group 1)");
        sb.Append("  Groups       ");
        for (int g = 1; g <= 8; g++) sb.Append(inv, $"{g}:{bits[g]:X} ");
        sb.AppendLine();
        sb.AppendLine($"  Mode         BGF {flags.BitPattern()} — {flags.BinaryGroupContent()} ({flags.Section()})");

        if (flags.CarriesDateTimeZone())
        {
            var tz = new TimeZoneCode(bits[7] | ((bits[8] & 3) << 4));
            bool dst = (bits[8] & 4) != 0, mjd = (bits[8] & 8) != 0;
            sb.AppendLine($"  Time zone    BG7 + BG8 bits 0–1 = {tz.Hex}: {tz.Description}");
            sb.AppendLine($"  DST flag     BG8 bit 2 = {(dst ? 1 : 0)}  {(dst ? "daylight saving time in effect" : "standard time")}");
            sb.AppendLine($"  MJD flag     BG8 bit 3 = {(mjd ? 1 : 0)}  {(mjd ? "date is a Modified Julian Date; time address is UTC" : "date is YYMMDD; time address is local time")}");
            if (DateTimeZone.TryFromUserBits(bits, out var d, out string? err))
            {
                sb.AppendLine(mjd
                    ? $"  Date         BG1–6 = MJD {d!.Mjd} = {d.Date:yyyy-MM-dd} (UTC)"
                    : $"  Date         BG6 BG5 / BG4 BG3 / BG2 BG1 = {bits[6]}{bits[5]}-{bits[4]}{bits[3]}-{bits[2]}{bits[1]} = {d!.Date:yyyy-MM-dd} (local)");
                foreach (var issue in d.Validate()) sb.AppendLine($"  Note         {issue}");
            }
            else sb.AppendLine($"  Date         {err}");
        }
        else if (flags.CarriesPageLine())
        {
            var pl = PageLineFrame.FromUserBits(bits);
            sb.AppendLine($"  Directory    BG8 page {pl.Index.Page}, BG7 line {pl.Index.Line} (byte 4 = {pl.Byte4:X2}): {pl.Index.Description}");
            sb.AppendLine(inv, $"  Bytes 1–3    {pl.Byte1:X2} {pl.Byte2:X2} {pl.Byte3:X2}   (byte n = BG 2n−1 low nibble + BG 2n high nibble)");
            switch (pl.Index.Category)
            {
                case DirectoryCategory.AuxiliaryTimeAddress:
                    if (AuxiliaryTimeAddress.TryFromUserBits(bits, rate, out var aux, out string? auxError))
                    {
                        sb.AppendLine($"  Aux time     {aux!.Timecode}  (SMPTE RP 169: BG1–2 frames, BG3–4 seconds, BG5–6 minutes, BG7–8 hours)");
                        sb.AppendLine($"  DF flag      BG2 bit 2 = {(aux.DropFrame ? 1 : 0)}  {(aux.DropFrame ? "auxiliary address uses drop-frame counting" : "non-drop")}");
                        sb.AppendLine($"  CF flag      BG2 bit 3 = {(aux.ColorFrame ? 1 : 0)}  {(aux.ColorFrame ? "color frame ID applied to the auxiliary address" : "no color framing")}");
                        foreach (var issue in aux.Validate()) sb.AppendLine($"  Note         {issue}");
                    }
                    else sb.AppendLine($"  Aux time     {auxError}");
                    break;
                case DirectoryCategory.Control:
                    sb.AppendLine(inv, $"  Command      {pl.Byte2:X2} {pl.Byte3:X2}");
                    sb.AppendLine(inv, $"  Checksum     byte 1 = {pl.Byte1:X2}, expected {PageLineFrame.Checksum(pl.Byte2, pl.Byte3, pl.Byte4):X2} — {(pl.HasValidChecksum ? "OK" : "ERROR")}");
                    break;
                default:
                    sb.AppendLine($"  As text      \"{Printable(pl.Byte1)}{Printable(pl.Byte2)}{Printable(pl.Byte3)}\"");
                    if (pl.HasValidChecksum) sb.AppendLine("  Checksum     byte 1 is a valid checksum of bytes 2–4 (prefix/suffix or checksummed message)");
                    break;
            }
        }
        else if (flags == BinaryGroupFlags.EightBitCharacters || bits.Value != 0)
        {
            sb.AppendLine($"  As text      \"{bits.ToText()}\"  (§8.4.2: 1st character in groups 7/8 … 4th in groups 1/2)");
            if (bits.IsBcd) sb.AppendLine($"  As digits    {bits}");
        }
        return sb.ToString();
    }

    private static char Printable(byte b) => b is >= 0x20 and < 0x7F ? (char)b : '.';
}
