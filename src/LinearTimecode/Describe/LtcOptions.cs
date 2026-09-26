using System.Text;
using LinearTimecode.Audio;

namespace LinearTimecode.Describe;

/// <summary>One option or value defined by ST 12-1 (or by this library), in human-readable form.</summary>
/// <param name="Code">The value as written in the spec or on the command line.</param>
/// <param name="Name">Short name.</param>
/// <param name="Description">What it means.</param>
public sealed record LtcOption(string Code, string Name, string Description)
{
    public override string ToString() => $"{Code,-14} {Name} — {Description}";
}

/// <summary>A titled list of related options.</summary>
public sealed record LtcOptionGroup(string Key, string Title, string Summary, IReadOnlyList<LtcOption> Options);

/// <summary>
/// Catalog of every option in SMPTE ST 12-1 LTC and every setting of the generator and decoder, for UI, help text and the CLI.
/// </summary>
public static class LtcOptions
{
    /// <summary>Frame rates (§1, §5–7, §12).</summary>
    public static LtcOptionGroup FrameRates { get; } = new("rates", "Frame Rates",
        "Rates defined by ST 12-1 §1. Above 30 fps one codeword labels a frame pair (§12). Only 30-frame counts have drop-frame.",
        [.. LtcFrameRateExtensions.All.Select(r => new LtcOption(r.Token(), r.DisplayName(), r.Description()))]);

    /// <summary>Counting modes.</summary>
    public static LtcOptionGroup CountingModes { get; } = new("counting", "Counting Modes (§5.2, §6.2, §7.2)",
        "How frame numbers advance within each second of the 24-hour clock 00:00:00:00–23:59:59:ff.",
        [
            new("NDF", "Non-drop frame", "Frames numbered 00 through 24/25/30 − 1 with no omissions. At 29.97 this runs ≈108 frames (3.6 s) per hour slow against real time."),
            new("DF", "Drop frame", "30-frame count only: frame numbers 00 and 01 are omitted at the start of every minute except 00, 10, 20, 30, 40 and 50. Error ≈ −3.6 ms/hour, −2.6 frames/day. Shown with ';' before the frames."),
            new("pairs", "Frame pairs", "48, 50 and 60 fps (and /1.001): the address increments every other frame; edit resolution is two frames (§12.1)."),
        ]);

    /// <summary>Flag bits (§8.3, Table 3).</summary>
    public static LtcOptionGroup Flags { get; } = new("flags", "Flag Bits (§8.3, Table 3)",
        "Six flag bits; positions depend on the frame count. Unused flag bits shall be 0 at the source and ignored by receivers.",
        [
            new("DF  10/–/–", "Drop frame flag", "1 when drop-frame counting is in use (§8.3.1). 30-frame only; bit 10 is unassigned at 24/25."),
            new("CF  11/11/–", "Color frame flag", "1 when color framing has been applied to the address (§8.3.2, §5.3, §6.3). Unassigned at 24 frames."),
            new("PC  27/59/27", "Biphase mark polarity correction", "Set so every codeword has an even number of zeros, making the sync word start with a stable polarity (§9.2.3)."),
            new("BGF0 43/27/43", "Binary group flag 0", "With BGF1 and BGF2, says what the binary groups contain (§8.3.3, Table 1)."),
            new("BGF1 58/58/58", "Binary group flag 1", "Same position in every system."),
            new("BGF2 59/43/59", "Binary group flag 2", "Swapped with the polarity bit in 25-frame systems."),
        ]);

    /// <summary>Binary group flag combinations (Table 1).</summary>
    public static LtcOptionGroup BinaryGroupFlagCombos { get; } = new("bgf", "Binary Group Flags (BGF2 BGF1 BGF0, Table 1)",
        "Time address reference and binary group usage.",
        [.. BinaryGroupFlagsExtensions.All.Select(f => new LtcOption($"{f.BitPattern()} ({(int)f})", $"{f.DisplayName()}", $"{f.Description()} {f.Section()}"))]);

    /// <summary>Codeword layout (Tables 2, 4, 5).</summary>
    public static LtcOptionGroup CodewordLayout { get; } = new("bits", "Codeword Layout (§9.1–9.2, Tables 2–5)",
        "80 bits sent serially from bit 0; the lowest numbered bit of each group is the LSB. Flags per Table 3 (see 'flags').",
        [
            new("0–3", "Units of frames", "BCD 0–9."),
            new("4–7", "Binary group 1", "User bits."),
            new("8–9", "Tens of frames", "BCD 0–2."),
            new("10, 11", "Flags", "Drop frame, color frame (30-frame layout)."),
            new("12–15", "Binary group 2", "User bits."),
            new("16–19", "Units of seconds", "BCD 0–9."),
            new("20–23", "Binary group 3", "User bits."),
            new("24–26", "Tens of seconds", "BCD 0–5."),
            new("27", "Flag", "Polarity correction (BGF0 at 25 frames)."),
            new("28–31", "Binary group 4", "User bits."),
            new("32–35", "Units of minutes", "BCD 0–9."),
            new("36–39", "Binary group 5", "User bits."),
            new("40–42", "Tens of minutes", "BCD 0–5."),
            new("43", "Flag", "BGF0 (BGF2 at 25 frames)."),
            new("44–47", "Binary group 6", "User bits."),
            new("48–51", "Units of hours", "BCD 0–9."),
            new("52–55", "Binary group 7", "User bits."),
            new("56–57", "Tens of hours", "BCD 0–2."),
            new("58, 59", "Flags", "BGF1; BGF2 (polarity correction at 25 frames)."),
            new("60–63", "Binary group 8", "User bits."),
            new("64–79", "Sync word", "0011111111111101: twelve ones framed by 00 and 01. Unique in the data; 64/79 complementary so a reader can tell direction (§9.2.5)."),
        ]);

    /// <summary>User bits interpretations.</summary>
    public static LtcOptionGroup UserBitsModes { get; } = new("userbits", "Binary Groups / User Bits (§8.4, §9.2.4)",
        "Eight 4-bit groups = 32 bits. Written here as 8 hex digits, group 8 first.",
        [
            new("hex", "Raw", "Eight nibbles, any meaning (BGF 000 or 010)."),
            new("text", "8-bit characters", "Four ISO/IEC 646/2022 characters; 1st in groups 7/8 … 4th in groups 1/2, low nibble in the lower group (§8.4.2, BGF 001)."),
            new("bcd", "Decimal digits", "Each group 0–9, e.g. a date or reel number (common practice)."),
            new("ST 309", "Date and time zone", "BGF 100/110: time zone code, DST and date (YYMMDD or MJD) — see 'st309' and 'timezones'."),
            new("ST 262", "Page/line multiplex", "BGF 101/111: directory index in groups 7/8 plus three data bytes — see 'st262'."),
        ]);

    /// <summary>SMPTE ST 309 date and time zone layout.</summary>
    public static LtcOptionGroup St309 { get; } = new("st309", "Date and Time Zone (SMPTE ST 309:2012)",
        "Binary groups 7–8 hold the time zone and date format; groups 1–6 hold the date. Signalled by BGF 100 (unspecified reference) or 110 (precision clock).",
        [
            new("BG7.0–BG8.1", "Time zone code TZ-0…TZ-5", "6-bit code 00–3F hex giving the offset from UTC in use (Table 2, see 'timezones')."),
            new("BG8.2", "DST flag", "0 = standard time, 1 = daylight saving time in effect. The time zone code already gives the offset in use."),
            new("BG8.3 = 0", "YYMMDD format", "Groups 1–6 = day units, day tens, month units, month tens, year units, year tens (BCD). The time address is local time."),
            new("BG8.3 = 1", "MJD format", "Groups 1–6 = Modified Julian Date, units first (six BCD digits). The time address is UTC; offset and DST are for display."),
            new("MJD", "Modified Julian Date", "Days since 17 November 1858 (JD − 2400000.5); 1 January 1995 = 49718. Increments at midnight."),
            new("§5.4", "Midnight rollover", "The date increments when the time address rolls over from 23:59:59:xx to 00:00:00:00 (the generator does this)."),
            new("BGF 100", "Unspecified reference", "Date and time zone, time address reference unspecified (Table 3)."),
            new("BGF 110", "Precision clock", "Date and time zone, time address referenced to a precision clock (Table 3)."),
        ]);

    /// <summary>SMPTE ST 309 Table 2 time zone codes.</summary>
    public static LtcOptionGroup TimeZones { get; } = new("timezones", "ST 309 Time Zone Codes (Table 2)",
        "Hex code in BG7 (low 4 bits) and BG8 bits 0–1. Locations are informative; (DST) marks places using the code in summer.",
        [.. BinaryGroups.TimeZoneCode.All.Select(z => new LtcOption(z.Hex, z.DisplayName, z.Description))]);

    /// <summary>SMPTE ST 262 page/line system.</summary>
    public static LtcOptionGroup St262 { get; } = new("st262", "Page/Line Multiplex (SMPTE ST 262)",
        "Binary group 8 = page, binary group 7 = line: a directory index saying what the other six groups carry in this frame. Signalled by BGF 101 (or 111 with clock time).",
        [
            new("byte n", "Binary bytes", "Byte 1 = BG1/BG2, byte 2 = BG3/BG4, byte 3 = BG5/BG6, byte 4 = BG7/BG8 = directory index; lower group = low nibble."),
            new("0.0–2.3", "Auxiliary time address", "Pages 0–1 lines 0–9 and page 2 lines 0–3 = hours 00–23: a second time address (SMPTE RP 169, see 'rp169')."),
            new("0.10–2.15", "Media address", "Pages 0–1 lines 10–15 and page 2 lines 4–15: media address data other than time address."),
            new("3.x–14.x", "Applications", "Twelve pages, 192 lines, for application dialects."),
            new("15.x", "Control data", "Real-time commands: bytes 2–3 = 2-byte instruction, byte 1 = checksum of bytes 2–4. Highest encoding priority."),
            new("single frame", "Single-frame message", "3-byte message in bytes 1–3; a dialect may make byte 1 a checksum of bytes 2–4."),
            new("prefix/suffix", "Message string ends", "Byte 1 = checksum of bytes 2–4; bytes 2–3 = specifier (message ID, destination, length…). Own directory index."),
            new("message", "Message string data", "Bytes 1–3 = data, byte 4 = message-frame index. ISO 646/2022 text is null-filled. Up to 256 frames."),
            new("extended", "Extended message string", "Unformatted ISO 646/2022 data between prefix and suffix frames, type signalled by the BGF."),
            new("checksum", "Error detection", "Two's complement of the low byte of the sum of the bytes; all bytes plus checksum sum to 0 mod 256."),
            new("priority", "Encoding priority", "When sources collide on one frame, the higher page wins, then the higher line."),
            new("one type", "Per-frame content", "Each frame carries one data type, identified by its directory index; repetition rate is up to the user."),
        ]);

    /// <summary>SMPTE RP 169 auxiliary time address.</summary>
    public static LtcOptionGroup Rp169 { get; } = new("rp169", "Auxiliary Time Address (SMPTE RP 169)",
        "A second time address in the binary groups, laid out like the primary address. BGF 101 (page/line); the directory index is the hours.",
        [
            new("BG1", "Frames units", "LTC bits 4–7."),
            new("BG2.0–1", "Frames tens", "LTC bits 12–13."),
            new("BG2.2", "Drop frame flag", "1 when the auxiliary address uses drop-frame counting (refers only to the auxiliary address, §3.2)."),
            new("BG2.3", "Color frame flag", "1 when color frame ID has been applied to the auxiliary address (§3.3)."),
            new("BG3", "Seconds units", "LTC bits 20–23."),
            new("BG4.0–2", "Seconds tens", "LTC bits 28–30; BG4.3 unassigned (0)."),
            new("BG5", "Minutes units", "LTC bits 36–39."),
            new("BG6.0–2", "Minutes tens", "LTC bits 44–46; BG6.3 unassigned (0)."),
            new("BG7", "Hours units", "LTC bits 52–55 = ST 262 directory line."),
            new("BG8.0–1", "Hours tens", "LTC bits 60–61 = ST 262 directory page (0–2); BG8.2–3 zero."),
            new("BGF 101", "Binary group flags", "BGF2 = 1, BGF1 = 0, BGF0 = 1 (LTC bits 59/58/43; 43/58/27 at 25 fps) (§4)."),
            new("§3.4", "Unassigned bits", "Set to 0 until assigned by SMPTE."),
        ]);

    /// <summary>Color framing.</summary>
    public static LtcOptionGroup ColorFrame { get; } = new("color", "Color Frame Identification (§5.3, §6.3)",
        "Applies when the color frame flag is 1.",
        [
            new("NTSC", "Four-field sequence", "Even frame units = color fields I & II; odd = III & IV (§5.3)."),
            new("PAL arith", "Eight-field sequence", "(seconds + frames) mod 4: 1 → fields 1–2, 2 → 3–4, 3 → 5–6, 0 → 7–8 (§6.3.2)."),
            new("PAL logic", "Eight-field sequence", "(A|B)^C^D^E^F = 1 for fields 1–4, 0 for 5–8, using the 1/2/10 bits of frames and seconds (§6.3.1)."),
        ]);

    /// <summary>Modulation and interface (§9.3–9.6).</summary>
    public static LtcOptionGroup Modulation { get; } = new("signal", "Modulation and Interface (§9.3–9.6)",
        "Biphase mark: DC-free, polarity-insensitive and self-clocking.",
        [
            new("clock", "Cell boundary", "A transition at every bit-cell boundary."),
            new("1", "Logical one", "An extra transition at the middle of the cell."),
            new("0", "Logical zero", "No extra transition."),
            new("Fe = 80·Ff", "Bit rate", "Bits evenly spaced to fill one codeword period. Unreferenced sources: ±100 ppm (§9.4)."),
            new("datum", "Timing", "First transition of bit 0 at the video reference datum, −32/+160 µs (§9.5)."),
            new("40 µs ± 10", "Rise/fall time", "10 %–90 % (§9.6.1)."),
            new("≤ 5 %", "Distortion", "Overshoot, undershoot and tilt of p-p amplitude (§9.6.2)."),
            new("1 % / 0.5 %", "Jitter", "Clock period within 1 % of average; 'one' transition mid-cell within 0.5 % (§9.6.3)."),
            new("0.5–4.5 V", "Amplitude", "Preferred 1–2 V p-p into 1 kΩ; source impedance ≤ 50 Ω (§9.6.5–9.6.6)."),
            new("XLR / BNC", "Connector", "XLR-3 male out / female in (pin 1 ground) balanced; BNC unbalanced (§9.6.4)."),
        ]);

    /// <summary>Generator settings.</summary>
    public static LtcOptionGroup GeneratorSettings { get; } = new("generator", "Generator Settings (LtcGenerator)",
        "Settings of this library's audio generator.",
        [
            new("start", "Start time code", "Address of the first codeword; its rate sets the bit rate."),
            new("sample rate", "Sample rate", "Any rate ≥ 8 kHz; 48000 is typical. Timing is exact at every rate."),
            new("amplitude", "Peak level", "0–1 of full scale (default 0.5 = −6 dBFS)."),
            new("rise", "Rise time", "10–90 % raised-cosine edge, default 40 µs; 0 = square."),
            new("invert", "Invert", "Flip waveform polarity (receivers don't care)."),
            new("reverse", "Reverse", "Send bit 79 first and count down, as a tape played backwards."),
            new("speed", "Varispeed", "Multiply the bit rate (addresses still advance one per codeword)."),
            new("userbits / bgf / cf", "Content", "User bits, binary group flags and color frame flag written into every codeword."),
            new("date", "ST 309 date", "Date, time zone, DST and YYMMDD/MJD format; the date steps at midnight rollover."),
            new("polarity", "Polarity correction", "On by default (§9.2.3)."),
            new("format", "WAV format", string.Join(", ", Enum.GetNames<WavSampleFormat>()) + "."),
        ]);

    /// <summary>Decoder settings.</summary>
    public static LtcOptionGroup DecoderSettings { get; } = new("decoder", "Decoder Settings (LtcDecoder)",
        "Settings and outputs of this library's audio reader.",
        [
            new("rate", "Frame rate", "Fixes the flag layout; omit to detect from speed, frame numbers and the DF flag."),
            new("min level", "Minimum level", "Quieter input is ignored (default 0.01 = −40 dBFS)."),
            new("direction", "Direction", "Forward (sync word last) or reverse (sync word mirrored first), reported per frame."),
            new("speed", "Measured speed", "Codeword rate ÷ nominal; negative in reverse."),
            new("start sample", "Timing", "Sample position of bit 0's first transition (the §9.5 datum)."),
            new("continuous", "Continuity", "False on a jump in the address sequence."),
        ]);

    /// <summary>All groups.</summary>
    public static IReadOnlyList<LtcOptionGroup> All { get; } =
        [FrameRates, CountingModes, CodewordLayout, Flags, BinaryGroupFlagCombos, UserBitsModes, St309, TimeZones, St262, Rp169, ColorFrame, Modulation, GeneratorSettings, DecoderSettings];

    /// <summary>Finds a group by key (e.g. "rates", "bgf").</summary>
    public static LtcOptionGroup? Find(string key) => All.FirstOrDefault(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain-text listing of the given groups (all when none given).</summary>
    public static string ToText(params LtcOptionGroup[] groups)
    {
        var sb = new StringBuilder();
        foreach (var g in groups.Length == 0 ? All : groups)
        {
            sb.AppendLine(g.Title);
            sb.AppendLine(new string('=', g.Title.Length));
            sb.AppendLine(g.Summary);
            sb.AppendLine();
            int w = Math.Max(4, g.Options.Max(o => o.Code.Length));
            foreach (var o in g.Options) sb.AppendLine($"  {o.Code.PadRight(w)}  {o.Name} — {o.Description}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Markdown tables of the given groups (all when none given).</summary>
    public static string ToMarkdown(params LtcOptionGroup[] groups)
    {
        var sb = new StringBuilder();
        foreach (var g in groups.Length == 0 ? All : groups)
        {
            sb.AppendLine($"### {g.Title}").AppendLine().AppendLine(g.Summary).AppendLine();
            sb.AppendLine("| Code | Name | Description |").AppendLine("|---|---|---|");
            foreach (var o in g.Options) sb.AppendLine($"| `{o.Code}` | {o.Name} | {o.Description.Replace("|", "\\|", StringComparison.Ordinal)} |");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
