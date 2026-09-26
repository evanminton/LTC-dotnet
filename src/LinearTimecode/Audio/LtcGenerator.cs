using LinearTimecode.BinaryGroups;

namespace LinearTimecode.Audio;

/// <summary>
/// Generates an LTC audio signal: biphase-mark modulated codewords (ST 12-1 §9.3) at Fe = 80 × Ff bits per second
/// (§9.4), with shaped edges (§9.6.1 rise/fall time 40 µs ± 10 µs, 10 %–90 %).
/// </summary>
/// <remarks>
/// <para>
/// The generator is a pull source: call <see cref="Read(Span{float})"/> from an audio callback or in a loop. Bit timing is computed
/// from the absolute sample index, so it never drifts, including at 1/1.001 rates. Setting <see cref="Speed"/> gives
/// varispeed (the time address still advances by one per codeword).
/// </para>
/// <para>
/// The first sample is the first transition of bit 0 of <see cref="NextFrame"/> — the LTC timing reference datum (§9.5).
/// </para>
/// </remarks>
public sealed class LtcGenerator
{
    private const int HalfCellsPerCodeword = 160;

    private readonly bool[] _levels = new bool[HalfCellsPerCodeword];
    private readonly bool[] _nextLevels = new bool[HalfCellsPerCodeword];
    private bool _nextReady;
    private LtcFrame? _nextReadyFrame;
    private bool _prevLast;          // level of the last half-cell of the previous codeword
    private long _codewordIndex = -1; // index of the codeword whose levels are in _levels
    private LtcFrame _frame;          // frame to encode for the next codeword to be generated

    private long _anchorSample;
    private double _anchorPhase;      // in half-cells
    private double _speed = 1.0;
    private double _halfCellRate;     // half-cells per second at speed 1
    private LtcFrame? _currentFrame;

    /// <summary>Creates a generator that starts with <paramref name="start"/>.</summary>
    public LtcGenerator(LtcFrame start, int sampleRate = 48_000)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (sampleRate < 8_000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be at least 8 kHz.");
        SampleRate = sampleRate;
        _frame = start;
        _halfCellRate = HalfCellsPerCodeword * start.Rate.CodewordRate();
    }

    /// <summary>Creates a generator starting at <paramref name="start"/> with default flags.</summary>
    public LtcGenerator(Timecode start, int sampleRate = 48_000) : this(new LtcFrame(start), sampleRate) { }

    public int SampleRate { get; }

    /// <summary>Frame rate of the code (taken from the start frame).</summary>
    public LtcFrameRate Rate => _frame.Rate;

    /// <summary>Peak amplitude of the output, 0–1 (full scale). Default 0.5 (−6 dBFS, i.e. 1 V p-p on a +4 dBu-aligned interface is ≈ −12 dBFS; pick to suit).</summary>
    public float Amplitude { get; set; } = 0.5f;

    /// <summary>10 %–90 % rise/fall time. Default 40 µs (§9.6.1). <see cref="TimeSpan.Zero"/> gives hard edges.</summary>
    public TimeSpan RiseTime { get; set; } = TimeSpan.FromTicks(400); // 40 µs

    /// <summary>Invert the output polarity (biphase mark is polarity-insensitive; this only changes the waveform's sign).</summary>
    public bool Invert { get; set; }

    /// <summary>
    /// Play backwards: each codeword is sent bit 79 first and the address counts down — what a reader sees when a tape is
    /// played in reverse.
    /// Changing it mid-stream takes effect from the next codeword, which continues from the one being output.
    /// </summary>
    public bool Reverse
    {
        get => _reverse;
        set
        {
            if (value == _reverse) return;
            _reverse = value;
            if (_currentFrame is not null)
            {
                var tc = _currentFrame.Timecode;
                _frame = _frame with { Timecode = value ? tc.Previous() : tc.Next() };
            }
            _nextReady = false;
        }
    }
    private bool _reverse;

    /// <summary>
    /// When the frame carries an ST 309 date (BGF 100/110), step the date at the 23:59:59:xx → 00:00:00:00 rollover
    /// (ST 309 §5.4). Default true.
    /// </summary>
    public bool AdvanceDateAtMidnight { get; set; } = true;

    /// <summary>
    /// Optional per-codeword hook: receives the codeword index (0 = first) and the frame about to be encoded, and returns
    /// the frame to send — e.g. to multiplex ST 262 page/line data or change user bits frame by frame. The time address
    /// sequence itself is not affected.
    /// </summary>
    public Func<long, LtcFrame, LtcFrame>? FrameHook { get; set; }

    /// <summary>Play speed (1 = nominal). Changes the bit rate from the current sample on.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), "Speed must be positive.");
            _anchorPhase = PhaseAt(SamplePosition);
            _anchorSample = SamplePosition;
            _speed = value;
        }
    }

    /// <summary>Samples produced so far.</summary>
    public long SamplePosition { get; private set; }

    /// <summary>
    /// The frame that will be encoded into the next codeword generated. Set it to relocate, or to change user bits/flags on the fly.
    /// Its rate must match <see cref="Rate"/>.
    /// </summary>
    public LtcFrame NextFrame
    {
        get => _frame;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Rate.Base() != _frame.Rate.Base() || value.Rate.CodewordRate() != _frame.Rate.CodewordRate())
                throw new ArgumentException("The frame rate cannot change while generating; create a new generator.", nameof(value));
            _frame = value;
            _nextReady = false;
        }
    }

    /// <summary>The frame currently being output (null before the first sample).</summary>
    public LtcFrame? CurrentFrame => _currentFrame;

    /// <summary>Raised when a codeword starts, with the frame and the sample index of its first transition.</summary>
    public event Action<LtcFrame, long>? FrameStarted;

    private double PhaseAt(long sample) => _anchorPhase + (sample - _anchorSample) * _halfCellRate * _speed / SampleRate;

    /// <summary>Fills <paramref name="buffer"/> with the signal.</summary>
    public void Read(Span<float> buffer)
    {
        double hcRate = _halfCellRate * _speed;
        // Full width of the raised-cosine edge: its 10–90 % span is 0.5903 of the width.
        double edgeHalfWidth = RiseTime.TotalSeconds / 0.5903 / 2 * _halfCellRate * _speed; // in half-cells
        edgeHalfWidth = Math.Min(edgeHalfWidth, 0.45);
        float amp = Invert ? -Amplitude : Amplitude;

        int i = 0;
        try
        {
            for (; i < buffer.Length; i++)
            {
                long n = SamplePosition + i;
                double p = _anchorPhase + (n - _anchorSample) * hcRate / SampleRate;
                long h = (long)Math.Floor(p);
                EnsureCodeword(Math.DivRem(h, HalfCellsPerCodeword, out _), n);

                bool level = LevelAt(h);
                double value = level ? 1 : -1;

                if (edgeHalfWidth > 0)
                {
                    long b = (long)Math.Round(p);
                    double dist = p - b;
                    if (Math.Abs(dist) < edgeHalfWidth)
                    {
                        bool before = LevelAt(b - 1), after = LevelAt(b);
                        if (before != after)
                        {
                            double x = (dist / edgeHalfWidth + 1) / 2; // 0..1 across the edge
                            double shape = 0.5 - 0.5 * Math.Cos(Math.PI * x);
                            double from = before ? 1 : -1, to = after ? 1 : -1;
                            value = from + (to - from) * shape;
                        }
                    }
                }
                buffer[i] = (float)(value * amp);
            }
        }
        finally
        {
            // Keep the position in step with the codewords already consumed, even if a FrameHook throws.
            SamplePosition += i;
        }
    }

    /// <summary>Reads <paramref name="count"/> samples into a new array.</summary>
    public float[] Read(int count)
    {
        var a = new float[count];
        Read(a);
        return a;
    }

    /// <summary>Number of samples that <paramref name="frames"/> codewords occupy at speed 1 (rounded up).</summary>
    public static long SamplesFor(int frames, LtcFrameRate rate, int sampleRate) =>
        (long)Math.Ceiling((double)frames * sampleRate * rate.RateDenominator() / rate.CodewordRateNumerator() - 1e-9);

    /// <summary>Renders <paramref name="frameCount"/> consecutive codewords starting at <paramref name="start"/>.</summary>
    public static float[] Render(LtcFrame start, int frameCount, int sampleRate = 48_000, float amplitude = 0.5f)
    {
        var g = new LtcGenerator(start, sampleRate) { Amplitude = amplitude };
        return g.Read(checked((int)SamplesFor(frameCount, start.Rate, sampleRate)));
    }

    // Level of global half-cell h. Only h within the current, previous (last half-cell) or next codeword is supported.
    private bool LevelAt(long h)
    {
        long cw = h >= 0 ? h / HalfCellsPerCodeword : -1;
        int idx = (int)(h - cw * HalfCellsPerCodeword);
        if (cw == _codewordIndex) return _levels[idx];
        if (cw < _codewordIndex) return _prevLast;
        PrepareNext();
        return _nextLevels[idx];
    }

    private void EnsureCodeword(long cw, long sample)
    {
        if (cw < 0) cw = 0;
        while (_codewordIndex < cw)
        {
            if (_codewordIndex >= 0) _prevLast = _levels[HalfCellsPerCodeword - 1];
            else _prevLast = false; // first codeword: start from low so bit 0 begins with a rising edge
            PrepareNext();
            Array.Copy(_nextLevels, _levels, HalfCellsPerCodeword);
            _currentFrame = _nextReadyFrame;
            _nextReady = false;
            _codewordIndex++;
            var previous = _frame;
            _frame = Reverse ? _frame with { Timecode = _frame.Timecode.Previous() } : _frame.Next();
            if (AdvanceDateAtMidnight && (Reverse ? previous.Timecode.TotalFrames == 0 : _frame.Timecode.TotalFrames == 0))
                _frame = _frame.RollDate(Reverse ? -1 : 1);
            if (_currentFrame is not null) FrameStarted?.Invoke(_currentFrame, sample);
        }
    }

    private void PrepareNext()
    {
        if (_nextReady) return;
        bool prev = _codewordIndex >= 0 && _levels[HalfCellsPerCodeword - 1];
        var frame = FrameHook?.Invoke(_codewordIndex + 1, _frame) ?? _frame;
        BiphaseMark.Encode(frame.ToCodeword(), prev, _nextLevels, Reverse);
        _nextReadyFrame = frame;
        _nextReady = true;
    }
}
