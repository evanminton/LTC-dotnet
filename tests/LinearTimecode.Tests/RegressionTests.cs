using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LinearTimecode.Tests;

/// <summary>Regression tests for bugs found in review.</summary>
public class RegressionTests
{
    // ---------------------------------------------------------------- decoder

    [Theory]
    [InlineData(LtcFrameRate.Fps25, 48_000, "10:00:00:01")]
    [InlineData(LtcFrameRate.Fps29_97Drop, 44_100, "01:00:00;03")]
    [InlineData(LtcFrameRate.Fps24, 96_000, "00:00:10:07")]
    [InlineData(LtcFrameRate.Fps30, 44_100, "00:00:10:09")]
    [InlineData(LtcFrameRate.Fps30, 48_000, "00:00:01:05")]
    public void OddStartFrameIsNotMisread(LtcFrameRate rate, int sampleRate, string start)
    {
        // Bit 0 = 1 makes the first interval a half-cell; the bits read before the clock settles must be discarded.
        var first = new LtcFrame(Timecode.Parse(start, rate));
        var frames = LtcDecoder.DecodeAll(LtcGenerator.Render(first, 40, sampleRate), sampleRate, rate);
        Assert.InRange(frames.Count, 37, 40);
        var expected = first;
        while (expected.Timecode != frames[0].Timecode && expected.Timecode.TotalFrames < first.Timecode.TotalFrames + 3) expected = expected.Next();
        Assert.Equal(expected.Timecode, frames[0].Timecode);
        for (int i = 1; i < frames.Count; i++) Assert.True(frames[i].IsContinuous, $"jump at {frames[i].Timecode}");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void RelocksImmediatelyAfterAGap(int gapMs)
    {
        const int sr = 48_000;
        var a = LtcGenerator.Render(new LtcFrame(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps25)), 25, sr);
        var b = LtcGenerator.Render(new LtcFrame(new Timecode(2, 0, 0, 0, LtcFrameRate.Fps25)), 25, sr);
        float[] audio = [.. a, .. new float[sr * gapMs / 1000], .. b];
        var frames = LtcDecoder.DecodeAll(audio, sr, LtcFrameRate.Fps25);
        var fresh = LtcDecoder.DecodeAll(b, sr, LtcFrameRate.Fps25);
        var afterGap = frames.Where(f => f.Timecode.Hours == 2).ToList();
        Assert.True(afterGap.Count >= fresh.Count, $"{afterGap.Count} frames after the gap, {fresh.Count} from a fresh decoder");
        Assert.All(afterGap.Skip(1), f => Assert.True(f.IsContinuous));
    }

    [Fact]
    public void RelocksAfterALargeDropInSpeed()
    {
        const int sr = 48_000;
        var gen = new LtcGenerator(Timecode.Zero(LtcFrameRate.Fps25), sr) { Speed = 4 };
        float[] fast = gen.Read((int)(LtcGenerator.SamplesFor(20, LtcFrameRate.Fps25, sr) / 4));
        gen.Speed = 0.4;
        float[] slow = gen.Read((int)(LtcGenerator.SamplesFor(20, LtcFrameRate.Fps25, sr) / 0.4));
        var frames = LtcDecoder.DecodeAll([.. fast, .. slow], sr, LtcFrameRate.Fps25);
        var slowFrames = frames.Where(f => f.Speed < 1).ToList();
        Assert.True(slowFrames.Count >= 15, $"{slowFrames.Count} slow frames");
        Assert.All(slowFrames, f => Assert.InRange(f.Speed, 0.39, 0.41));
    }

    // ---------------------------------------------------------------- generator

    [Fact]
    public void GeneratorKeepsPositionWhenFrameHookThrows()
    {
        var gen = new LtcGenerator(Timecode.Zero(LtcFrameRate.Fps25), 48_000);
        gen.FrameHook = (i, f) => i == 3 ? throw new InvalidOperationException() : f;
        var buffer = new float[48_000];
        Assert.Throws<InvalidOperationException>(() => gen.Read(buffer));
        Assert.InRange(gen.SamplePosition, 1920 * 2, 1920 * 3); // stopped where codeword 3 would start
        Assert.Equal(2, gen.CurrentFrame!.Timecode.Frames);
    }

    [Fact]
    public void ReverseToggleContinuesFromTheCurrentFrame()
    {
        var gen = new LtcGenerator(new Timecode(0, 0, 1, 0, LtcFrameRate.Fps25), 48_000);
        gen.Read(1920 * 5 + 10); // inside codeword 5 (00:00:01:05)
        Assert.Equal(5, gen.CurrentFrame!.Timecode.Frames);
        gen.Reverse = true;
        Assert.Equal(new Timecode(0, 0, 1, 4, LtcFrameRate.Fps25), gen.NextFrame.Timecode);
    }

    [Fact]
    public void ReverseToggleAtMidnightKeepsTheDate()
    {
        var day = new DateOnly(2026, 9, 26);
        var frame = new LtcFrame(new Timecode(23, 59, 59, 24, LtcFrameRate.Fps25))
            .WithDateTimeZone(new DateTimeZone(day, TimeZoneCode.Utc));
        var gen = new LtcGenerator(frame, 48_000);
        gen.Read(10); // outputting 23:59:59:24; the queued next frame is 00:00:00:00 on the 27th
        Assert.Equal(day.AddDays(1), gen.NextFrame.GetDateTimeZone()!.Date);
        gen.Reverse = true;
        Assert.Equal(new Timecode(23, 59, 59, 23, LtcFrameRate.Fps25), gen.NextFrame.Timecode);
        Assert.Equal(day, gen.NextFrame.GetDateTimeZone()!.Date);

        // Reverse across midnight steps the date back; switching forward again steps it on.
        var gen2 = new LtcGenerator(frame with { Timecode = Timecode.Zero(LtcFrameRate.Fps25) }, 48_000) { Reverse = true };
        gen2.Read(10); // outputting 00:00:00:00 on the 26th; queued 23:59:59:24 on the 25th
        Assert.Equal(day.AddDays(-1), gen2.NextFrame.GetDateTimeZone()!.Date);
        gen2.Reverse = false;
        Assert.Equal(new Timecode(0, 0, 0, 1, LtcFrameRate.Fps25), gen2.NextFrame.Timecode);
        Assert.Equal(day, gen2.NextFrame.GetDateTimeZone()!.Date);
    }

    [Fact]
    public void ReverseToggleKeepsAnExplicitNextFrame()
    {
        var gen = new LtcGenerator(new Timecode(0, 0, 1, 0, LtcFrameRate.Fps25), 48_000);
        gen.Read(1920 * 5 + 10);
        var jump = new LtcFrame(new Timecode(10, 0, 0, 0, LtcFrameRate.Fps25));
        gen.NextFrame = jump;
        gen.Reverse = true;
        Assert.Equal(jump, gen.NextFrame);
        gen.Read(1920);
        Assert.Equal(jump.Timecode, gen.CurrentFrame!.Timecode);
        Assert.Equal(new Timecode(9, 59, 59, 24, LtcFrameRate.Fps25), gen.NextFrame.Timecode);
    }

    [Fact]
    public void GeneratorAtTheEndOfTheYymmddRangeKeepsItsDate()
    {
        var frame = new LtcFrame(new Timecode(23, 59, 59, 24, LtcFrameRate.Fps25))
            .WithDateTimeZone(new DateTimeZone(new DateOnly(2069, 12, 31), TimeZoneCode.Utc));
        var gen = new LtcGenerator(frame, 48_000);
        gen.Read(1920 * 2 + 10);
        Assert.Equal(new DateOnly(2069, 12, 31), gen.CurrentFrame!.GetDateTimeZone()!.Date);
    }

    // ---------------------------------------------------------------- WAV

    [Theory]
    [InlineData(WavSampleFormat.Pcm16, 1, 44)]
    [InlineData(WavSampleFormat.Pcm24, 1, 68)]
    [InlineData(WavSampleFormat.Float32, 1, 58)]
    [InlineData(WavSampleFormat.Pcm16, 3, 68)]
    public void WavHeaderAndPadding(WavSampleFormat format, int channels, int headerSize)
    {
        var ch = Enumerable.Range(0, channels).Select(_ => new[] { 0.5f, -0.5f, 0.25f }).ToArray();
        using var ms = new MemoryStream();
        new WavFile(48_000, ch).Write(ms, format);
        byte[] bytes = ms.ToArray();
        int dataSize = (int)format / 8 * channels * 3;
        Assert.Equal(headerSize + dataSize + (dataSize & 1), bytes.Length);
        Assert.Equal(bytes.Length - 8, BitConverter.ToInt32(bytes, 4)); // RIFF size includes the pad byte
        ms.Position = 0;
        var back = WavFile.Read(ms);
        Assert.Equal(channels, back.ChannelCount);
        Assert.Equal(3, back.SampleCount);
        Assert.Equal((int)format, back.SourceBitsPerSample);
        Assert.InRange(back.Channels[channels - 1][2], 0.249f, 0.251f);
    }

    [Fact]
    public void StreamingWriteMatchesBufferedWrite()
    {
        var start = new LtcFrame(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps25));
        float[] audio = LtcGenerator.Render(start, 100, 48_000);
        using var a = new MemoryStream();
        WavFile.Write(a, audio, 48_000, WavSampleFormat.Pcm24);
        using var b = new MemoryStream();
        var gen = new LtcGenerator(start, 48_000);
        WavFile.Write(b, 48_000, audio.Length, gen.Read, WavSampleFormat.Pcm24);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void WavReaderRejectsBadFilesCleanly()
    {
        using var ms = new MemoryStream();
        WavFile.Write(ms, new float[10], 48_000);
        byte[] good = ms.ToArray();

        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream(good[..30])));   // truncated fmt
        byte[] zeroAlign = (byte[])good.Clone();
        zeroAlign[32] = 0; zeroAlign[33] = 0;                                                   // block align 0
        Assert.Throws<InvalidDataException>(() => WavFile.Read(new MemoryStream(zeroAlign)));
        Assert.Throws<ArgumentException>(() => new WavFile(48_000, [new float[3], new float[2]]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WavFile(0, [new float[3]]));
    }

    [Fact]
    public void WavReaderAcceptsNonSeekableStreamsAndFmtAfterData()
    {
        using var ms = new MemoryStream();
        WavFile.Write(ms, [0.5f, -0.5f], 48_000);
        byte[] b = ms.ToArray();
        // Move the fmt chunk (bytes 12–35) after the data chunk.
        byte[] swapped = [.. b[..12], .. b[36..], .. b[12..36]];
        var wav = WavFile.Read(new NonSeekableStream(swapped));
        Assert.Equal(2, wav.SampleCount);
        Assert.InRange(wav.Channels[0][0], 0.49f, 0.51f);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- core

    [Fact]
    public void StrayDropFrameFlagIsReportedAndKept()
    {
        var cw = new LtcFrame(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps30)).ToCodeword().WithBit(10, true).WithPolarityCorrection(TimecodeBase.Base30);
        var frame = LtcFrame.FromCodeword(cw, LtcFrameRate.Fps30);
        Assert.Equal(LtcFrameRate.Fps30, frame.Rate);
        Assert.True(frame.StrayDropFrameFlag);
        Assert.Contains(frame.Validate(), i => i.Contains("drop-frame", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(cw, frame.ToCodeword());
    }

    [Theory]
    [InlineData("00:01:00;1", LtcFrameRate.Fps29_97, false)]   // ';' selects DF → 00:01:00;01 is a dropped frame
    [InlineData("01;00:00:00", LtcFrameRate.Fps29_97, false)]  // mixed separators
    [InlineData("01.00.00.00", LtcFrameRate.Fps29_97, true)]
    [InlineData("01:00:00.00", LtcFrameRate.Fps29_97, true)]
    public void ParseUsesTheSeparatorBeforeTheFrames(string text, LtcFrameRate rate, bool ok)
    {
        Assert.Equal(ok, Timecode.TryParse(text, rate, out var tc));
        if (ok) Assert.True(tc.Rate.IsDropFrame());
    }

    [Fact]
    public void BitPeriodRoundsToTheNearestTick()
    {
        Assert.Equal(TimeSpan.FromTicks(4167), LtcFrameRate.Fps30.BitPeriod());
        Assert.Equal(416.667, LtcFrameRate.Fps30.BitPeriodMicroseconds(), 3);
    }

    // ---------------------------------------------------------------- ST 309 / RP 169 / ST 262

    [Fact]
    public void DateRollsOnlyWithinItsFormat()
    {
        var end = new DateTimeZone(new DateOnly(2069, 12, 31), TimeZoneCode.Utc);
        Assert.Throws<ArgumentOutOfRangeException>(() => end.AddDays(1));
        var frame = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps25)).WithDateTimeZone(end);
        Assert.Equal(frame, frame.RollDate(1));

        var start = new DateTimeZone(new DateOnly(1970, 1, 1), TimeZoneCode.Utc);
        var f2 = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps25)).WithDateTimeZone(start);
        Assert.Equal(f2, f2.RollDate(-1));

        var mjdEnd = new DateTimeZone(DateTimeZone.FromMjd(999_999), TimeZoneCode.Utc, DateFormat.ModifiedJulianDate);
        Assert.Throws<ArgumentOutOfRangeException>(() => mjdEnd.AddDays(1));

        var bad = end with { Date = new DateOnly(2150, 1, 1) };
        Assert.Throws<InvalidOperationException>(() => bad.ToUserBits());
        Assert.NotEmpty(bad.Validate());
    }

    [Theory]
    [InlineData(LtcFrameRate.Fps24)]
    [InlineData(LtcFrameRate.Fps25)]
    [InlineData(LtcFrameRate.Fps30)]
    public void AuxiliaryDropFrameFlagRoundTrips(LtcFrameRate rate)
    {
        var bits = UserBits.Parse("00000040"); // BG 2 bit 2 set: 00:00:00:00 with the DF flag
        var aux = AuxiliaryTimeAddress.FromUserBits(bits, rate)!;
        Assert.True(aux.DropFrame);
        Assert.True(aux.StrayDropFrameFlag);
        Assert.Equal(bits, aux.ToUserBits());
        Assert.Contains(aux.Validate(), i => i.Contains("Drop frame", StringComparison.Ordinal));
        Assert.Contains("BG2 bit 2 = 1", UserBitsDescriber.Explain(bits, BinaryGroupFlags.PageLine, rate));
    }

    [Fact]
    public void AuxiliaryUnassignedBitsDoNotTouchTheHours()
    {
        var aux = new AuxiliaryTimeAddress(new Timecode(21, 2, 3, 4, LtcFrameRate.Fps25)) { UnassignedBits = 0xF };
        Assert.Equal(new DirectoryIndex(2, 1), aux.ToPageLineFrame().Index);
        var back = AuxiliaryTimeAddress.FromUserBits(aux.ToUserBits(), LtcFrameRate.Fps25)!;
        Assert.Equal(3, back.UnassignedBits);
        Assert.Equal(aux.Timecode, back.Timecode);
    }

    [Fact]
    public void MessageKeepsRealTrailingZerosBeyondTheFill()
    {
        var layout = PageLineMessageLayout.Default;
        Assert.Equal(new byte[] { 1, 2, 0, 0 }, layout.Decode(layout.Encode([1, 2, 0, 0])).Single().Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, layout.Decode(layout.Encode([1, 2, 3])).Single().Data);
        Assert.Throws<ArgumentException>(() => new PageLineMessageLayout(new(3, 1), new(3, 1), new(3, 2)));
    }

    [Theory]
    [InlineData("15.3", 15, 3)]
    [InlineData("p15 l3", 15, 3)]
    [InlineData("page 2 line 3", 2, 3)]
    [InlineData("line 3 page 2", 2, 3)]
    [InlineData("L3 P2", 2, 3)]
    [InlineData("F3", 15, 3)]
    public void DirectoryIndexParse(string text, int page, int line) =>
        Assert.Equal(new DirectoryIndex(page, line), DirectoryIndex.Parse(text));

    [Fact]
    public void TimeZoneAfterUtcNeedsASign()
    {
        Assert.Throws<FormatException>(() => TimeZoneCode.Parse("UTC5"));
        Assert.Equal(TimeSpan.FromHours(5), TimeZoneCode.Parse("UTC+5").Offset);
        Assert.Equal(TimeSpan.FromHours(-5), TimeZoneCode.Parse("GMT-05:00").Offset);
        Assert.Equal(TimeSpan.FromHours(-5), TimeZoneCode.Parse("05").Offset);
    }

    [Fact]
    public void TryExplainReportsBadInput()
    {
        Assert.False(LtcDescriber.TryExplain("garbage", LtcFrameRate.Fps30, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.True(LtcDescriber.TryExplain("01:00:00:00", LtcFrameRate.Fps25, out string? text, out _));
        Assert.Contains("01:00:00:00", text);
    }

    // ---------------------------------------------------------------- full review, 2026-09-29

    [Theory]
    [InlineData(LtcFrameRate.Fps25, 1.15)]
    [InlineData(LtcFrameRate.Fps25, 1.2)]
    [InlineData(LtcFrameRate.Fps30, 0.8)]
    [InlineData(LtcFrameRate.Fps24, 1.2)]
    public void RateDetectionUsesTheFrameCountAtVarispeed(LtcFrameRate rate, double speed)
    {
        // Start just before a second rolls over, so the count is known almost at once.
        var start = new LtcFrame(new Timecode(1, 0, 0, rate.FramesPerSecond() - 3, rate)) { BinaryGroupFlags = BinaryGroupFlags.ClockTimeDateTimeZone };
        var gen = new LtcGenerator(start, 48_000) { Speed = speed };
        float[] audio = gen.Read((int)(LtcGenerator.SamplesFor(3 * rate.FramesPerSecond(), rate, 48_000) / speed));
        var decoder = new LtcDecoder(48_000);
        var frames = decoder.Process(audio);
        // Every frame after the rollover is read with the right layout, so its flags are right.
        var after = frames.SkipWhile(f => f.Timecode.Seconds == 0).ToList();
        Assert.True(after.Count > 2 * rate.FramesPerSecond());
        Assert.All(after, f => Assert.Equal(rate.Base(), f.Frame.Rate.Base()));
        Assert.All(after, f => Assert.Equal(BinaryGroupFlags.ClockTimeDateTimeZone, f.Frame.BinaryGroupFlags));
        Assert.Equal(rate.Base(), decoder.DetectedRate!.Value.Base());
    }

    [Fact]
    public void DropFrameMinuteRolloverGivesThirtyFrames()
    {
        var rate = LtcFrameRate.Fps29_97Drop;
        var gen = new LtcGenerator(new LtcFrame(Timecode.Parse("00:00:59;27", rate)), 48_000) { Speed = 0.8 };
        float[] audio = gen.Read((int)(LtcGenerator.SamplesFor(10, rate, 48_000) / 0.8));
        var frames = LtcDecoder.DecodeAll(audio, 48_000);
        Assert.Contains(frames, f => f.Timecode.ToString() == "00:01:00;02");
        Assert.Equal(rate, frames[^1].Frame.Rate);
    }

    [Theory]
    [InlineData("0000000000000001")]
    [InlineData("0000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void TryExplainReadsDigitOnlyCodewordsAsCodewords(string input)
    {
        var cw = LtcCodeword.Parse(input);
        Assert.True(LtcDescriber.TryExplain(input, LtcFrameRate.Fps30, out string? text, out _));
        Assert.Equal(LtcDescriber.Explain(cw, LtcFrameRate.Fps30), text);
        Assert.True(LtcDescriber.TryExplain("10", LtcFrameRate.Fps30, out text, out _)); // a bare frame number is still a time code
        Assert.StartsWith("Time code      00:00:00:10", text);
    }

    [Fact]
    public void BcdErrorCodewordRoundTrips()
    {
        var cw = new LtcCodeword(0).WithBits(LtcBits.FrameUnits, 4, 12).WithBits(LtcBits.FrameTens, 2, 1)
            .WithBits(LtcBits.HourUnits, 4, 15).WithPolarityCorrection(TimecodeBase.Base30);
        var frame = LtcFrame.FromCodeword(cw, LtcFrameRate.Fps30);
        Assert.True(frame.BcdError);
        Assert.Equal(cw, frame.ToCodeword());
        Assert.Equal(cw, (frame with { UserBits = new UserBits(0x12345678) }).ToCodeword().WithBits(4, 4, 0).WithBits(12, 4, 0)
            .WithBits(20, 4, 0).WithBits(28, 4, 0).WithBits(36, 4, 0).WithBits(44, 4, 0).WithBits(52, 4, 0).WithBits(60, 4, 0)
            .WithPolarityCorrection(TimecodeBase.Base30));
        // A new address is encoded normally.
        var moved = frame with { Timecode = new Timecode(1, 0, 0, 0) };
        Assert.Equal(new Timecode(1, 0, 0, 0), LtcFrame.FromCodeword(moved.ToCodeword(), LtcFrameRate.Fps30).Timecode);
    }

    [Theory]
    [InlineData(45, 0, 0, 0)]
    [InlineData(0, 80, 0, 0)]
    [InlineData(0, 0, 80, 0)]
    [InlineData(0, 0, 0, 40)]
    [InlineData(-1, 0, 0, 0)]
    public void ToCodewordRejectsFieldsThatDontFitTheirDigits(int h, int m, int s, int f)
    {
        var frame = new LtcFrame(Timecode.CreateUnchecked(h, m, s, f, LtcFrameRate.Fps25));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.ToCodeword());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuxiliaryTimeAddress(Timecode.CreateUnchecked(h, m, s, f, LtcFrameRate.Fps25)).ToUserBits());
    }

    [Fact]
    public void OutOfRangeButEncodableAddressesStillEncode()
    {
        // Hour 25 fits the digits; it is written as is and reported by Validate.
        var frame = new LtcFrame(Timecode.CreateUnchecked(25, 0, 0, 0, LtcFrameRate.Fps25));
        var back = LtcFrame.FromCodeword(frame.ToCodeword(), LtcFrameRate.Fps25);
        Assert.Equal(25, back.Timecode.Hours);
        Assert.NotEmpty(back.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(-1)]
    public void UserBitsIndexerRejectsBadGroups(int group) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UserBits(0x80000000)[group]);

    [Fact]
    public void DefaultTimecodeIsThirtyFps()
    {
        Assert.Equal(LtcFrameRate.Fps30, default(Timecode).Rate);
        Assert.Equal(new Timecode(0, 0, 0, 0), default);
        foreach (var rate in LtcFrameRateExtensions.All) Assert.Equal(rate, new Timecode(1, 2, 3, 4, rate).Rate);
    }

    [Fact]
    public void CompareOrdersByRealTimeAcrossRates()
    {
        var a = new Timecode(1, 0, 0, 0, LtcFrameRate.Fps25);
        Assert.Equal(0, a.CompareTo(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps30)));
        Assert.NotEqual(a, new Timecode(1, 0, 0, 0, LtcFrameRate.Fps30));
        Assert.True(new Timecode(0, 50, 0, 0, LtcFrameRate.Fps30) < a); // earlier in real time, though a larger address count
        // NTSC time runs slow: 01:00:00:00 NDF at 29.97 is later than 01:00:00:00 at 30.
        Assert.True(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps29_97) > new Timecode(1, 0, 0, 0, LtcFrameRate.Fps30));
        // Same instant, different counting.
        var df = Timecode.Parse("00:10:00;00", LtcFrameRate.Fps29_97Drop);
        Assert.Equal(0, df.CompareTo(Timecode.FromTotalFrames(df.TotalFrames, LtcFrameRate.Fps29_97)));
        Assert.True(new Timecode(0, 0, 0, 1, LtcFrameRate.Fps24) > new Timecode(0, 0, 0, 1, LtcFrameRate.Fps25));
    }

    [Fact]
    public void CenturyPivotIsPerValue()
    {
        var ub = new DateTimeZone(new DateOnly(2065, 3, 4), TimeZoneCode.Utc).ToUserBits();
        Assert.Equal(new DateOnly(2065, 3, 4), DateTimeZone.FromUserBits(ub).Date);
        var old = DateTimeZone.FromUserBits(ub, centuryPivot: 60);
        Assert.Equal(new DateOnly(1965, 3, 4), old.Date);
        Assert.Equal(60, old.CenturyPivot);
        Assert.Equal(ub, old.ToUserBits());
        Assert.Equal(60, old.AddDays(1).CenturyPivot);
        // Other values keep their own pivot.
        Assert.Equal(DateTimeZone.DefaultCenturyPivot, new DateTimeZone(new DateOnly(2065, 1, 1), TimeZoneCode.Utc).CenturyPivot);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DateTimeZone(new DateOnly(2075, 1, 1), TimeZoneCode.Utc, centuryPivot: 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => DateTimeZone.FromUserBits(ub, centuryPivot: 100));
    }
}
