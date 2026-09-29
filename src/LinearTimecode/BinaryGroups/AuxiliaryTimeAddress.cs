namespace LinearTimecode.BinaryGroups;

/// <summary>
/// A second time address carried in the binary groups, per SMPTE RP 169-1995 (the auxiliary time address dialect of
/// the ST 262 page/line system). Signalled by binary group flags 101 (<see cref="BinaryGroupFlags.PageLine"/>).
/// </summary>
/// <remarks>
/// <para>RP 169 Table 1 mirrors the primary time address, shifted into the binary groups:</para>
/// <list type="table">
/// <item><term>BG 1</term><description>frames units</description></item>
/// <item><term>BG 2</term><description>bits 0–1 frames tens, bit 2 drop frame flag, bit 3 color frame flag</description></item>
/// <item><term>BG 3</term><description>seconds units</description></item>
/// <item><term>BG 4</term><description>bits 0–2 seconds tens, bit 3 unassigned (0)</description></item>
/// <item><term>BG 5</term><description>minutes units</description></item>
/// <item><term>BG 6</term><description>bits 0–2 minutes tens, bit 3 unassigned (0)</description></item>
/// <item><term>BG 7</term><description>hours units = ST 262 directory line</description></item>
/// <item><term>BG 8</term><description>bits 0–1 hours tens = directory page, bits 2–3 zero</description></item>
/// </list>
/// <para>
/// The drop frame and color frame flags refer only to the auxiliary address (RP 169 §3.2–3.3). Unassigned bits are 0 (§3.4).
/// </para>
/// </remarks>
public sealed record AuxiliaryTimeAddress
{
    public AuxiliaryTimeAddress(Timecode timecode, bool colorFrame = false)
    {
        Timecode = timecode;
        ColorFrame = colorFrame;
    }

    /// <summary>The auxiliary time address. Its rate's drop-frame mode sets the drop frame flag.</summary>
    public Timecode Timecode { get; init; }

    /// <summary>Color frame flag (§3.3): color frame identification has been applied to the auxiliary address.</summary>
    public bool ColorFrame { get; init; }

    /// <summary>Drop frame flag (§3.2): set by a drop-frame rate, or as received (<see cref="StrayDropFrameFlag"/>).</summary>
    public bool DropFrame => Timecode.Rate.IsDropFrame() || StrayDropFrameFlag;

    /// <summary>
    /// Set when decoding found the drop frame flag set at a rate without drop-frame counting (24/25/30 frames). It is
    /// written back by <see cref="ToUserBits"/> so the bits round-trip, and reported by <see cref="Validate"/>.
    /// </summary>
    public bool StrayDropFrameFlag { get; init; }

    /// <summary>
    /// Unassigned bits as received (bit 0 = BG 4 bit 3, bit 1 = BG 6 bit 3); should be 0. BG 8 bits 2–3 are not
    /// included: setting them would make the directory page 4 or more, which is not an auxiliary time address.
    /// </summary>
    public int UnassignedBits { get; init; }

    /// <summary>The ST 262 directory index this address lives at (page = hours tens, line = hours units).</summary>
    public DirectoryIndex Index => DirectoryIndex.ForAuxiliaryHours(Timecode.Hours);

    /// <summary>Encodes into the 32 user bits (use with BGF 101).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A field doesn't fit its digits: frames tens above 3 would spill into the flags, seconds or minutes tens above 7
    /// into the unassigned bits, and hours outside 00–23 aren't an auxiliary time address directory index.
    /// </exception>
    public UserBits ToUserBits()
    {
        var tc = Timecode;
        if (tc.Frames is < 0 or > 39 || tc.Seconds is < 0 or > 79 || tc.Minutes is < 0 or > 79 || tc.Hours is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(Timecode), $"Auxiliary time address {tc} can't be written in the RP 169 layout.");
        int bg2 = (tc.Frames / 10) | (DropFrame ? 4 : 0) | (ColorFrame ? 8 : 0);
        return UserBits.FromGroups(
        [
            tc.Frames % 10, bg2,
            tc.Seconds % 10, tc.Seconds / 10 | ((UnassignedBits & 1) << 3),
            tc.Minutes % 10, tc.Minutes / 10 | ((UnassignedBits & 2) << 2),
            tc.Hours % 10, tc.Hours / 10,
        ]);
    }

    /// <summary>As an ST 262 page/line frame.</summary>
    public PageLineFrame ToPageLineFrame() => PageLineFrame.FromUserBits(ToUserBits());

    /// <summary>The next auxiliary address (same flags), wrapping at 24 hours.</summary>
    public AuxiliaryTimeAddress Next() => this with { Timecode = Timecode.Next() };

    /// <summary>Advances by <paramref name="frames"/> addresses.</summary>
    public AuxiliaryTimeAddress AddFrames(long frames) => this with { Timecode = Timecode.AddFrames(frames) };

    /// <summary>
    /// Decodes user bits. <paramref name="rate"/> gives the frame count (24/25/30); a set drop frame flag selects the
    /// drop-frame variant. Fails when the page is above 2, a digit is not BCD, or the address is out of range.
    /// </summary>
    public static bool TryFromUserBits(UserBits bits, LtcFrameRate rate, out AuxiliaryTimeAddress? value, out string? error)
    {
        value = null;
        error = null;
        int page = bits[8], line = bits[7];
        if (new DirectoryIndex(page, line).Category != DirectoryCategory.AuxiliaryTimeAddress)
        {
            error = $"Directory index {page}.{line} is not an auxiliary time address (hours 00–23 = 0.0–2.3).";
            return false;
        }
        if (bits[1] > 9 || bits[3] > 9 || bits[5] > 9)
        {
            error = "A units digit of the auxiliary time address is not BCD.";
            return false;
        }

        bool df = (bits[2] & 4) != 0, cf = (bits[2] & 8) != 0;
        int unassigned = ((bits[4] >> 3) & 1) | (((bits[6] >> 3) & 1) << 1);
        var r = rate.WithDropFrame(df);
        var tc = Timecode.CreateUnchecked(page * 10 + line, (bits[6] & 7) * 10 + bits[5], (bits[4] & 7) * 10 + bits[3], (bits[2] & 3) * 10 + bits[1], r);
        if (tc.Validate() is { } e) { error = $"Auxiliary time address: {e}"; return false; }
        value = new AuxiliaryTimeAddress(tc, cf) { UnassignedBits = unassigned, StrayDropFrameFlag = df && !r.IsDropFrame() };
        return true;
    }

    /// <summary>Decodes user bits or returns null.</summary>
    public static AuxiliaryTimeAddress? FromUserBits(UserBits bits, LtcFrameRate rate) => TryFromUserBits(bits, rate, out var v, out _) ? v : null;

    /// <summary>Problems against RP 169.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (Timecode.Validate() is { } e) issues.Add(e);
        if ((UnassignedBits & ~3) != 0) issues.Add("Only unassigned bits 0–1 (BG 4 bit 3, BG 6 bit 3) exist; the others are ignored.");
        if ((UnassignedBits & 3) != 0) issues.Add("RP 169 unassigned bits are set; they shall be 0 (§3.4).");
        if (StrayDropFrameFlag) issues.Add($"Drop frame flag is set, but {Timecode.Rate.DisplayName()} has no drop-frame counting.");
        return issues;
    }

    /// <summary>
    /// A generator hook that writes a running auxiliary time address into every codeword, starting at <paramref name="start"/>
    /// and advancing one address per codeword (use with <see cref="Audio.LtcGenerator.FrameHook"/>).
    /// </summary>
    public static Func<long, LtcFrame, LtcFrame> RunningHook(AuxiliaryTimeAddress start, bool clockTime = false) =>
        (index, frame) => frame.WithPageLine(start.AddFrames(index).ToPageLineFrame(), clockTime);

    public override string ToString() => $"{Timecode}{(ColorFrame ? " CF" : "")}";
}
