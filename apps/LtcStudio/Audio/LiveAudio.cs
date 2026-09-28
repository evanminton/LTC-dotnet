using System.Runtime.InteropServices;
using LinearTimecode;
using LinearTimecode.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LtcStudio.Audio;

/// <summary>
/// Reads LTC live from a WASAPI capture endpoint (or loopback of an output) with <see cref="LtcDecoder"/>.
/// Audio arrives on NAudio's capture thread; events are raised there, so UI code must marshal.
/// </summary>
public sealed class LiveReader : IDisposable
{
    private readonly object _lock = new();
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private LtcDecoder? _decoder;
    private WaveFormat? _format;
    private int _channel;
    private float[] _mono = [];
    private float _peak;
    private long _lastFrameTick = -1;
    private LtcFrameRate? _rate;
    private double _minimumLevel = 0.01;

    /// <summary>Raised on the audio thread for every decoded codeword.</summary>
    public event Action<LtcDecodedFrame>? FrameDecoded;

    /// <summary>Raised when capture stops; the argument is the error message, or null for a normal stop.</summary>
    public event Action<string?>? Stopped;

    public bool IsRunning => _capture is not null;

    /// <summary>Format being captured, e.g. "48000 Hz, 32-bit float, 2 ch".</summary>
    public string? FormatDescription { get; private set; }

    public int SampleRate => _format?.SampleRate ?? 0;

    /// <summary>Fixed frame rate for the decoder, or null to auto-detect. Applies immediately.</summary>
    public LtcFrameRate? Rate
    {
        get => _rate;
        set { lock (_lock) { _rate = value; if (_decoder is not null) _decoder.Rate = value; } }
    }

    /// <summary>Minimum peak level (0–1) treated as signal. Applies immediately.</summary>
    public double MinimumLevel
    {
        get => _minimumLevel;
        set { lock (_lock) { _minimumLevel = value; if (_decoder is not null) _decoder.MinimumLevel = value; } }
    }

    /// <summary>0-based input channel. Applies immediately.</summary>
    public int Channel
    {
        get => _channel;
        set { lock (_lock) { _channel = Math.Clamp(value, 0, Math.Max(0, (_format?.Channels ?? 1) - 1)); } }
    }

    /// <summary>Returns the peak level (0–1) since the last call, and resets it.</summary>
    public float TakePeak()
    {
        lock (_lock) { float p = _peak; _peak = 0; return p; }
    }

    /// <summary>Milliseconds since the last decoded codeword, or null if none yet.</summary>
    public long? MillisecondsSinceLastFrame
    {
        get { long t = Interlocked.Read(ref _lastFrameTick); return t < 0 ? null : Environment.TickCount64 - t; }
    }

    /// <summary>Measured codewords per second (smoothed), or 0.</summary>
    public double MeasuredCodewordRate { get { lock (_lock) return _decoder?.MeasuredCodewordRate ?? 0; } }

    /// <summary>Frame rate detected by the decoder.</summary>
    public LtcFrameRate? DetectedRate { get { lock (_lock) return _decoder?.DetectedRate; } }

    /// <summary>Opens the endpoint and starts decoding. Call from a background thread (WASAPI setup blocks briefly).</summary>
    public void Start(AudioEndpoint endpoint, int channel)
    {
        Stop();
        var device = AudioDevices.Open(endpoint.Id);
        WasapiCapture capture;
        try
        {
            capture = endpoint.IsLoopback
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);
        }
        catch
        {
            device.Dispose();
            throw;
        }
        if (!SampleConverter.IsFloat(capture.WaveFormat) && !SampleConverter.IsPcm(capture.WaveFormat))
        {
            string encoding = capture.WaveFormat.Encoding.ToString();
            capture.Dispose();
            device.Dispose();
            throw new NotSupportedException($"Unsupported capture format {encoding}.");
        }

        lock (_lock)
        {
            _device = device;
            _capture = capture;
            _format = capture.WaveFormat;
            _channel = Math.Clamp(channel, 0, _format.Channels - 1);
            _decoder = new LtcDecoder(_format.SampleRate, _rate) { MinimumLevel = _minimumLevel };
            _decoder.FrameDecoded += OnFrame;
            _peak = 0;
            Interlocked.Exchange(ref _lastFrameTick, -1);
            FormatDescription = SampleConverter.Describe(_format);
        }

        capture.DataAvailable += OnData;
        capture.RecordingStopped += OnStopped;
        try
        {
            capture.StartRecording();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>Stops capture and releases the device.</summary>
    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_lock)
        {
            capture = _capture; device = _device;
            _capture = null; _device = null;
            if (_decoder is not null) _decoder.FrameDecoded -= OnFrame;
            _decoder = null;
        }
        if (capture is null) return;
        capture.DataAvailable -= OnData;
        capture.RecordingStopped -= OnStopped;
        try { capture.StopRecording(); } catch { /* already stopped */ }
        capture.Dispose();
        device?.Dispose();
        Stopped?.Invoke(null);
    }

    /// <summary>Clears the decoder state (rate detection, continuity).</summary>
    public void ResetDecoder()
    {
        lock (_lock) _decoder?.Reset();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_decoder is null || _format is null) return;
            int frames = e.BytesRecorded / _format.BlockAlign;
            if (frames <= 0) return;
            if (_mono.Length < frames) _mono = new float[frames];
            float peak = SampleConverter.Extract(e.Buffer.AsSpan(0, e.BytesRecorded), _format, _channel, _mono, frames);
            if (peak > _peak) _peak = peak;
            _decoder.Process(_mono.AsSpan(0, frames));
        }
    }

    private void OnFrame(LtcDecodedFrame frame)
    {
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
        FrameDecoded?.Invoke(frame);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return; // our own Stop() raises Stopped
        lock (_lock)
        {
            _capture?.Dispose(); _device?.Dispose();
            _capture = null; _device = null; _decoder = null;
        }
        Stopped?.Invoke(e.Exception.Message);
    }

    public void Dispose() => Stop();
}

/// <summary>Which output channels carry the code.</summary>
/// <param name="Channel">0-based channel, or −1 for every channel.</param>
public sealed record OutputChannel(int Channel, string Name)
{
    public override string ToString() => Name;

    public static IReadOnlyList<OutputChannel> For(int channels)
    {
        var list = new List<OutputChannel> { new(-1, "All channels") };
        for (int c = 0; c < channels; c++)
            list.Add(new(c, c switch { 0 => "Channel 1 (left)", 1 => "Channel 2 (right)", _ => $"Channel {c + 1}" }));
        return list;
    }
}

/// <summary>
/// Plays an <see cref="LtcGenerator"/> to a WASAPI render endpoint in shared mode, at the endpoint's own mix rate so
/// Windows does not resample the code. All generator access goes through <see cref="Use"/> (the audio thread pulls
/// samples under the same lock).
/// </summary>
public sealed class LiveGenerator : IDisposable
{
    private readonly object _lock = new();
    private MMDevice? _device;
    private WasapiOut? _out;
    private LtcGenerator? _generator;
    private int _channel = -1;

    /// <summary>Raised when playback stops; the argument is the error message, or null for a normal stop.</summary>
    public event Action<string?>? Stopped;

    public bool IsRunning => _out is not null;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    /// <summary>Output latency requested from WASAPI.</summary>
    public TimeSpan Latency { get; private set; }

    public string? FormatDescription { get; private set; }

    /// <summary>0-based output channel, or −1 for all. Applies immediately.</summary>
    public int Channel
    {
        get => _channel;
        set { lock (_lock) _channel = value; }
    }

    /// <summary>Runs <paramref name="action"/> on the generator under the audio lock (no-op when stopped).</summary>
    public void Use(Action<LtcGenerator> action)
    {
        lock (_lock) { if (_generator is not null) action(_generator); }
    }

    /// <summary>Reads a value from the generator under the audio lock.</summary>
    public T? Get<T>(Func<LtcGenerator, T> read)
    {
        lock (_lock) return _generator is null ? default : read(_generator);
    }

    /// <summary>
    /// Opens the endpoint and starts playing. <paramref name="create"/> receives the endpoint's sample rate and the
    /// output latency, and returns the generator to play. Call from a background thread.
    /// </summary>
    public void Start(AudioEndpoint endpoint, int channel, int latencyMs, Func<int, TimeSpan, LtcGenerator> create)
    {
        Stop();
        var device = AudioDevices.Open(endpoint.Id);
        WasapiOut? output = null;
        try
        {
            WaveFormat mix;
            using (var client = device.AudioClient) mix = client.MixFormat;
            // Match the mix format exactly (normally 32-bit float) so shared mode needs no resampler.
            var format = SampleConverter.IsFloat(mix) && mix.BitsPerSample == 32
                ? mix
                : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);

            var latency = TimeSpan.FromMilliseconds(latencyMs);
            var generator = create(format.SampleRate, latency);
            lock (_lock)
            {
                _generator = generator;
                _channel = channel;
                SampleRate = format.SampleRate;
                Channels = format.Channels;
                Latency = latency;
                FormatDescription = SampleConverter.Describe(format);
            }

            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latencyMs);
            output.Init(new Provider(this, format));
            output.PlaybackStopped += OnStopped;
            lock (_lock) { _device = device; _out = output; }
            output.Play();
        }
        catch
        {
            output?.Dispose();
            device.Dispose();
            lock (_lock) { _generator = null; _out = null; _device = null; }
            throw;
        }
    }

    /// <summary>Stops playback and releases the device.</summary>
    public void Stop()
    {
        WasapiOut? output;
        MMDevice? device;
        lock (_lock)
        {
            output = _out; device = _device;
            _out = null; _device = null; _generator = null;
        }
        if (output is null) return;
        output.PlaybackStopped -= OnStopped;
        try { output.Stop(); } catch { /* already stopped */ }
        output.Dispose();
        device?.Dispose();
        Stopped?.Invoke(null);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null) return;
        lock (_lock)
        {
            _out?.Dispose(); _device?.Dispose();
            _out = null; _device = null; _generator = null;
        }
        Stopped?.Invoke(e.Exception.Message);
    }

    public void Dispose() => Stop();

    // Pulls mono LTC from the generator and writes it to the selected channel(s) as interleaved 32-bit float.
    private sealed class Provider(LiveGenerator owner, WaveFormat format) : IWaveProvider
    {
        private float[] _mono = [];

        public WaveFormat WaveFormat { get; } = format;

        public int Read(byte[] buffer, int offset, int count)
        {
            var dest = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            int channels = WaveFormat.Channels;
            int frames = dest.Length / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            var mono = _mono.AsSpan(0, frames);

            int channel;
            lock (owner._lock)
            {
                channel = owner._channel;
                if (owner._generator is null) mono.Clear();
                else owner._generator.Read(mono);
            }

            for (int i = 0; i < frames; i++)
            {
                float v = mono[i];
                for (int c = 0; c < channels; c++)
                    dest[i * channels + c] = channel < 0 || c == channel ? v : 0f;
            }
            return frames * channels * sizeof(float);
        }
    }
}
