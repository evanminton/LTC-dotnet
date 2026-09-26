namespace LinearTimecode.Audio;

/// <summary>Direction the code was read in.</summary>
public enum LtcDirection
{
    /// <summary>Bit 0 first; sync word at the end of the codeword.</summary>
    Forward,
    /// <summary>Bit 79 first (reverse play); detected from the mirrored sync word (§9.2.5).</summary>
    Reverse,
}

/// <summary>One codeword recovered from audio.</summary>
/// <param name="Frame">Decoded content.</param>
/// <param name="Codeword">Raw codeword.</param>
/// <param name="StartSample">Sample position (fractional) of the codeword's first transition. For forward code this is bit 0 — the §9.5 timing reference.</param>
/// <param name="EndSample">Sample position of the transition that ends the codeword.</param>
/// <param name="Direction">Read direction.</param>
/// <param name="CodewordsPerSecond">Measured codeword rate.</param>
/// <param name="Speed">Measured rate ÷ nominal rate of <see cref="LtcFrame.Rate"/> (negative when reversed).</param>
/// <param name="IsContinuous">True when the address follows the previous decoded one (+1 forward, −1 reverse).</param>
public sealed record LtcDecodedFrame(
    LtcFrame Frame,
    LtcCodeword Codeword,
    double StartSample,
    double EndSample,
    LtcDirection Direction,
    double CodewordsPerSecond,
    double Speed,
    bool IsContinuous)
{
    public Timecode Timecode => Frame.Timecode;

    /// <summary>Spec problems found in the frame (see <see cref="LtcFrame.Validate"/>).</summary>
    public IReadOnlyList<string> Issues => Frame.Validate();

    public override string ToString() =>
        FormattableString.Invariant($"{Frame}  @{StartSample,12:0.0}  {(Direction == LtcDirection.Reverse ? "REV" : "FWD")} x{Math.Abs(Speed):0.000}{(IsContinuous ? "" : "  (jump)")}");
}

/// <summary>
/// Streaming LTC reader: recovers codewords from audio samples at any sample rate, in either direction and over a wide
/// speed range (§9.3 biphase mark is self-clocking and polarity-insensitive).
/// </summary>
/// <remarks>
/// <para>
/// Pipeline: adaptive-threshold zero-crossing detection with hysteresis → interval classification against a tracked bit
/// period (long = 0, two shorts = 1) → 80-bit shift register → sync-word match in either direction (Table 5).
/// </para>
/// <para>
/// The flag layout depends on the frame count (24/25/30). Pass <c>rate</c> when you know it; otherwise the decoder
/// infers it from the measured codeword rate, the highest frame number seen and the drop-frame flag
/// (<see cref="DetectedRate"/>). Frame-pair rates (48/50/60) can't be told from 24/25/30 by the code alone.
/// </para>
/// </remarks>
public sealed class LtcDecoder
{
    private const double Alpha = 0.15; // bit-period tracking gain

    // edge detector
    private double _envMax, _envMin;
    private bool _high;
    private bool _haveSample;
    private double _prevSample;
    private double _lastRise = double.NaN, _lastFall = double.NaN;

    // bit clock
    private double _lastEdge = double.NaN;
    private double _period; // samples per bit cell
    private bool _halfPending;
    private double _halfStart;

    // shift register (newest bit at position 79)
    private UInt128 _reg;
    private int _validBits;
    private readonly double[] _bitStart = new double[80];
    private long _bitCount;

    // rate detection
    private double _avgCodewordRate;
    private int _maxFrameSeen = -1;
    private LtcDecodedFrame? _last;

    /// <summary>Creates a decoder.</summary>
    /// <param name="sampleRate">Input sample rate in Hz.</param>
    /// <param name="rate">Frame rate, if known. For 29.97/59.94 the drop-frame flag still selects DF/NDF.</param>
    public LtcDecoder(int sampleRate, LtcFrameRate? rate = null)
    {
        if (sampleRate < 8_000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be at least 8 kHz.");
        SampleRate = sampleRate;
        Rate = rate;
    }

    public int SampleRate { get; }

    /// <summary>Fixed frame rate, or null to detect it.</summary>
    public LtcFrameRate? Rate { get; set; }

    /// <summary>Minimum peak level (0–1 full scale) treated as signal; quieter input is ignored. Default 0.01 (−40 dBFS).</summary>
    public double MinimumLevel { get; set; } = 0.01;

    /// <summary>Samples consumed so far.</summary>
    public long SamplePosition { get; private set; }

    /// <summary>Best guess at the frame rate from what has been decoded (null until the first frame).</summary>
    public LtcFrameRate? DetectedRate { get; private set; }

    /// <summary>Smoothed measured codeword rate (codewords per second), or 0.</summary>
    public double MeasuredCodewordRate => _avgCodewordRate;

    /// <summary>The most recent decoded frame.</summary>
    public LtcDecodedFrame? LastFrame => _last;

    /// <summary>Raised for every decoded codeword.</summary>
    public event Action<LtcDecodedFrame>? FrameDecoded;

    /// <summary>Clears all state.</summary>
    public void Reset()
    {
        _envMax = _envMin = 0; _high = false; _haveSample = false; _prevSample = 0;
        _lastRise = _lastFall = double.NaN;
        _lastEdge = double.NaN; _period = 0; _halfPending = false;
        _reg = UInt128.Zero; _validBits = 0; _bitCount = 0;
        _avgCodewordRate = 0; _maxFrameSeen = -1; _last = null; DetectedRate = null;
        SamplePosition = 0;
    }

    /// <summary>Processes float samples (−1…1) and returns the frames completed within them.</summary>
    public IReadOnlyList<LtcDecodedFrame> Process(ReadOnlySpan<float> samples)
    {
        var output = new List<LtcDecodedFrame>();
        foreach (float s in samples) Step(s, output);
        return output;
    }

    /// <summary>Processes 16-bit PCM samples.</summary>
    public IReadOnlyList<LtcDecodedFrame> Process(ReadOnlySpan<short> samples)
    {
        var output = new List<LtcDecodedFrame>();
        foreach (short s in samples) Step(s / 32768.0, output);
        return output;
    }

    /// <summary>Decodes a whole buffer with a fresh decoder.</summary>
    public static IReadOnlyList<LtcDecodedFrame> DecodeAll(ReadOnlySpan<float> samples, int sampleRate, LtcFrameRate? rate = null) =>
        new LtcDecoder(sampleRate, rate).Process(samples);

    private void Step(double x, List<LtcDecodedFrame> output)
    {
        long n = SamplePosition++;

        // Envelope follower (decays over ~20 ms) gives an adaptive mid-level and hysteresis, removing DC offsets.
        double k = 1.0 / (0.02 * SampleRate);
        if (!_haveSample) { _envMax = _envMin = x; _prevSample = x; _haveSample = true; return; }
        _envMax = Math.Max(x, _envMax - (_envMax - _envMin) * k);
        _envMin = Math.Min(x, _envMin + (_envMax - _envMin) * k);
        double span = _envMax - _envMin;
        double mid = (_envMax + _envMin) / 2;

        // Remember the interpolated mid-level crossings.
        double a = _prevSample - mid, b = x - mid;
        if (a < 0 && b >= 0) _lastRise = n - 1 + a / (a - b);
        else if (a > 0 && b <= 0) _lastFall = n - 1 + a / (a - b);
        _prevSample = x;

        if (span < 2 * MinimumLevel) return;
        double hyst = span * 0.2;

        if (!_high && x > mid + hyst)
        {
            _high = true;
            OnEdge(double.IsNaN(_lastRise) || n - _lastRise > _period ? n : _lastRise, output);
        }
        else if (_high && x < mid - hyst)
        {
            _high = false;
            OnEdge(double.IsNaN(_lastFall) || n - _lastFall > _period ? n : _lastFall, output);
        }
    }

    private void OnEdge(double t, List<LtcDecodedFrame> output)
    {
        if (double.IsNaN(_lastEdge)) { _lastEdge = t; return; }
        double d = t - _lastEdge;
        _lastEdge = t;
        if (d <= 0) return;

        // Signal gap or first interval: restart the bit clock.
        if (_period <= 0 || d > 4 * _period)
        {
            _period = d;
            _halfPending = false;
            _validBits = 0;
            return;
        }

        if (d > 1.6 * _period)
        {
            // The tracked period was a half-cell; this is a full cell.
            _period = d;
            _halfPending = false;
            EmitBit(false, t - d, t, output);
            return;
        }

        if (d < 0.75 * _period)
        {
            if (!_halfPending)
            {
                _halfPending = true;
                _halfStart = t - d;
            }
            else
            {
                _halfPending = false;
                double full = t - _halfStart;
                _period += (full - _period) * Alpha;
                EmitBit(true, _halfStart, t, output);
            }
        }
        else
        {
            _halfPending = false; // a lone half-cell means we were out of phase; the long cell re-aligns us
            _period += (d - _period) * Alpha;
            EmitBit(false, t - d, t, output);
        }
    }

    private void EmitBit(bool bit, double start, double end, List<LtcDecodedFrame> output)
    {
        _reg = (_reg >> 1) | (bit ? UInt128.One << 79 : UInt128.Zero);
        _bitStart[_bitCount % 80] = start;
        _bitCount++;
        if (_validBits < 80) _validBits++;
        if (_validBits < 80) return;

        double first = _bitStart[_bitCount % 80]; // start of the oldest bit in the register

        if ((ushort)(_reg >> 64) == LtcBits.SyncWord)
        {
            Complete(new LtcCodeword((ulong)_reg), first, end, LtcDirection.Forward, output);
        }
        else if ((ushort)_reg == LtcBits.ReverseSyncWord)
        {
            ulong data = 0;
            for (int j = 0; j < 64; j++)
                if (((_reg >> (79 - j)) & UInt128.One) != UInt128.Zero) data |= 1UL << j;
            Complete(new LtcCodeword(data), first, end, LtcDirection.Reverse, output);
        }
    }

    private void Complete(LtcCodeword cw, double start, double end, LtcDirection dir, List<LtcDecodedFrame> output)
    {
        double duration = end - start;
        double cps = duration > 0 ? SampleRate / duration : 0;
        _avgCodewordRate = _avgCodewordRate <= 0 ? cps : _avgCodewordRate + (cps - _avgCodewordRate) * 0.1;
        if (cw.HasValidBcd && cw.Frames < 40) _maxFrameSeen = Math.Max(_maxFrameSeen, cw.Frames);

        LtcFrameRate rate = Rate ?? Detect(cw);
        var frame = LtcFrame.FromCodeword(cw, rate);
        rate = frame.Rate;
        DetectedRate = Rate is null ? rate : Rate.Value.WithDropFrame(rate.IsDropFrame());

        double speed = cps / rate.CodewordRate() * (dir == LtcDirection.Reverse ? -1 : 1);
        bool continuous = _last is not null && _last.Direction == dir &&
            _last.Timecode.Rate == frame.Timecode.Rate &&
            frame.Timecode.TotalFrames == _last.Timecode.AddFrames(dir == LtcDirection.Forward ? 1 : -1).TotalFrames;

        var decoded = new LtcDecodedFrame(frame, cw, start, end, dir, cps, speed, continuous);
        _last = decoded;
        output.Add(decoded);
        FrameDecoded?.Invoke(decoded);
    }

    // Guess the rate from the measured codeword rate, the frame numbers seen and the DF flag.
    private LtcFrameRate Detect(LtcCodeword cw)
    {
        double r = _avgCodewordRate;
        TimecodeBase b;
        if (_maxFrameSeen >= 25) b = TimecodeBase.Base30;
        else if (cw.DropFrameFlag(TimecodeBase.Base30) && _maxFrameSeen != 24 && r > 27) b = TimecodeBase.Base30;
        else if (r <= 0) b = TimecodeBase.Base30;
        else if (r < 24.5) b = _maxFrameSeen == 24 ? TimecodeBase.Base25 : TimecodeBase.Base24;
        else if (r < 27.5) b = TimecodeBase.Base25;
        else b = TimecodeBase.Base30;

        return b switch
        {
            TimecodeBase.Base24 => Math.Abs(r - 24000.0 / 1001) < Math.Abs(r - 24) ? LtcFrameRate.Fps23_98 : LtcFrameRate.Fps24,
            TimecodeBase.Base25 => LtcFrameRate.Fps25,
            _ => cw.DropFrameFlag(TimecodeBase.Base30) ? LtcFrameRate.Fps29_97Drop
                : Math.Abs(r - 30000.0 / 1001) < Math.Abs(r - 30) ? LtcFrameRate.Fps29_97 : LtcFrameRate.Fps30,
        };
    }
}
