using LinearTimecode.BinaryGroups;

namespace LinearTimecode;

/// <summary>
/// The content of one LTC codeword: time address, flags and binary groups (ST 12-1 §8, §9.2).
/// </summary>
/// <remarks>
/// Convert with <see cref="ToCodeword"/> and <see cref="FromCodeword"/>. The drop-frame flag is derived from
/// <see cref="Timecode"/>'s rate; the polarity-correction bit is computed on encode.
/// </remarks>
public sealed record LtcFrame
{
    public LtcFrame(Timecode timecode) => Timecode = timecode;

    /// <summary>The time address. Its rate selects the flag layout and the drop-frame flag.</summary>
    public Timecode Timecode { get; init; }

    /// <summary>Binary groups (user bits).</summary>
    public UserBits UserBits { get; init; }

    /// <summary>BGF2..BGF0.</summary>
    public BinaryGroupFlags BinaryGroupFlags { get; init; }

    /// <summary>Color frame flag (§8.3.2): color framing has been applied to the address (see <see cref="ColorFraming"/>). Not used by 24-frame systems.</summary>
    public bool ColorFrame { get; init; }

    /// <summary>
    /// Set the biphase-mark polarity correction bit so every codeword has an even number of zeros (§9.2.3). Default true.
    /// When false the bit is 0. After decoding this reflects whether the received codeword had an even zero count.
    /// </summary>
    public bool PolarityCorrection { get; init; } = true;

    /// <summary>
    /// Value of the unassigned flag bits (bit 10 in 24/25-frame systems, bit 11 in 24-frame systems) as received.
    /// Original sources shall set them to 0 (§9.2.2); some legacy devices do not.
    /// </summary>
    public int UnassignedFlagBits { get; init; }

    /// <summary>Set by <see cref="FromCodeword"/> when a received BCD units digit was above 9.</summary>
    public bool BcdError { get; init; }

    public LtcFrameRate Rate => Timecode.Rate;

    /// <summary>Encodes into an 80-bit codeword.</summary>
    public LtcCodeword ToCodeword()
    {
        var tc = Timecode;
        var b = tc.Rate.Base();
        var cw = new LtcCodeword(0)
            .WithBits(LtcBits.FrameUnits, 4, tc.Frames % 10)
            .WithBits(LtcBits.FrameTens, 2, tc.Frames / 10)
            .WithBits(LtcBits.SecondUnits, 4, tc.Seconds % 10)
            .WithBits(LtcBits.SecondTens, 3, tc.Seconds / 10)
            .WithBits(LtcBits.MinuteUnits, 4, tc.Minutes % 10)
            .WithBits(LtcBits.MinuteTens, 3, tc.Minutes / 10)
            .WithBits(LtcBits.HourUnits, 4, tc.Hours % 10)
            .WithBits(LtcBits.HourTens, 2, tc.Hours / 10);

        for (int g = 1; g <= 8; g++) cw = cw.WithBits(LtcBits.BinaryGroup(g), 4, this.UserBits[g]);

        if (LtcBits.DropFrameFlag(b) is var df and >= 0) cw = cw.WithBit(df, tc.Rate.IsDropFrame());
        if (LtcBits.ColorFrameFlag(b) is var cf and >= 0) cw = cw.WithBit(cf, ColorFrame);
        cw = cw.WithBit(LtcBits.Bgf0(b), this.BinaryGroupFlags.Bgf0())
               .WithBit(LtcBits.Bgf1(b), this.BinaryGroupFlags.Bgf1())
               .WithBit(LtcBits.Bgf2(b), this.BinaryGroupFlags.Bgf2());

        if (b != TimecodeBase.Base30) cw = cw.WithBit(10, (UnassignedFlagBits & 1) != 0);
        if (b == TimecodeBase.Base24) cw = cw.WithBit(11, (UnassignedFlagBits & 2) != 0);

        return PolarityCorrection ? cw.WithPolarityCorrection(b) : cw.WithBit(LtcBits.PolarityCorrection(b), false);
    }

    /// <summary>
    /// Decodes a codeword. <paramref name="rate"/> selects the flag layout; for 29.97/59.94 the drop-frame flag in the
    /// codeword decides between the DF and NDF variant. The resulting <see cref="Timecode"/> is not validated — check
    /// <see cref="Validate"/>.
    /// </summary>
    public static LtcFrame FromCodeword(LtcCodeword codeword, LtcFrameRate rate)
    {
        var b = rate.Base();
        if (b == TimecodeBase.Base30) rate = rate.WithDropFrame(codeword.DropFrameFlag(b));
        int unassigned = 0;
        if (b != TimecodeBase.Base30 && codeword[10]) unassigned |= 1;
        if (b == TimecodeBase.Base24 && codeword[11]) unassigned |= 2;
        return new LtcFrame(Timecode.CreateUnchecked(codeword.Hours, codeword.Minutes, codeword.Seconds, codeword.Frames, rate))
        {
            UserBits = codeword.UserBits,
            BinaryGroupFlags = codeword.GetBinaryGroupFlags(b),
            ColorFrame = codeword.ColorFrameFlag(b),
            PolarityCorrection = codeword.HasEvenZeroCount,
            UnassignedFlagBits = unassigned,
            BcdError = !codeword.HasValidBcd,
        };
    }

    /// <summary>Parses a codeword from hex bytes or a bit string (see <see cref="LtcCodeword.Parse"/>) and decodes it.</summary>
    public static LtcFrame Parse(string codeword, LtcFrameRate rate) => FromCodeword(LtcCodeword.Parse(codeword), rate);

    /// <summary>
    /// Problems with this frame against ST 12-1: invalid BCD, out-of-range fields, skipped drop-frame addresses,
    /// the reserved binary-group flag combination, a drop-frame flag at a rate without drop-frame, and set unassigned bits.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (BcdError) issues.Add("A BCD units digit is above 9.");
        if (Timecode.Validate() is { } e) issues.Add(e);
        if (this.BinaryGroupFlags.IsReserved()) issues.Add("Binary group flags 011 are reserved and shall not be used (§8.4.4).");
        if (this.BinaryGroupFlags.CarriesDateTimeZone())
        {
            if (DateTimeZone.TryFromUserBits(this.UserBits, out var dtz, out string? dateError)) issues.AddRange(dtz!.Validate());
            else issues.Add($"ST 309 date: {dateError}");
        }
        if (this.BinaryGroupFlags.CarriesPageLine() && PageLineFrame.FromUserBits(this.UserBits) is { Index.Category: DirectoryCategory.Control, HasValidChecksum: false })
            issues.Add("ST 262 control frame checksum error.");
        if (UnassignedFlagBits != 0) issues.Add("Unassigned flag bits are set; original sources shall set them to 0 (§9.2.2).");
        return issues;
    }

    /// <summary>Shorthand: this frame advanced by one address, with the same flags and user bits.</summary>
    public LtcFrame Next() => this with { Timecode = Timecode.Next() };

    public override string ToString()
    {
        string s = $"{Timecode} @ {Rate.DisplayName()}";
        if (this.UserBits.Value != 0) s += $" UB {this.UserBits.ToDisplayString()}";
        if (BinaryGroupFlags != BinaryGroupFlags.Unspecified) s += $" BGF {this.BinaryGroupFlags.BitPattern()}";
        if (ColorFrame) s += " CF";
        return s;
    }
}
