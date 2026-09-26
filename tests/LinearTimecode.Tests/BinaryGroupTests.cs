using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LinearTimecode.Tests;

public class St309Tests
{
    [Theory]
    [InlineData(0x00, 0.0)]
    [InlineData(0x05, -5.0)]
    [InlineData(0x08, -8.0)]
    [InlineData(0x12, -12.0)]
    [InlineData(0x13, 13.0)]
    [InlineData(0x14, 12.0)]
    [InlineData(0x17, 9.0)]
    [InlineData(0x25, 1.0)]
    [InlineData(0x0A, -0.5)]
    [InlineData(0x0D, -3.5)]
    [InlineData(0x1D, -9.5)]
    [InlineData(0x2A, 11.5)]
    [InlineData(0x2C, 9.5)]
    [InlineData(0x3A, 5.5)]
    [InlineData(0x3F, 0.5)]
    [InlineData(0x32, 12.75)]
    public void TimeZoneTable2Offsets(int code, double hours)
    {
        var z = new TimeZoneCode(code);
        Assert.Equal(TimeZoneCodeKind.Offset, z.Kind);
        Assert.Equal(TimeSpan.FromHours(hours), z.Offset);
        Assert.Equal(z, TimeZoneCode.FromOffset(TimeSpan.FromHours(hours)));
    }

    [Theory]
    [InlineData(0x26, TimeZoneCodeKind.Reserved)]
    [InlineData(0x27, TimeZoneCodeKind.Reserved)]
    [InlineData(0x28, TimeZoneCodeKind.Deprecated)]
    [InlineData(0x31, TimeZoneCodeKind.Deprecated)]
    [InlineData(0x33, TimeZoneCodeKind.Reserved)]
    [InlineData(0x37, TimeZoneCodeKind.Reserved)]
    [InlineData(0x38, TimeZoneCodeKind.UserDefined)]
    [InlineData(0x39, TimeZoneCodeKind.Unknown)]
    public void NonOffsetCodes(int code, TimeZoneCodeKind kind)
    {
        var z = new TimeZoneCode(code);
        Assert.Equal(kind, z.Kind);
        Assert.Null(z.Offset);
    }

    [Fact]
    public void EveryOffsetHasOneCode()
    {
        var offsets = TimeZoneCode.Offsets.Select(z => z.Offset!.Value).ToList();
        Assert.Equal(offsets.Count, offsets.Distinct().Count());
        Assert.Equal(TimeSpan.FromHours(-12), offsets[0]);
        Assert.Equal(TimeSpan.FromHours(13), offsets[^1]);
        Assert.Equal(new TimeZoneCode(0x08), TimeZoneCode.Parse("UTC-8"));
        Assert.Equal(new TimeZoneCode(0x3A), TimeZoneCode.Parse("+05:30"));
        Assert.Equal(new TimeZoneCode(0x3A), TimeZoneCode.Parse("3A"));
        Assert.Contains("Los Angeles", new TimeZoneCode(0x08).Description);
    }

    [Fact]
    public void YymmddLayoutMatchesTables1And4()
    {
        var d = new DateTimeZone(new DateOnly(2026, 9, 26), new TimeZoneCode(0x07), DateFormat.Yymmdd, daylightSaving: true);
        var ub = d.ToUserBits();
        Assert.Equal(new[] { 6, 2, 9, 0, 6, 2, 7, 0b0100 }, ub.ToGroups()); // DD=26 MM=09 YY=26, TZ 07, DST
        Assert.Equal(d, DateTimeZone.FromUserBits(ub));
        Assert.Equal("47260926", ub.ToString());
    }

    [Fact]
    public void MjdLayoutMatchesTable5()
    {
        Assert.Equal(49718, DateTimeZone.ToMjd(new DateOnly(1995, 1, 1))); // ST 309 Annex C example
        var d = new DateTimeZone(new DateOnly(1995, 1, 1), new TimeZoneCode(0x3A), DateFormat.ModifiedJulianDate);
        var ub = d.ToUserBits();
        Assert.Equal(new[] { 8, 1, 7, 9, 4, 0, 0xA, 0b1011 }, ub.ToGroups()); // MJD 049718 units first; TZ 3A; MJD flag
        Assert.Equal(d, DateTimeZone.FromUserBits(ub));
    }

    [Fact]
    public void TimeAddressIsLocalForYymmddAndUtcForMjd()
    {
        var tc = new Timecode(13, 0, 0, 0, LtcFrameRate.Fps25);
        var local = new DateTimeZone(new DateOnly(2026, 9, 26), new TimeZoneCode(0x07), DateFormat.Yymmdd, true);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 13, 0, 0, TimeSpan.FromHours(-7)), local.ToDateTimeOffset(tc));

        var utc = local with { Format = DateFormat.ModifiedJulianDate };
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 13, 0, 0, TimeSpan.Zero), utc.ToDateTimeOffset(tc));
        Assert.Equal(TimeSpan.FromHours(-7), utc.ToDateTimeOffset(tc)!.Value.Offset);
    }

    [Fact]
    public void InvalidDigitsAreRejected()
    {
        Assert.False(DateTimeZone.TryFromUserBits(UserBits.Parse("00991331"), out _)); // month 13
        Assert.False(DateTimeZone.TryFromUserBits(UserBits.Parse("000000A0"), out _)); // non-BCD
        var frame = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps25)) { UserBits = UserBits.Parse("00991331"), BinaryGroupFlags = BinaryGroupFlags.DateTimeZone };
        Assert.Contains(frame.Validate(), s => s.Contains("ST 309", StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratorAdvancesDateAtMidnight()
    {
        var date = new DateTimeZone(new DateOnly(2026, 12, 31), new TimeZoneCode(0x25));
        var start = new LtcFrame(Timecode.Parse("23:59:59:20", LtcFrameRate.Fps25)).WithDateTimeZone(date, clockTime: true);
        float[] audio = LtcGenerator.Render(start, 12, 48_000);
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps25);

        Assert.All(frames, f => Assert.Equal(BinaryGroupFlags.ClockTimeDateTimeZone, f.Frame.BinaryGroupFlags));
        foreach (var f in frames)
        {
            var d = f.Frame.GetDateTimeZone()!;
            Assert.Equal(f.Timecode.Hours == 0 ? new DateOnly(2027, 1, 1) : new DateOnly(2026, 12, 31), d.Date);
        }
        Assert.Contains(frames, f => f.Timecode.Hours == 0);
    }

    [Fact]
    public void DescriberExplainsDate()
    {
        var frame = new LtcFrame(new Timecode(10, 0, 0, 0, LtcFrameRate.Fps29_97)).WithDateTimeZone(
            new DateTimeZone(new DateOnly(2026, 9, 26), new TimeZoneCode(0x07), DateFormat.Yymmdd, true));
        string text = LtcDescriber.Explain(frame);
        Assert.Contains("2026-09-26", text);
        Assert.Contains("daylight saving", text);
        Assert.Contains("Los Angeles", text);
        Assert.Contains("-07:00", text);
        Assert.StartsWith("Date 2026-09-26", UserBitsDescriber.Summary(frame.UserBits, frame.BinaryGroupFlags));
    }
}

public class St262Tests
{
    [Theory]
    [InlineData(0, 0, DirectoryCategory.AuxiliaryTimeAddress)]
    [InlineData(1, 9, DirectoryCategory.AuxiliaryTimeAddress)]
    [InlineData(2, 3, DirectoryCategory.AuxiliaryTimeAddress)]
    [InlineData(0, 10, DirectoryCategory.MediaAddress)]
    [InlineData(2, 4, DirectoryCategory.MediaAddress)]
    [InlineData(2, 15, DirectoryCategory.MediaAddress)]
    [InlineData(3, 0, DirectoryCategory.Application)]
    [InlineData(14, 15, DirectoryCategory.Application)]
    [InlineData(15, 0, DirectoryCategory.Control)]
    public void DirectoryCategories(int page, int line, DirectoryCategory category) =>
        Assert.Equal(category, new DirectoryIndex(page, line).Category);

    [Fact]
    public void ApplicationPagesHold192Lines()
    {
        int n = 0;
        for (int p = 0; p < 16; p++)
            for (int l = 0; l < 16; l++)
                if (new DirectoryIndex(p, l).Category == DirectoryCategory.Application) n++;
        Assert.Equal(192, n);
        Assert.Equal(24, Enumerable.Range(0, 256).Count(b => DirectoryIndex.FromByte((byte)b).Category == DirectoryCategory.AuxiliaryTimeAddress));
    }

    [Fact]
    public void DirectoryIndexLivesInGroups7And8()
    {
        var f = new PageLineFrame(new DirectoryIndex(15, 3), 0x11, 0x22, 0x33);
        var ub = f.ToUserBits();
        Assert.Equal(3, ub[7]);   // line
        Assert.Equal(15, ub[8]);  // page
        Assert.Equal(0x1, ub[1]); // byte 1 low nibble in BG1
        Assert.Equal(0x1, ub[2]);
        Assert.Equal(0x3, ub[5]);
        Assert.Equal(f, PageLineFrame.FromUserBits(ub));
    }

    [Fact]
    public void ChecksumIsTwosComplementOfSum()
    {
        Assert.Equal(0x00, PageLineFrame.Checksum(0x00, 0x00));
        Assert.Equal(0xFF, PageLineFrame.Checksum(0x01));
        Assert.Equal(0x9A, PageLineFrame.Checksum(0x12, 0x34, 0x20)); // 0x66 → 0x9A
        var cc = PageLineFrame.ControlCode(3, 0x12, 0x34);
        Assert.Equal(new DirectoryIndex(15, 3), cc.Index);
        Assert.True(cc.HasValidChecksum);
        Assert.Equal(0, (cc.Byte1 + cc.Byte2 + cc.Byte3 + cc.Byte4) % 256);
        Assert.False((cc with { Byte2 = 0x13 }).HasValidChecksum);
    }

    [Fact]
    public void AuxiliaryTimeAddress()
    {
        var tc = new Timecode(21, 45, 12, 7, LtcFrameRate.Fps25);
        var f = PageLineFrame.ForAuxiliaryTimeAddress(tc);
        Assert.Equal(new DirectoryIndex(2, 1), f.Index);
        Assert.Equal(0x07, f.Byte1);
        Assert.Equal(0x12, f.Byte2);
        Assert.Equal(0x45, f.Byte3);
        Assert.Equal(tc, f.GetAuxiliaryTimeAddress(LtcFrameRate.Fps25)!.Timecode);
    }

    [Fact]
    public void PriorityHigherPageThenLine()
    {
        Assert.True(new DirectoryIndex(15, 0).CompareTo(new DirectoryIndex(14, 15)) > 0);
        Assert.True(new DirectoryIndex(3, 5).CompareTo(new DirectoryIndex(3, 4)) > 0);
    }

    [Fact]
    public void MessageStringRoundTrip()
    {
        var layout = PageLineMessageLayout.Default;
        var frames = layout.EncodeText("HELLO WORLD", messageId: 7);
        Assert.Equal(1 + 4 + 1, frames.Count); // 11 bytes → 4 message frames, null filled
        Assert.True(frames[0].HasValidChecksum);
        Assert.True(frames[^1].HasValidChecksum);
        Assert.Equal(0, frames[^2].Byte3); // null fill
        var messages = layout.Decode([new PageLineFrame(new DirectoryIndex(15, 0), 1, 2, 3), .. frames, .. frames]);
        Assert.Equal(2, messages.Count);
        Assert.Equal("HELLO WORLD", messages[0].Text);
        Assert.Equal(7, messages[0].MessageId);
        Assert.True(messages[0].ChecksumsValid);
        Assert.Throws<ArgumentException>(() => layout.Encode(new byte[255 * 3]));
    }

    [Fact]
    public void MessageThroughAudio()
    {
        var layout = PageLineMessageLayout.Default;
        var msg = layout.EncodeText("LTC!", 1);
        var gen = new LtcGenerator(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps30), 48_000)
        {
            FrameHook = (i, f) => f.WithPageLine(msg[(int)(i % msg.Count)]),
        };
        float[] audio = gen.Read((int)LtcGenerator.SamplesFor(12, LtcFrameRate.Fps30, 48_000));
        var decoded = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps30);
        var pl = decoded.Select(d => d.Frame.GetPageLine()).OfType<PageLineFrame>();
        var messages = layout.Decode(pl);
        Assert.NotEmpty(messages);
        Assert.All(messages, m => Assert.Equal("LTC!", m.Text));
        Assert.All(decoded, d => Assert.Equal(BinaryGroupFlags.PageLine, d.Frame.BinaryGroupFlags));
    }

    [Fact]
    public void OptionsCatalogCoversNewStandards()
    {
        string text = LtcOptions.ToText();
        Assert.Contains("ST 309", text);
        Assert.Contains("Chatham Island", text);
        Assert.Contains("Page/Line", text);
        Assert.Equal(64, LtcOptions.TimeZones.Options.Count);
        Assert.Contains("control 12 34", UserBitsDescriber.Summary(PageLineFrame.ControlCode(0, 0x12, 0x34).ToUserBits(), BinaryGroupFlags.PageLine));
    }
}

public class Rp169Tests
{
    [Fact]
    public void BitAssignmentMatchesTable1()
    {
        // 12:34:56;28 drop-frame with color frame flag.
        var aux = new AuxiliaryTimeAddress(Timecode.Parse("12:34:56;28", LtcFrameRate.Fps29_97), colorFrame: true);
        var ub = aux.ToUserBits();
        Assert.Equal(8, ub[1]);                 // frames units
        Assert.Equal(2 | 4 | 8, ub[2]);         // frames tens 2, DF (bit 2), CF (bit 3)
        Assert.Equal(6, ub[3]);                 // seconds units
        Assert.Equal(5, ub[4]);                 // seconds tens, bit 3 unassigned = 0
        Assert.Equal(4, ub[5]);                 // minutes units
        Assert.Equal(3, ub[6]);                 // minutes tens
        Assert.Equal(2, ub[7]);                 // hours units = directory line
        Assert.Equal(1, ub[8]);                 // hours tens = directory page
        Assert.Equal(new DirectoryIndex(1, 2), aux.Index);

        // Table 1 LTC bit numbers: BG2 bit 2 = LTC bit 14 (DF), BG2 bit 3 = LTC bit 15 (CF).
        var cw = new LtcFrame(Timecode.Zero(LtcFrameRate.Fps30)).WithPageLine(aux.ToPageLineFrame()).ToCodeword();
        Assert.True(cw[14]);
        Assert.True(cw[15]);
        Assert.True(cw[60]); // hours tens LSB
        Assert.False(cw[62]);
        Assert.False(cw[63]);
    }

    [Fact]
    public void RoundTripSelectsDropFrameFromTheFlag()
    {
        var df = new AuxiliaryTimeAddress(Timecode.Parse("00:10:00;00", LtcFrameRate.Fps29_97));
        var back = AuxiliaryTimeAddress.FromUserBits(df.ToUserBits(), LtcFrameRate.Fps29_97)!;
        Assert.Equal(LtcFrameRate.Fps29_97Drop, back.Timecode.Rate);
        Assert.Equal(df, back);

        var ndf = new AuxiliaryTimeAddress(new Timecode(23, 59, 59, 24, LtcFrameRate.Fps25));
        Assert.Equal(ndf, AuxiliaryTimeAddress.FromUserBits(ndf.ToUserBits(), LtcFrameRate.Fps25));
    }

    [Fact]
    public void RejectsNonAuxiliaryIndexesAndFlagsUnassignedBits()
    {
        Assert.Null(AuxiliaryTimeAddress.FromUserBits(new PageLineFrame(new DirectoryIndex(3, 0), 0, 0, 0).ToUserBits(), LtcFrameRate.Fps25));
        Assert.Null(AuxiliaryTimeAddress.FromUserBits(new PageLineFrame(new DirectoryIndex(2, 4), 0, 0, 0).ToUserBits(), LtcFrameRate.Fps25));
        var withSpare = new AuxiliaryTimeAddress(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps25)).ToUserBits().WithGroup(4, 8);
        var decoded = AuxiliaryTimeAddress.FromUserBits(withSpare, LtcFrameRate.Fps25)!;
        Assert.Equal(1, decoded.UnassignedBits);
        Assert.NotEmpty(decoded.Validate());
    }

    [Fact]
    public void RunningAuxiliaryAddressThroughAudio()
    {
        var aux = new AuxiliaryTimeAddress(Timecode.Parse("09:59:59;28", LtcFrameRate.Fps29_97));
        var gen = new LtcGenerator(new Timecode(1, 0, 0, 0, LtcFrameRate.Fps29_97Drop), 48_000) { FrameHook = AuxiliaryTimeAddress.RunningHook(aux) };
        float[] audio = gen.Read((int)LtcGenerator.SamplesFor(8, LtcFrameRate.Fps29_97Drop, 48_000));
        var frames = LtcDecoder.DecodeAll(audio, 48_000, LtcFrameRate.Fps29_97Drop);
        Assert.NotEmpty(frames);
        foreach (var f in frames)
        {
            Assert.Equal(BinaryGroupFlags.PageLine, f.Frame.BinaryGroupFlags);
            var a = f.Frame.GetPageLine()!.Value.GetAuxiliaryTimeAddress(LtcFrameRate.Fps29_97)!;
            long k = f.Timecode.TotalFrames - new Timecode(1, 0, 0, 0, LtcFrameRate.Fps29_97Drop).TotalFrames;
            Assert.Equal(aux.Timecode.AddFrames(k), a.Timecode); // crosses 10:00:00;00 with DF counting
        }
        Assert.Contains("Aux time", UserBitsDescriber.Summary(frames[0].Frame.UserBits, frames[0].Frame.BinaryGroupFlags, LtcFrameRate.Fps29_97));
    }

    [Fact]
    public void DescriberAndCatalog()
    {
        var aux = new AuxiliaryTimeAddress(Timecode.Parse("12:00:00;02", LtcFrameRate.Fps29_97), colorFrame: true);
        string text = UserBitsDescriber.Explain(aux.ToUserBits(), BinaryGroupFlags.PageLine, LtcFrameRate.Fps29_97);
        Assert.Contains("12:00:00;02", text);
        Assert.Contains("drop-frame", text);
        Assert.Contains("color frame ID", text);
        Assert.NotNull(LtcOptions.Find("rp169"));
    }
}
