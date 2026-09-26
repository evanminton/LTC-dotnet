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
}
