using System.Globalization;
using System.Text;
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LinearTimecode.Cli;

internal static class Program
{
    private const string Usage = """
        ltc — SMPTE ST 12-1 Linear Time Code utility

        Reference
          ltc options [group…] [--markdown]     every option in the spec and in this tool, explained
                                                groups: rates counting bits flags bgf userbits st309 timezones st262 rp169
                                                        color signal generator decoder
          ltc rates                             frame rates with exact timing
          ltc bits [--rate R]                   the 80-bit codeword map for a rate's flag layout

        Codewords
          ltc explain <tc | hex | bits> [--rate R]   explain a time code or a codeword field by field
          ltc encode <tc> [content] [--rate R] [--table]
                                                build the codeword: hex bytes, bit string, (optional) bit table

        User bits (binary groups)
          ltc userbits <hex> [--bgf N] [--rate R]    explain user bits in the mode the binary group flags select
          ltc timezones                         SMPTE ST 309 time zone codes (Table 2)
          ltc date [YYYY-MM-DD | today] [--tz CODE|±HH:MM] [--dst] [--mjd] [--clock]
                                                SMPTE ST 309 date and time zone → user bits + BGF
          ltc pageline <page.line> [B1 B2 B3] [--checksum] | --aux TC [--aux-cf] | --control LINE C1 C2
                                                SMPTE ST 262 page/line frame → user bits
          ltc message "<text>" [--id N] [--prefix P.L] [--msg P.L] [--suffix P.L]
                                                SMPTE ST 262 message string → user bits for each frame

        Audio
          ltc generate <out.wav> [--start TC | --now] [--rate R] [--frames N | --seconds S] [content] [signal]
          ltc read <in.wav> [--rate R] [--channel N] [--min-level dBFS] [--csv | --summary]

        Arithmetic
          ltc info <tc> [--rate R]              address number, real time, color frame, validity
          ltc add <tc> <frames> [--rate R]      add (or subtract) addresses; drop-frame aware, wraps at 24 h
          ltc diff <tc1> <tc2> [--rate R]       addresses and real time between two time codes
          ltc convert <tc> --rate R --to R2     same real time at another rate

        Options
          --rate R          23.98 24 25 29.97 29.97df 30 47.95 48 50 59.94 59.94df 60 (default 30; ';' in a TC selects DF)
          content:  --ub HEX (8 hex digits, group 8 first) | --text ABCD (§8.4.2 characters, sets BGF 001)
                    --bgf 0-7 | --bgf 110 (BGF2 BGF1 BGF0)   --cf (color frame flag)   --no-polarity
                    --date YYYY-MM-DD|today [--tz CODE|±HH:MM] [--dst] [--mjd] [--clock]   (ST 309, BGF 100/110)
                    --pageline P.L [--bytes "B1 B2 B3"] [--clock]                          (ST 262, BGF 101/111)
                    generate only: --message "<text>" [--id N]  cycles an ST 262 message string through the frames
                                   --aux-start TC [--aux-cf]    running RP 169 auxiliary time address (BGF 101)
          signal:   --sr 48000   --format pcm16|pcm24|float32   --level -6 (dBFS peak)   --rise 40 (µs, 0 = square)
                    --invert   --reverse   --speed 1.0

        Examples
          ltc explain 01:00:00;00 --rate 29.97
          ltc encode 10:00:00:00 --rate 25 --text REEL --table
          ltc generate tc.wav --start 00:59:50:00 --rate 25 --seconds 30
          ltc read tc.wav --summary
          ltc generate dated.wav --start 23:59:50:00 --rate 25 --date 2026-12-31 --tz +01:00 --seconds 20
          ltc generate msg.wav --rate 30 --message "HELLO WORLD" --seconds 5
          ltc generate aux.wav --start 01:00:00:00 --rate 29.97df --aux-start 10:00:00;00 --seconds 10
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            var a = new Args(args[1..]);
            return args[0].ToLowerInvariant() switch
            {
                "options" => Options(a),
                "rates" => Rates(),
                "bits" => Bits(a),
                "explain" => Explain(a),
                "encode" => Encode(a),
                "generate" or "gen" => Generate(a),
                "read" or "decode" => Read(a),
                "info" => Info(a),
                "add" => Add(a),
                "diff" => Diff(a),
                "convert" => Convert(a),
                "userbits" or "ub" => UserBitsCmd(a),
                "timezones" or "tz" => TimeZones(),
                "date" => Date(a),
                "pageline" or "pl" => PageLine(a),
                "message" or "msg" => Message(a),
                _ => Fail($"Unknown command '{args[0]}'. Run 'ltc help'."),
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or OverflowException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    // ---------------------------------------------------------------- reference

    private static int Options(Args a)
    {
        var groups = new List<LtcOptionGroup>();
        foreach (string key in a.Positional)
            groups.Add(LtcOptions.Find(key) ?? throw new ArgumentException($"Unknown group '{key}'. Groups: {string.Join(", ", LtcOptions.All.Select(g => g.Key))}."));
        Console.Write(a.Flag("markdown") ? LtcOptions.ToMarkdown([.. groups]) : LtcOptions.ToText([.. groups]));
        return 0;
    }

    private static int Rates()
    {
        Console.WriteLine($"{"Token",-9} {"Name",-10} {"Video fps",-14} {"Codewords/s",-13} {"Bit rate",-11} {"Bit period",-11} {"Addr/day",-10} Description");
        foreach (var r in LtcFrameRateExtensions.All)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Token(),-9} {r.DisplayName(),-10} {r.VideoFrameRate(),-14:0.#########} {r.CodewordRate(),-13:0.######} {r.BitRate(),-11:0.###} {r.BitPeriodMicroseconds(),-8:0.0} µs  {r.AddressesPerDay(),-10} {r.Description()}"));
        }
        return 0;
    }

    private static int Bits(Args a)
    {
        var rate = a.Rate();
        Console.WriteLine($"LTC codeword, {(int)rate.Base()}-frame flag layout (ST 12-1 Tables 2–5)");
        for (int i = 0; i < 80; i++) Console.WriteLine($"  {i,2}  {LtcBits.NameOf(i, rate.Base())}");
        return 0;
    }

    // ---------------------------------------------------------------- codewords

    private static int Explain(Args a)
    {
        string input = string.Join(' ', a.Positional);
        if (input.Length == 0) return Fail("explain needs a time code, hex bytes or a bit string.");
        if (!LtcDescriber.TryExplain(input, a.Rate(), out string? text, out string? error)) return Fail(error);
        Console.Write(text);
        return 0;
    }

    private static int Encode(Args a)
    {
        var frame = BuildFrame(a, a.PositionalAt(0, "time code"));
        var cw = frame.ToCodeword();
        Console.WriteLine($"Frame   {frame}");
        Console.WriteLine($"Hex     {cw.ToHex()}");
        Console.WriteLine($"Bits    {cw.ToBitString()}");
        Console.WriteLine($"        (bit 0 first; {cw.ZeroCount + 3} zeros, polarity bit {LtcBits.PolarityCorrection(frame.Rate.Base())} = {(cw[LtcBits.PolarityCorrection(frame.Rate.Base())] ? 1 : 0)})");
        foreach (var issue in frame.Validate()) Console.WriteLine($"note: {issue}");
        if (a.Flag("table"))
        {
            Console.WriteLine();
            Console.Write(LtcDescriber.BitTable(cw, frame.Rate.Base()));
        }
        return 0;
    }

    // ---------------------------------------------------------------- audio

    private static int Generate(Args a)
    {
        string path = a.PositionalAt(0, "output .wav path");
        var rate = a.Rate();
        if (a.Flag("now") && a.Value("start") is not null) return Fail("Use either --start or --now, not both.");
        if (a.Value("seconds") is not null && a.Value("frames") is not null) return Fail("Use either --frames or --seconds, not both.");
        if (a.Value("message") is not null && a.Value("aux-start") is not null) return Fail("Use either --message or --aux-start, not both.");
        if ((a.Value("message") ?? a.Value("aux-start")) is not null && ContentOptions.Any(o => a.Value(o) is not null))
            return Fail("--message and --aux-start write the user bits of every frame; don't combine them with --ub, --text, --date or --pageline.");

        LtcFrame frame;
        if (a.Flag("now"))
        {
            // ST 309: with an MJD date the time address is UTC; with YYMMDD it is local time at the date's time zone.
            frame = BuildFrame(a, null, Timecode.Zero(rate));
            var now = DateTimeOffset.UtcNow;
            var time = frame.GetDateTimeZone() switch
            {
                { TimeAddressIsUtc: true } => now.UtcDateTime.TimeOfDay,
                { TimeZone.Offset: { } offset } => now.ToOffset(offset).TimeOfDay,
                _ => now.ToLocalTime().TimeOfDay,
            };
            frame = frame with { Timecode = Timecode.FromTimeOfDay(time, rate) };
        }
        else
        {
            frame = BuildFrame(a, a.Value("start") ?? "00:00:00:00");
        }
        rate = frame.Rate;

        int sr = a.Int("sr", 48_000);
        double frameCount = a.Value("seconds") is not null
            ? Math.Ceiling(a.Double("seconds", 0) * rate.CodewordRate())
            : a.Int("frames", (int)Math.Ceiling(10 * rate.CodewordRate()));
        if (double.IsNaN(frameCount) || frameCount <= 0) return Fail("Nothing to generate.");
        if (frameCount > int.MaxValue) return Fail("Output too long for a WAV file.");
        int frames = (int)frameCount;

        double speed = a.Double("speed", 1.0);
        double level = a.Double("level", -6), rise = a.Double("rise", 40);
        if (level > 0) return Fail("--level is a peak level in dBFS and must be 0 or below.");
        if (rise < 0) return Fail("--rise must be 0 (square edges) or more.");
        var gen = new LtcGenerator(frame, sr)
        {
            Amplitude = (float)Math.Pow(10, level / 20),
            RiseTime = TimeSpan.FromTicks((long)Math.Round(rise * 10)),
            Invert = a.Flag("invert"),
            Reverse = a.Flag("reverse"),
            Speed = speed,
        };
        if (a.Value("message") is { } messageText)
        {
            var layout = Layout(a);
            var msgFrames = layout.EncodeText(messageText, a.Byte("id", 0));
            bool clock = a.Flag("clock");
            gen.FrameHook = (i, f) => f.WithPageLine(msgFrames[(int)(i % msgFrames.Count)], clock);
            Console.WriteLine($"ST 262 message string: {msgFrames.Count} frames repeating ({layout.Prefix} prefix, {layout.Message} message, {layout.Suffix} suffix).");
        }
        if (a.Value("aux-start") is { } auxStart)
        {
            var aux = new AuxiliaryTimeAddress(a.Tc(auxStart), a.Flag("aux-cf"));
            gen.FrameHook = AuxiliaryTimeAddress.RunningHook(aux, a.Flag("clock"));
            Console.WriteLine($"RP 169 auxiliary time address running from {aux.Timecode} (BGF 101).");
        }
        var format = ParseFormat(a.Value("format") ?? "pcm16");
        long samples = (long)Math.Ceiling(LtcGenerator.SamplesFor(frames, rate, sr) / speed);
        WriteOutput(path, fs => WavFile.Write(fs, sr, samples, gen.Read, format));

        var last = gen.Reverse ? frame.Timecode.AddFrames(-(frames - 1)) : frame.Timecode.AddFrames(frames - 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Wrote {path}: {frames} codewords {frame.Timecode} → {last} at {rate.DisplayName()}, {sr} Hz {format}, {TimeSpan.FromSeconds((double)samples / sr):hh\\:mm\\:ss\\.fff}, peak {20 * Math.Log10(gen.Amplitude):0.#} dBFS{(gen.Reverse ? ", reverse" : "")}{(speed != 1 ? $", x{speed}" : "")}."));
        foreach (var issue in frame.Validate()) Console.WriteLine($"note: {issue}");
        return 0;
    }

    private static int Read(Args a)
    {
        string path = a.PositionalAt(0, "input .wav path");
        var wav = WavFile.Read(path);
        int channel = a.Int("channel", 1);
        if (channel < 1 || channel > wav.ChannelCount) return Fail($"Channel {channel} out of range 1–{wav.ChannelCount}.");

        LtcFrameRate? rate = a.Value("rate") is { } r ? LtcFrameRateExtensions.Parse(r) : null;
        var decoder = new LtcDecoder(wav.SampleRate, rate) { MinimumLevel = Math.Pow(10, a.Double("min-level", -40) / 20) };
        var frames = decoder.Process(wav.Channels[channel - 1]);

        Console.Error.WriteLine($"{path}: {wav.SampleRate} Hz, {wav.SourceBitsPerSample}-bit, {wav.ChannelCount} ch, {wav.Duration:hh\\:mm\\:ss\\.fff}, channel {channel}");
        if (frames.Count == 0) { Console.Error.WriteLine("No LTC found."); return 2; }
        var first = frames[0];

        bool csv = a.Flag("csv");
        if (csv && a.Flag("summary")) return Fail("Use either --csv or --summary, not both.");
        if (csv) Console.WriteLine("timecode,rate,start_sample,start_seconds,direction,speed,userbits,bgf,color_frame,continuous,hex,meaning");
        if (!a.Flag("summary"))
        {
            foreach (var f in frames)
            {
                if (csv)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{f.Timecode},{f.Frame.Rate.Token()},{f.StartSample:0.00},{f.StartSample / wav.SampleRate:0.000000},{f.Direction},{f.Speed:0.0000},{f.Frame.UserBits},{f.Frame.BinaryGroupFlags.BitPattern()},{(f.Frame.ColorFrame ? 1 : 0)},{(f.IsContinuous ? 1 : 0)},{f.Codeword.ToHex()},\"{UserBitsDescriber.Summary(f.Frame.UserBits, f.Frame.BinaryGroupFlags, f.Frame.Rate).Replace("\"", "\"\"")}\""));
                else
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{f.StartSample / wav.SampleRate,10:0.000}s  {f.Timecode}  {(f.Direction == LtcDirection.Reverse ? "REV" : "FWD")} x{Math.Abs(f.Speed):0.000}  UB {f.Frame.UserBits.ToDisplayString()}  BGF {f.Frame.BinaryGroupFlags.BitPattern()}{(f.Frame.ColorFrame ? "  CF" : "")}{(f.Frame.BinaryGroupFlags.CarriesDateTimeZone() || f.Frame.BinaryGroupFlags.CarriesPageLine() ? "  " + UserBitsDescriber.Summary(f.Frame.UserBits, f.Frame.BinaryGroupFlags, f.Frame.Rate) : "")}{(f.IsContinuous || f == first ? "" : "  ← jump")}{(f.Issues.Count > 0 ? "  ! " + string.Join("; ", f.Issues) : "")}"));
            }
        }

        var lastFrame = frames[^1];
        int jumps = frames.Skip(1).Count(f => !f.IsContinuous);
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Frames      {frames.Count}  ({first.Timecode} → {lastFrame.Timecode})");
        Console.Error.WriteLine($"Rate        {(rate is null ? "detected " : "")}{decoder.DetectedRate?.DisplayName()} — {decoder.DetectedRate?.Description()}");
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Measured    {decoder.MeasuredCodewordRate:0.####} codewords/s, speed x{Math.Abs(lastFrame.Speed):0.0000} {lastFrame.Direction}"));
        Console.Error.WriteLine($"Jumps       {jumps}");
        Console.Error.WriteLine($"User bits   {lastFrame.Frame.UserBits.ToDisplayString()}  BGF {lastFrame.Frame.BinaryGroupFlags.BitPattern()} {lastFrame.Frame.BinaryGroupFlags.DisplayName()}");
        Console.Error.WriteLine($"Meaning     {UserBitsDescriber.Summary(lastFrame.Frame.UserBits, lastFrame.Frame.BinaryGroupFlags, lastFrame.Frame.Rate)}");
        if (lastFrame.Frame.GetDateTimeZone() is { } dtz && dtz.ToDateTimeOffset(lastFrame.Timecode) is { } instant)
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Instant     {instant:yyyy-MM-dd HH:mm:ss.fff zzz} (last frame)"));
        var pageLine = frames.Select(f => f.Frame.GetPageLine()).OfType<PageLineFrame>().ToList();
        if (pageLine.Count > 0)
        {
            var messages = Layout(a).Decode(pageLine);
            foreach (var msg in messages.DistinctBy(x => (x.MessageId, x.Text)).Take(20)) Console.Error.WriteLine($"Message     {msg}");
            Console.Error.WriteLine($"Page/line   {pageLine.Count} frames, directories {string.Join(", ", pageLine.Select(p => p.Index.ToString()).Distinct().Take(16))}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- arithmetic

    private static int Info(Args a)
    {
        var tc = a.Tc(a.PositionalAt(0, "time code"));
        var r = tc.Rate;
        Console.WriteLine($"Time code     {tc}  ({r.DisplayName()} — {r.Description()})");
        Console.WriteLine($"Address #     {tc.TotalFrames} of {r.AddressesPerDay()} per day");
        if (r.IsFramePair()) Console.WriteLine($"Video frames  {tc.VideoFrameIndex} and {tc.VideoFrameIndex + 1} (frame pair)");
        Console.WriteLine($"Real time     {tc.ToTimeSpan():hh\\:mm\\:ss\\.fffffff} since 00:00:00:00");
        Console.WriteLine($"Color frame   {ColorFraming.Describe(tc)}");
        Console.WriteLine($"Next / prev   {tc.Next()} / {tc.Previous()}");
        return 0;
    }

    private static int Add(Args a)
    {
        var tc = a.Tc(a.PositionalAt(0, "time code"));
        string count = a.PositionalAt(1, "frame count");
        if (!long.TryParse(count, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n)) return Fail($"'{count}' is not a whole number of frames.");
        Console.WriteLine(tc.AddFrames(n));
        return 0;
    }

    private static int Diff(Args a)
    {
        var t1 = a.Tc(a.PositionalAt(0, "first time code"));
        var t2 = a.Tc(a.PositionalAt(1, "second time code"));
        string seconds = (t2.ToTimeSpan() - t1.ToTimeSpan()).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        if (t2.Rate != t1.Rate)
        {
            // Different counting (e.g. one DF, one NDF): compare real time, and count addresses after converting.
            var t2At1 = t2.ConvertTo(t1.Rate);
            int dc = t2At1.TotalFrames - t1.TotalFrames;
            Console.WriteLine($"{seconds} s real time; {t2} @ {t2.Rate.DisplayName()} ≈ {t2At1} @ {t1.Rate.DisplayName()}, {dc} addresses at {t1.Rate.DisplayName()}");
            return 0;
        }
        int d = t2.TotalFrames - t1.TotalFrames;
        Console.WriteLine($"{d} addresses ({Timecode.FromTotalFrames(Math.Abs(d), t1.Rate)}), {seconds} s real time");
        return 0;
    }

    private static int Convert(Args a)
    {
        var tc = a.Tc(a.PositionalAt(0, "time code"));
        var to = LtcFrameRateExtensions.Parse(a.Value("to") ?? throw new ArgumentException("convert needs --to RATE."));
        var result = tc.ConvertTo(to);
        Console.WriteLine($"{tc} @ {tc.Rate.DisplayName()} = {result} @ {to.DisplayName()}  (real time {tc.ToTimeSpan():hh\\:mm\\:ss\\.fff})");
        return 0;
    }

    // ---------------------------------------------------------------- user bits

    private static int UserBitsCmd(Args a)
    {
        var bits = UserBits.Parse(a.PositionalAt(0, "user bits (8 hex digits)"));
        var flags = a.Value("bgf") is { } b ? ParseBgf(b) : BinaryGroupFlags.Unspecified;
        Console.Write(UserBitsDescriber.Explain(bits, flags, a.Rate()));
        return 0;
    }

    private static int TimeZones()
    {
        Console.WriteLine("Code  Offset       Meaning");
        foreach (var z in TimeZoneCode.All) Console.WriteLine($"{z.Hex}    {z.DisplayName,-11}  {z.Description}");
        return 0;
    }

    private static int Date(Args a)
    {
        var d = BuildDate(a, a.Positional.Count > 0 ? a.Positional[0] : "today");
        var ub = d.ToUserBits();
        var flags = a.Flag("clock") ? BinaryGroupFlags.ClockTimeDateTimeZone : BinaryGroupFlags.DateTimeZone;
        Console.WriteLine($"ST 309      {d}");
        Console.WriteLine($"User bits   {ub}   (use: --ub {ub} --bgf {flags.BitPattern()})");
        Console.WriteLine($"BGF         {flags.BitPattern()} {flags.DisplayName()}");
        Console.WriteLine();
        Console.Write(UserBitsDescriber.Explain(ub, flags));
        return 0;
    }

    private static int PageLine(Args a)
    {
        PageLineFrame f;
        if (a.Value("aux") is { } aux) f = PageLineFrame.ForAuxiliaryTimeAddress(a.Tc(aux), a.Flag("aux-cf"));
        else if (a.Value("control") is { } line)
        {
            byte c1 = ParseByte(a.PositionalAt(0, "command byte 1")), c2 = ParseByte(a.PositionalAt(1, "command byte 2"));
            if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int controlLine)) return Fail($"--control expects a line number 0–15, not '{line}'.");
            f = PageLineFrame.ControlCode(controlLine, c1, c2);
        }
        else
        {
            var index = DirectoryIndex.Parse(a.PositionalAt(0, "directory index page.line"));
            f = BuildPageLine(index, string.Join(' ', a.Positional.Skip(1)), a.Flag("checksum"));
        }
        var flags = a.Flag("clock") ? BinaryGroupFlags.ClockTimePageLine : BinaryGroupFlags.PageLine;
        var ub = f.ToUserBits();
        Console.WriteLine($"ST 262      {f.Describe(a.Rate())}");
        Console.WriteLine($"User bits   {ub}   (use: --ub {ub} --bgf {flags.BitPattern()})");
        Console.WriteLine();
        Console.Write(UserBitsDescriber.Explain(ub, flags, a.Rate()));
        return 0;
    }

    private static int Message(Args a)
    {
        string text = string.Join(' ', a.Positional);
        if (text.Length == 0) return Fail("message needs some text.");
        var layout = Layout(a);
        var frames = layout.EncodeText(text, a.Byte("id", 0));
        Console.WriteLine($"ST 262 message string, {frames.Count} frames (prefix {layout.Prefix}, message {layout.Message}, suffix {layout.Suffix}); BGF 101:");
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            string role = f.Index == layout.Prefix ? "prefix " : f.Index == layout.Suffix ? "suffix " : "message";
            Console.WriteLine($"  {i,3}  {role}  UB {f.ToUserBits()}  {f}");
        }
        var back = layout.Decode(frames);
        Console.WriteLine($"Round trip: {string.Join("; ", back)}");
        return 0;
    }

    private static DateTimeZone BuildDate(Args a, string dateText)
    {
        var format = a.Flag("mjd") ? DateFormat.ModifiedJulianDate : DateFormat.Yymmdd;
        var tz = a.Value("tz") is { } t ? TimeZoneCode.Parse(t) : TimeZoneCode.FromOffset(DateTimeOffset.Now.Offset) ?? TimeZoneCode.Utc;
        var now = DateTimeOffset.UtcNow;
        DateOnly date = dateText.ToLowerInvariant() switch
        {
            // MJD carries the UTC date; YYMMDD the local date at the chosen time zone.
            "today" or "now" => DateOnly.FromDateTime(format == DateFormat.ModifiedJulianDate ? now.UtcDateTime
                : tz.Offset is { } offset ? now.ToOffset(offset).DateTime : now.ToLocalTime().DateTime),
            _ => DateOnly.Parse(dateText, CultureInfo.InvariantCulture),
        };
        return new DateTimeZone(date, tz, format, a.Flag("dst"));
    }

    private static PageLineFrame BuildPageLine(DirectoryIndex index, string? bytesText, bool checksum)
    {
        var bytes = (bytesText ?? "").Split([' ', ',', ':'], StringSplitOptions.RemoveEmptyEntries).Select(ParseByte).ToArray();
        if (bytes.Length > 3) throw new ArgumentException("A page/line frame holds three data bytes (byte 4 is the directory index).");
        if (checksum && bytes.Length > 2) throw new ArgumentException("With --checksum, byte 1 holds the checksum; give at most two data bytes (B2 B3).");
        byte B(int i) => i < bytes.Length ? bytes[i] : (byte)0;
        return PageLineFrame.SingleFrame(index, B(0), B(1), B(2), checksum);
    }

    private static PageLineMessageLayout Layout(Args a) => new(
        a.Value("prefix") is { } p ? DirectoryIndex.Parse(p) : PageLineMessageLayout.Default.Prefix,
        a.Value("msg") is { } m ? DirectoryIndex.Parse(m) : PageLineMessageLayout.Default.Message,
        a.Value("suffix") is { } s ? DirectoryIndex.Parse(s) : PageLineMessageLayout.Default.Suffix);

    private static byte ParseByte(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b) ? b : throw new FormatException($"'{s}' is not a hex byte.");
    }

    // ---------------------------------------------------------------- helpers

    private static LtcFrame BuildFrame(Args a, string? tcText, Timecode? explicitTc = null)
    {
        var tc = explicitTc ?? a.Tc(tcText!);
        var frame = new LtcFrame(tc)
        {
            ColorFrame = a.Flag("cf"),
            PolarityCorrection = !a.Flag("no-polarity"),
        };
        if (ContentOptions.Count(o => a.Value(o) is not null) > 1)
            throw new ArgumentException("Use only one of --ub, --text, --date and --pageline; each sets all 32 user bits.");
        if (a.Value("text") is { } text)
            frame = frame with { UserBits = UserBits.FromText(text), BinaryGroupFlags = BinaryGroupFlags.EightBitCharacters };
        if (a.Value("ub") is { } ub) frame = frame with { UserBits = UserBits.Parse(ub) };
        if (a.Value("date") is { } date) frame = frame.WithDateTimeZone(BuildDate(a, date), a.Flag("clock"));
        if (a.Value("pageline") is { } pl) frame = frame.WithPageLine(BuildPageLine(DirectoryIndex.Parse(pl), a.Value("bytes"), a.Flag("checksum")), a.Flag("clock"));
        if (a.Value("bgf") is { } bgf) frame = frame with { BinaryGroupFlags = ParseBgf(bgf) };
        if (frame.ColorFrame && tc.Rate.Base() == TimecodeBase.Base24) Console.Error.WriteLine("note: 24-frame systems have no color frame flag; --cf ignored.");
        return frame;
    }

    private static BinaryGroupFlags ParseBgf(string s)
    {
        s = s.Trim();
        if (s.Length == 3 && s.All(c => c is '0' or '1')) return (BinaryGroupFlags)System.Convert.ToInt32(s, 2);
        if (int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
        {
            if (v is >= 0 and <= 7) return (BinaryGroupFlags)v;
        }
        else if (Enum.TryParse<BinaryGroupFlags>(s, true, out var f) && Enum.IsDefined(f)) return f;
        throw new FormatException($"--bgf expects 0–7, a 3-bit pattern like 110, or one of {string.Join(", ", Enum.GetNames<BinaryGroupFlags>())}.");
    }

    /// <summary>
    /// Writes a regular file through a temporary file in the same directory that is moved into place, so a failed
    /// write (e.g. a full disk) leaves no truncated file and keeps an existing file intact. Devices, pipes, symlinks,
    /// and directories where a temporary file can't be created are written directly.
    /// </summary>
    private static void WriteOutput(string path, Action<Stream> write)
    {
        var existing = new FileInfo(path);
        if (existing.Exists && (existing.LinkTarget is not null || (existing.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0))
        {
            WriteDirect(path, write); // symlink or device: write through it
            return;
        }
        if (existing.Exists)
        {
            // Pipes and character devices can't seek; write into them rather than replacing them.
            var target = new FileStream(path, FileMode.Open, FileAccess.Write);
            if (!target.CanSeek)
            {
                using (target) write(target);
                return;
            }
            target.Dispose();
        }

        string? temp = null;
        FileStream? fs = null;
        if (!Directory.Exists(path))
        {
            temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try { fs = new FileStream(temp, FileMode.CreateNew); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { temp = null; } // e.g. read-only directory
        }
        if (fs is null)
        {
            WriteDirect(path, write);
            return;
        }

        try
        {
            using (fs) write(fs);
            if (existing.Exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp!, File.GetUnixFileMode(path));
            File.Move(temp!, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp!); } catch (IOException) { } // best effort; don't hide the original error
        }
    }

    private static void WriteDirect(string path, Action<Stream> write)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        write(fs);
    }

    private static readonly string[] ContentOptions = ["ub", "text", "date", "pageline"];

    private static WavSampleFormat ParseFormat(string s) => s.ToLowerInvariant() switch
    {
        "pcm16" or "16" => WavSampleFormat.Pcm16,
        "pcm24" or "24" => WavSampleFormat.Pcm24,
        "float32" or "float" or "32" => WavSampleFormat.Float32,
        _ => throw new FormatException($"Unknown format '{s}'. Use pcm16, pcm24 or float32."),
    };

    /// <summary>Tiny argument parser: positionals, --flag and --name value.</summary>
    private sealed class Args
    {
        private static readonly HashSet<string> Switches = ["markdown", "table", "now", "cf", "no-polarity", "invert", "reverse", "csv", "summary", "dst", "mjd", "clock", "checksum", "aux-cf"];
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i];
                if (s.StartsWith("--", StringComparison.Ordinal) && s.Length > 2)
                {
                    string name = s[2..];
                    int eq = name.IndexOf('=');
                    if (eq > 0) _named[name[..eq]] = name[(eq + 1)..];
                    else if (Switches.Contains(name.ToLowerInvariant())) _named[name] = null;
                    else if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{name} needs a value.");
                    else _named[name] = args[++i];
                }
                else Positional.Add(s);
            }
        }

        public List<string> Positional { get; } = [];

        public bool Flag(string name) => _named.ContainsKey(name);

        public string? Value(string name) => _named.TryGetValue(name, out var v) ? v : null;

        public int Int(string name, int fallback) =>
            Value(name) is not { } v ? fallback
            : int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n
            : throw new FormatException($"--{name} expects a whole number, not '{v}'.");

        public byte Byte(string name, byte fallback) =>
            Int(name, fallback) is var n and >= 0 and <= 255 ? (byte)n : throw new FormatException($"--{name} expects 0–255.");

        public double Double(string name, double fallback) =>
            Value(name) is not { } v ? fallback
            : double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d
            : throw new FormatException($"--{name} expects a number, not '{v}'.");

        public LtcFrameRate Rate() => Value("rate") is { } r ? LtcFrameRateExtensions.Parse(r) : LtcFrameRate.Fps30;

        public Timecode Tc(string text) => Timecode.Parse(text, Rate());

        public string PositionalAt(int index, string what) =>
            index < Positional.Count ? Positional[index] : throw new ArgumentException($"Missing {what}. Run 'ltc help'.");
    }
}
