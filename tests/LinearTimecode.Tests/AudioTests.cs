using LinearTimecode;
using LinearTimecode.Audio;

namespace LinearTimecode.Tests;

public class AudioTests
{
    [Fact]
    public void BiphaseMarkRules()
    {
        Span<bool> half = stackalloc bool[8];
        bool end = BiphaseMark.Encode([false, true, true, false], false, half);
        // zero: boundary transition only; one: boundary + mid-cell transition
        Assert.Equal(new[] { true, true, false, true, false, true, false, false }, half.ToArray());
        Assert.False(end);
        Assert.Equal(new[] { false, true, true, false }, BiphaseMark.Decode(half));
    }

    [Fact]
    public void EvenZeroCodewordsKeepSyncPolarity()
    {
        // With polarity correction, every codeword starts with the same level (§9.2.3).
        var frame = new LtcFrame(Timecode.Parse("10:00:00:00", LtcFrameRate.Fps25)) { UserBits = new UserBits(0xDEADBEEF) };
        Span<bool> half = stackalloc bool[160];
        bool level = false;
        for (int i = 0; i < 50; i++)
        {
            level = BiphaseMark.Encode(frame.ToCodeword(), level, half);
            Assert.True(half[0]);
            frame = frame.Next();
        }
    }

    public static TheoryData<LtcFrameRate, int> RateAndSampleRate() => new()
    {
        { LtcFrameRate.Fps23_98, 48_000 },
        { LtcFrameRate.Fps24, 44_100 },
        { LtcFrameRate.Fps25, 48_000 },
        { LtcFrameRate.Fps29_97, 48_000 },
        { LtcFrameRate.Fps29_97Drop, 96_000 },
        { LtcFrameRate.Fps30, 22_050 },
        { LtcFrameRate.Fps50, 48_000 },
        { LtcFrameRate.Fps59_94Drop, 48_000 },
    };

    [Theory]
    [MemberData(nameof(RateAndSampleRate))]
    public void GenerateThenDecode(LtcFrameRate rate, int sampleRate)
    {
        var start = new LtcFrame(Timecode.Parse(rate.IsDropFrame() ? "00:59:59;20" : "00:59:59:20", rate))
        {
            UserBits = UserBits.FromText("TAPE"),
            BinaryGroupFlags = BinaryGroupFlags.EightBitCharacters,
        };
        const int count = 40;
        float[] audio = LtcGenerator.Render(start, count, sampleRate);

        var decoder = new LtcDecoder(sampleRate, rate);
        var frames = decoder.Process(audio);

        // The first codeword is spent locking the bit clock; the last has no closing edge.
        Assert.InRange(frames.Count, count - 2, count);
        var expected = start;
        while (expected.Timecode != frames[0].Timecode) expected = expected.Next();
        foreach (var f in frames)
        {
            Assert.Equal(expected, f.Frame);
            Assert.Equal(LtcDirection.Forward, f.Direction);
            Assert.InRange(f.Speed, 0.995, 1.005);
            expected = expected.Next();
        }
        Assert.All(frames.Skip(1), f => Assert.True(f.IsContinuous));

        // Bit 0 of codeword k starts k codeword periods after sample 0 (the §9.5 datum), within a sample.
        foreach (var f in frames)
        {
            int k = f.Timecode.TotalFrames - start.Timecode.TotalFrames;
            double ideal = k * sampleRate / rate.CodewordRate();
            Assert.InRange(f.StartSample - ideal, -1.0, 1.0);
        }
    }

    [Theory]
    [InlineData(LtcFrameRate.Fps24)]
    [InlineData(LtcFrameRate.Fps25)]
    [InlineData(LtcFrameRate.Fps29_97Drop)]
    [InlineData(LtcFrameRate.Fps30)]
    public void DetectsRateWithoutAHint(LtcFrameRate rate)
    {
        var start = new LtcFrame(Timecode.Parse(rate.IsDropFrame() ? "01:00:00;00" : "01:00:00:00", rate));
        float[] audio = LtcGenerator.Render(start, 3 * rate.FramesPerSecond(), 48_000);
        var decoder = new LtcDecoder(48_000);
        var frames = decoder.Process(audio);
        Assert.Equal(rate, decoder.DetectedRate);
        Assert.Equal(rate, frames[^1].Frame.Rate);
    }

    [Fact]
    public void ReversePlayback()
    {
        var gen = new LtcGenerator(new LtcFrame(Timecode.Parse("02:00:00:10", LtcFrameRate.Fps25)), 48_000) { Reverse = true };
        float[] audio = gen.Read((int)LtcGenerator.SamplesFor(20, LtcFrameRate.Fps25, 48_000));
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps25);
        Assert.True(frames.Count >= 18);
        Assert.All(frames, f => Assert.Equal(LtcDirection.Reverse, f.Direction));
        Assert.All(frames, f => Assert.True(f.Speed < 0));
        for (int i = 1; i < frames.Count; i++) Assert.Equal(frames[i - 1].Timecode.Previous(), frames[i].Timecode);
        Assert.All(frames.Skip(1), f => Assert.True(f.IsContinuous));
    }

    [Fact]
    public void TimeReversedAudioDecodesBackwards()
    {
        var start = new LtcFrame(Timecode.Parse("00:00:10:00", LtcFrameRate.Fps30));
        float[] audio = LtcGenerator.Render(start, 20, 48_000);
        Array.Reverse(audio);
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps30);
        Assert.True(frames.Count >= 17);
        Assert.All(frames, f => Assert.Equal(LtcDirection.Reverse, f.Direction));
        for (int i = 1; i < frames.Count; i++) Assert.Equal(frames[i - 1].Timecode.Previous(), frames[i].Timecode);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.8)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void Varispeed(double speed)
    {
        var gen = new LtcGenerator(new Timecode(0, 0, 0, 0, LtcFrameRate.Fps25), 48_000) { Speed = speed };
        float[] audio = gen.Read((int)(LtcGenerator.SamplesFor(20, LtcFrameRate.Fps25, 48_000) / speed));
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps25);
        Assert.True(frames.Count >= 17, $"{frames.Count} frames");
        Assert.All(frames, f => Assert.InRange(f.Speed, speed * 0.99, speed * 1.01));
    }

    [Fact]
    public void SurvivesNoiseDcOffsetInversionAndLowLevel()
    {
        var gen = new LtcGenerator(new Timecode(10, 0, 0, 0, LtcFrameRate.Fps24), 44_100) { Amplitude = 0.05f, Invert = true, RiseTime = TimeSpan.FromTicks(500) };
        float[] audio = gen.Read((int)LtcGenerator.SamplesFor(30, LtcFrameRate.Fps24, 44_100));
        var rnd = new Random(7);
        for (int i = 0; i < audio.Length; i++) audio[i] += 0.2f + (float)(rnd.NextDouble() - 0.5) * 0.02f;
        var frames = LtcDecoder.DecodeAll(audio, 44_100, LtcFrameRate.Fps24);
        Assert.True(frames.Count >= 27, $"{frames.Count} frames");
        Assert.All(frames.Skip(1), f => Assert.True(f.IsContinuous));
    }

    [Fact]
    public void RecoversAfterDropoutAndReportsJump()
    {
        var a = LtcGenerator.Render(new LtcFrame(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps25)), 10, 48_000);
        var b = LtcGenerator.Render(new LtcFrame(new Timecode(5, 0, 0, 0, LtcFrameRate.Fps25)), 10, 48_000);
        float[] audio = [.. a, .. new float[4800], .. b];
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps25);
        Assert.Contains(frames, f => f.Timecode.Hours == 1);
        var firstB = frames.First(f => f.Timecode.Hours == 5);
        Assert.False(firstB.IsContinuous);
    }

    [Fact]
    public void GeneratorStreamsSeamlesslyAcrossBufferSizes()
    {
        var one = LtcGenerator.Render(new LtcFrame(new Timecode(0, 0, 0, 0, LtcFrameRate.Fps29_97Drop)), 12, 48_000);
        var gen = new LtcGenerator(new Timecode(0, 0, 0, 0, LtcFrameRate.Fps29_97Drop), 48_000);
        var chunks = new List<float>();
        var rnd = new Random(3);
        while (chunks.Count < one.Length) chunks.AddRange(gen.Read(Math.Min(rnd.Next(1, 700), one.Length - chunks.Count)));
        Assert.Equal(one, chunks.ToArray());
    }

    [Fact]
    public void FrameStartedEventsAlignWithCodewords()
    {
        var gen = new LtcGenerator(new Timecode(0, 0, 0, 0, LtcFrameRate.Fps25), 48_000);
        var starts = new List<(Timecode, long)>();
        gen.FrameStarted += (f, s) => starts.Add((f.Timecode, s));
        gen.Read(48_000);
        Assert.Equal(25, starts.Count);
        for (int i = 0; i < 25; i++)
        {
            Assert.Equal(i, starts[i].Item1.Frames);
            Assert.Equal(i * 1920L, starts[i].Item2);
        }
    }

    [Fact]
    public void RiseTimeIsAboutFortyMicroseconds()
    {
        const int sr = 192_000;
        float[] audio = LtcGenerator.Render(new LtcFrame(Timecode.Zero(LtcFrameRate.Fps25)), 2, sr, 1f);
        // Find a rising edge away from the start and measure 10 %–90 %.
        int i = sr / 100;
        while (!(audio[i] < -0.95f && audio[i + 20] > 0.95f)) i++;
        int t10 = i; while (audio[t10] < -0.8f) t10++;
        int t90 = t10; while (audio[t90] < 0.8f) t90++;
        double us = (t90 - t10) * 1e6 / sr;
        Assert.InRange(us, 30, 50);
    }

    [Fact]
    public void WavRoundTrip()
    {
        float[] audio = LtcGenerator.Render(new LtcFrame(new Timecode(1, 2, 3, 4, LtcFrameRate.Fps25)), 10, 48_000);
        foreach (var fmt in Enum.GetValues<WavSampleFormat>())
        {
            using var ms = new MemoryStream();
            WavFile.Write(ms, audio, 48_000, fmt);
            ms.Position = 0;
            var wav = WavFile.Read(ms);
            Assert.Equal(48_000, wav.SampleRate);
            Assert.Equal(1, wav.ChannelCount);
            Assert.Equal(audio.Length, wav.SampleCount);
            Assert.Equal((int)fmt, wav.SourceBitsPerSample);
            var frames = LtcDecoder.DecodeAll(wav.Channels[0], 48_000);
            Assert.Equal(new Timecode(1, 2, 3, 12, LtcFrameRate.Fps25), frames[^1].Timecode);
        }
    }
}
