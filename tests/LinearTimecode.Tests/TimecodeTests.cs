using LinearTimecode;

namespace LinearTimecode.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData("00:00:59;29", "00:01:00;02")]
    [InlineData("00:09:59;29", "00:10:00;00")]
    [InlineData("00:10:00;00", "00:10:00;01")]
    [InlineData("00:10:00;01", "00:10:00;02")]
    [InlineData("01:00:59;29", "01:01:00;02")]
    [InlineData("23:59:59;29", "00:00:00;00")]
    public void DropFrameNext(string from, string expected)
    {
        var tc = Timecode.Parse(from, LtcFrameRate.Fps29_97);
        Assert.Equal(LtcFrameRate.Fps29_97Drop, tc.Rate);
        Assert.Equal(expected, tc.Next().ToString());
    }

    [Fact]
    public void DropFrameSkippedAddressesAreInvalid()
    {
        Assert.False(Timecode.TryParse("00:01:00;00", LtcFrameRate.Fps29_97Drop, out _));
        Assert.False(Timecode.TryParse("00:01:00;01", LtcFrameRate.Fps29_97Drop, out _));
        Assert.True(Timecode.TryParse("00:10:00;00", LtcFrameRate.Fps29_97Drop, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timecode(0, 2, 0, 1, LtcFrameRate.Fps59_94Drop));
    }

    [Theory]
    [InlineData(LtcFrameRate.Fps24, 2_073_600)]
    [InlineData(LtcFrameRate.Fps25, 2_160_000)]
    [InlineData(LtcFrameRate.Fps29_97, 2_592_000)]
    [InlineData(LtcFrameRate.Fps29_97Drop, 2_589_408)]
    [InlineData(LtcFrameRate.Fps50, 2_160_000)]
    [InlineData(LtcFrameRate.Fps59_94Drop, 2_589_408)]
    public void AddressesPerDay(LtcFrameRate rate, int perDay)
    {
        Assert.Equal(perDay, rate.AddressesPerDay());
        var last = Timecode.FromTotalFrames(perDay - 1, rate);
        Assert.Equal(23, last.Hours);
        Assert.Equal(59, last.Minutes);
        Assert.Equal(59, last.Seconds);
        Assert.Equal(rate.FramesPerSecond() - 1, last.Frames);
        Assert.Equal(0, last.Next().TotalFrames);
    }

    [Theory]
    [InlineData(LtcFrameRate.Fps29_97Drop)]
    [InlineData(LtcFrameRate.Fps25)]
    [InlineData(LtcFrameRate.Fps23_98)]
    public void TotalFramesRoundTripsAcrossTheDay(LtcFrameRate rate)
    {
        for (int n = 0; n < rate.AddressesPerDay(); n += 997)
        {
            var tc = Timecode.FromTotalFrames(n, rate);
            Assert.True(tc.IsValid, tc.ToString());
            Assert.Equal(n, tc.TotalFrames);
            Assert.Equal(tc, Timecode.Parse(tc.ToString(), rate));
        }
    }

    [Fact]
    public void DropFrameTracksRealTime()
    {
        // One hour of 29.97 DF labels = 3600 s − 3.6 ms of real time (§5.2.2 note).
        var hour = new Timecode(1, 0, 0, 0, LtcFrameRate.Fps29_97Drop);
        Assert.Equal(107_892, hour.TotalFrames);
        Assert.InRange((hour.ToTimeSpan() - TimeSpan.FromHours(1)).TotalMilliseconds, -3.7, -3.5);

        // Non-drop 29.97 runs 3.6 s slow per hour.
        var ndf = new Timecode(1, 0, 0, 0, LtcFrameRate.Fps29_97);
        Assert.InRange(ndf.ToTimeSpan().TotalSeconds - 3600, 3.59, 3.61);
    }

    [Fact]
    public void ParseForms()
    {
        Assert.Equal(new Timecode(0, 0, 10, 5, LtcFrameRate.Fps25), Timecode.Parse("10:05", LtcFrameRate.Fps25));
        Assert.Equal(LtcFrameRate.Fps59_94Drop, Timecode.Parse("01:00:00.00", LtcFrameRate.Fps59_94).Rate);
        Assert.Equal(LtcFrameRate.Fps25, Timecode.Parse("01:00:00;00", LtcFrameRate.Fps25).Rate); // no DF at 25
        Assert.Throws<FormatException>(() => Timecode.Parse("00:00:00:25", LtcFrameRate.Fps25));
        Assert.Throws<FormatException>(() => Timecode.Parse("24:00:00:00"));
    }

    [Fact]
    public void ArithmeticWraps()
    {
        var tc = new Timecode(0, 0, 0, 0, LtcFrameRate.Fps24);
        Assert.Equal("23:59:59:23", (tc - 1).ToString());
        Assert.Equal("00:00:01:00", (tc + 24).ToString());
        Assert.True(tc < tc + 1);
    }

    [Fact]
    public void FramePairsCountPairs()
    {
        var tc = new Timecode(1, 23, 45, 13, LtcFrameRate.Fps50);
        Assert.Equal(25, LtcFrameRate.Fps50.FramesPerSecond());
        Assert.Equal(2 * (long)tc.TotalFrames, tc.VideoFrameIndex);
        Assert.Equal(25.0, LtcFrameRate.Fps50.CodewordRate());
        Assert.Equal(50.0, LtcFrameRate.Fps50.VideoFrameRate());
    }

    [Fact]
    public void ConvertThroughRealTime()
    {
        var tc = new Timecode(0, 0, 10, 0, LtcFrameRate.Fps25);
        Assert.Equal("00:00:10:00", tc.ConvertTo(LtcFrameRate.Fps30).ToString());
        Assert.Equal("00:00:10:00", tc.ConvertTo(LtcFrameRate.Fps29_97).ToString()); // 299.7 addresses rounds to 300
    }

    [Theory]
    [InlineData("23.976", LtcFrameRate.Fps23_98)]
    [InlineData("23.98", LtcFrameRate.Fps23_98)]
    [InlineData("29.97df", LtcFrameRate.Fps29_97Drop)]
    [InlineData("29.97 NDF", LtcFrameRate.Fps29_97)]
    [InlineData("59.94DF", LtcFrameRate.Fps59_94Drop)]
    [InlineData("50fps", LtcFrameRate.Fps50)]
    [InlineData("Fps24", LtcFrameRate.Fps24)]
    public void ParseRates(string text, LtcFrameRate expected) => Assert.Equal(expected, LtcFrameRateExtensions.Parse(text));

    [Fact]
    public void RateTokensRoundTrip()
    {
        foreach (var r in LtcFrameRateExtensions.All) Assert.Equal(r, LtcFrameRateExtensions.Parse(r.Token()));
        Assert.False(LtcFrameRateExtensions.TryParse("25df", out _));
        Assert.False(LtcFrameRateExtensions.TryParse("3", out _));
    }

    [Fact]
    public void TimeOfDay()
    {
        var tc = Timecode.FromTimeOfDay(new TimeSpan(0, 13, 1, 0, 20), LtcFrameRate.Fps29_97Drop);
        Assert.Equal("13:01:00;02", tc.ToString());
        Assert.True(tc.IsValid);
    }
}
