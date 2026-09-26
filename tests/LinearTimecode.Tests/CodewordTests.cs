using LinearTimecode;
using LinearTimecode.Describe;

namespace LinearTimecode.Tests;

public class CodewordTests
{
    [Fact]
    public void KnownCodeword30()
    {
        var cw = new LtcFrame(new Timecode(1, 37, 52, 16, LtcFrameRate.Fps30)).ToCodeword();
        // 51 zeros in bits 0–63 besides bit 27 → polarity correction bit 27 = 1 (byte 3 = 0x0D).
        Assert.Equal("06 01 02 0D 07 03 01 00 FC BF", cw.ToHex());
        Assert.True(cw.HasEvenZeroCount);
    }

    [Fact]
    public void KnownCodeword25UsesSwappedFlags()
    {
        var frame = new LtcFrame(new Timecode(23, 59, 59, 24, LtcFrameRate.Fps25)) { UserBits = new UserBits(0x12345678) };
        var cw = frame.ToCodeword();
        Assert.Equal("84 72 69 55 49 35 23 1A FC BF", cw.ToHex());
        Assert.True(cw[59]); // polarity correction lives at bit 59 in 25-frame systems
        Assert.Equal(frame, LtcFrame.FromCodeword(cw, LtcFrameRate.Fps25));
    }

    [Fact]
    public void SyncWordMatchesTable5()
    {
        var bits = new LtcCodeword(0).ToBits();
        int[] expected = [0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1];
        for (int i = 0; i < 16; i++) Assert.Equal(expected[i] == 1, bits[64 + i]);
        Assert.NotEqual(bits[64], bits[79]); // complementary: direction indicator
    }

    [Theory]
    [InlineData(LtcFrameRate.Fps24, 10, 11, 27, 43, 58, 59)]
    [InlineData(LtcFrameRate.Fps25, 10, 11, 59, 27, 58, 43)]
    [InlineData(LtcFrameRate.Fps29_97Drop, 10, 11, 27, 43, 58, 59)]
    public void FlagPositionsMatchTable3(LtcFrameRate rate, int df, int cf, int pc, int bgf0, int bgf1, int bgf2)
    {
        var b = rate.Base();
        Assert.Equal(b == TimecodeBase.Base30 ? df : -1, LtcBits.DropFrameFlag(b));
        Assert.Equal(b == TimecodeBase.Base24 ? -1 : cf, LtcBits.ColorFrameFlag(b));
        Assert.Equal(pc, LtcBits.PolarityCorrection(b));
        Assert.Equal(bgf0, LtcBits.Bgf0(b));
        Assert.Equal(bgf1, LtcBits.Bgf1(b));
        Assert.Equal(bgf2, LtcBits.Bgf2(b));
    }

    [Fact]
    public void BinaryGroupPositionsMatchTable4()
    {
        var ub = UserBits.FromGroups([1, 2, 3, 4, 5, 6, 7, 8]);
        var cw = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps25)) { UserBits = ub, PolarityCorrection = false }.ToCodeword();
        for (int g = 1; g <= 8; g++) Assert.Equal(g, cw.GetBits(LtcBits.BinaryGroup(g), 4));
        Assert.Equal(new[] { 4, 12, 20, 28, 36, 44, 52, 60 }, Enumerable.Range(1, 8).Select(LtcBits.BinaryGroup));
        Assert.Equal(ub, cw.UserBits);
    }

    [Fact]
    public void DropFrameFlagSetsRate()
    {
        var f = new LtcFrame(Timecode.Parse("00:10:00;00", LtcFrameRate.Fps29_97));
        var cw = f.ToCodeword();
        Assert.True(cw[10]);
        Assert.Equal(LtcFrameRate.Fps29_97Drop, LtcFrame.FromCodeword(cw, LtcFrameRate.Fps29_97).Rate);
        Assert.Equal(LtcFrameRate.Fps29_97, LtcFrame.FromCodeword(cw.WithBit(10, false), LtcFrameRate.Fps29_97Drop).Rate);
    }

    [Fact]
    public void PolarityCorrectionAlwaysGivesEvenZeros()
    {
        var rnd = new Random(12);
        foreach (var rate in LtcFrameRateExtensions.All)
        {
            for (int i = 0; i < 200; i++)
            {
                var tc = Timecode.FromTotalFrames(rnd.Next(rate.AddressesPerDay()), rate);
                var f = new LtcFrame(tc) { UserBits = new UserBits((uint)rnd.NextInt64(0, 1L << 32)), BinaryGroupFlags = (BinaryGroupFlags)rnd.Next(8), ColorFrame = rate.Base() != TimecodeBase.Base24 && rnd.Next(2) == 1 };
                var cw = f.ToCodeword();
                Assert.True(cw.HasEvenZeroCount);
                Assert.Equal(f, LtcFrame.FromCodeword(cw, rate));
            }
        }
    }

    [Fact]
    public void BytesAndBitsRoundTrip()
    {
        var cw = new LtcFrame(new Timecode(12, 34, 56, 7, LtcFrameRate.Fps30)) { UserBits = UserBits.FromText("LTC!"), BinaryGroupFlags = BinaryGroupFlags.EightBitCharacters }.ToCodeword();
        Assert.Equal(cw, LtcCodeword.FromBytes(cw.ToBytes()));
        Assert.Equal(cw, LtcCodeword.FromBits(cw.ToBits()));
        Assert.Equal(cw, LtcCodeword.Parse(cw.ToHex()));
        Assert.Equal(cw, LtcCodeword.Parse(cw.ToBitString()));
        Assert.Throws<FormatException>(() => LtcCodeword.Parse("00 00 00 00 00 00 00 00 00 00"));
    }

    [Fact]
    public void UserBitsText()
    {
        var ub = UserBits.FromText("ABCD");
        // First character in groups 7/8 (low nibble in 7): 'A' = 0x41 → group 7 = 1, group 8 = 4.
        Assert.Equal(1, ub[7]);
        Assert.Equal(4, ub[8]);
        Assert.Equal(0x4, ub[2]); // 'D' = 0x44 in groups 1/2
        Assert.Equal("41424344", ub.ToString());
        Assert.Equal("ABCD", ub.ToText());
        Assert.Equal(ub, UserBits.Parse("41 42 43 44"));
        Assert.True(UserBits.Parse("20260926").IsBcd);
    }

    [Fact]
    public void ValidationFindsProblems()
    {
        var bad = new LtcCodeword(0).WithBits(0, 4, 12); // frame units 12
        var f = LtcFrame.FromCodeword(bad, LtcFrameRate.Fps25);
        Assert.Contains(f.Validate(), s => s.Contains("BCD", StringComparison.Ordinal));

        var reserved = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps24)) { BinaryGroupFlags = BinaryGroupFlags.Reserved };
        Assert.Contains(reserved.Validate(), s => s.Contains("reserved", StringComparison.Ordinal));

        var legacy = LtcFrame.FromCodeword(new LtcCodeword(0).WithBit(10, true), LtcFrameRate.Fps25);
        Assert.Equal(1, legacy.UnassignedFlagBits);
        Assert.NotEmpty(legacy.Validate());
    }

    [Fact]
    public void PalColorFramingLogicalMatchesArithmetic()
    {
        for (int s = 0; s < 60; s++)
            for (int f = 0; f < 25; f++)
            {
                var tc = new Timecode(0, 0, s, f, LtcFrameRate.Fps25);
                var pair = ColorFraming.Pal(tc);
                bool fields1to4 = pair is PalFieldPair.Fields1And2 or PalFieldPair.Fields3And4;
                Assert.Equal(fields1to4, ColorFraming.PalLogical(tc));
            }
    }

    [Fact]
    public void BinaryGroupFlagTable()
    {
        Assert.True(BinaryGroupFlags.ClockTimeDateTimeZone.IsClockTime());
        Assert.False(BinaryGroupFlags.DateTimeZone.IsClockTime());
        Assert.Equal("101", BinaryGroupFlags.PageLine.BitPattern());
        Assert.Equal(8, BinaryGroupFlagsExtensions.All.Count);
    }

    [Fact]
    public void DescriberExplainsEverything()
    {
        string text = LtcDescriber.Explain("06 01 02 0D 07 03 01 00 FC BF");
        Assert.Contains("01:37:52:16", text);
        Assert.Contains("Polarity", text);
        Assert.Contains("even", text);

        Assert.Contains("drop-frame counting", LtcDescriber.Explain("00:10:00;00", LtcFrameRate.Fps29_97));
        Assert.Contains("does not exist", LtcDescriber.Explain("00:01:00;00", LtcFrameRate.Fps29_97Drop));

        string all = LtcOptions.ToText();
        foreach (var g in LtcOptions.All) Assert.Contains(g.Title, all);
        foreach (var r in LtcFrameRateExtensions.All) Assert.Contains(r.Description(), all);
        Assert.Equal(80, LtcDescriber.BitTable(new LtcCodeword(0), TimecodeBase.Base30).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
