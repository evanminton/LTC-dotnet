using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;
using LtcStudio.Audio;

namespace LtcStudio.Pages;

/// <summary>Reads LTC live from a sound-card input (or the loopback of an output) and shows everything in it.</summary>
public class LiveReaderPage : ContentPage
{
    private const int SignalTimeoutMs = 250;
    private const int MaxLogLines = 300;

    private readonly LiveReader _reader = new();
    private IReadOnlyList<AudioEndpoint> _inputs = [];

    private readonly Picker _device = new();
    private readonly Picker _channel = new();
    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps25, withAuto: true);
    private readonly Slider _minLevel = new(-70, -10, -40);
    private readonly Label _minLevelText = new();
    private readonly Button _start;

    private readonly Label _tc = new()
    {
        Text = "--:--:--:--",
        FontFamily = Ui.Mono,
        FontSize = 88,
        FontAttributes = FontAttributes.Bold,
        HorizontalOptions = LayoutOptions.Center,
    };
    private readonly Label _state = new() { Text = "Stopped", FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center };
    private readonly ProgressBar _meter = new() { Progress = 0, WidthRequest = 320, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center };
    private readonly Label _meterText = new() { FontFamily = Ui.Mono, VerticalOptions = LayoutOptions.Center, Text = "   -∞ dBFS" };
    private readonly Label _details = Ui.MonoLabel();
    private readonly Label _explain = Ui.MonoLabel(12);
    private readonly Label _log = Ui.MonoLabel(12);
    private readonly Label _status = Ui.Caption("");

    private readonly ConcurrentQueue<string> _events = new();
    private readonly List<string> _logLines = [];
    private readonly IDispatcherTimer _timer;
    private LtcDecodedFrame? _latest;
    private long _frameCount, _jumpCount, _dropouts;
    private bool _hadSignal;
    private bool _loading;

    public LiveReaderPage()
    {
        Title = "Live Reader";
        _start = Ui.Button("Start", async (_, _) => await ToggleAsync());
        _start.MinimumWidthRequest = 110;

        _minLevel.ValueChanged += (_, _) =>
        {
            UpdateMinLevelText();
            _reader.MinimumLevel = Math.Pow(10, _minLevel.Value / 20);
            Settings.Set("reader.minLevel", _minLevel.Value);
        };
        _rate.SelectedIndexChanged += (_, _) =>
        {
            _reader.Rate = Ui.SelectedRate(_rate, withAuto: true);
            Settings.Set("reader.rate", _rate.SelectedIndex);
        };
        _channel.SelectedIndexChanged += (_, _) =>
        {
            if (_channel.SelectedIndex < 0) return;
            _reader.Channel = _channel.SelectedIndex;
            Settings.Set("reader.channel", _channel.SelectedIndex);
        };
        _device.SelectedIndexChanged += async (_, _) => await DeviceChangedAsync();

        _reader.FrameDecoded += OnFrame; // audio thread
        _reader.Stopped += error => MainThread.BeginInvokeOnMainThread(() =>
        {
            _start.Text = "Start";
            if (error is not null) Status($"Input stopped: {error}");
        });

        _rate.SelectedIndex = Math.Clamp(Settings.Get("reader.rate", 0), 0, LtcFrameRateExtensions.All.Count);
        _minLevel.Value = Math.Clamp(Settings.Get("reader.minLevel", -40.0), -70, -10);
        UpdateMinLevelText();

        Content = Ui.Scroll(
            Ui.Heading("Input"),
            Ui.Field("Device", _device, "Sound-card inputs, then 'Loopback' of every output (reads what that output is playing — handy with the Live Generator tab)."),
            Ui.Field("Channel", _channel),
            new HorizontalStackLayout
            {
                Children =
                {
                    _start,
                    Ui.Button("Refresh devices", async (_, _) => await RefreshDevicesAsync()),
                    Ui.Button("Reset decoder", (_, _) => { _reader.ResetDecoder(); ClearStats(); }),
                },
            },
            _status,

            Ui.Panel(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    _tc,
                    _state,
                    new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.Center, Children = { new Label { Text = "Input level", VerticalOptions = LayoutOptions.Center }, _meter, _meterText } },
                },
            }),

            Ui.Heading("Decoder options"),
            Ui.Field("Frame rate", _rate, "Fixes the flag layout. Auto-detect uses the measured speed, frame numbers and the drop-frame flag; 48/50/60 fps pairs read as 24/25/30."),
            Ui.Field("Minimum level", new HorizontalStackLayout { Spacing = 10, Children = { new ContentView { Content = _minLevel, WidthRequest = 260 }, _minLevelText } }, "Quieter input is treated as silence."),

            Ui.Heading("Current frame"),
            Ui.Panel(_details),
            new HorizontalStackLayout
            {
                Children =
                {
                    Ui.Button("Explain frame bit by bit", (_, _) => _explain.Text = _latest is { } f ? LtcDescriber.Explain(f.Frame) : "No frame decoded yet."),
                    Ui.Button("Copy time code", async (_, _) =>
                    {
                        if (_latest is { } f && await CopyAsync(f.Timecode.ToString())) Status($"Copied {f.Timecode}.");
                    }),
                    Ui.Button("Hide explanation", (_, _) => _explain.Text = ""),
                },
            },
            _explain,

            Ui.Heading("Events"),
            Ui.Caption("Jumps (discontinuous addresses), signal loss/return, direction changes and spec issues. Newest first."),
            new HorizontalStackLayout
            {
                Children =
                {
                    Ui.Button("Clear events", (_, _) => { _logLines.Clear(); _logDirty = true; }),
                    Ui.Button("Copy events", async (_, _) => { if (await CopyAsync(string.Join(Environment.NewLine, _logLines))) Status("Copied the events."); }),
                },
            },
            _log);

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Loaded += async (_, _) => { if (_inputs.Count == 0) await RefreshDevicesAsync(); };
    }

    /// <summary>Stops capture (window closing).</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _reader.Stop();
    }

    // The clipboard can be held by another process; report that instead of crashing (these run from async void handlers).
    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(text);
            return true;
        }
        catch (Exception ex)
        {
            Status($"Could not copy to the clipboard: {ex.Message}");
            return false;
        }
    }

    private void UpdateMinLevelText() => _minLevelText.Text = string.Create(CultureInfo.InvariantCulture, $"{_minLevel.Value:0} dBFS");

    private void Status(string text) => _status.Text = text;

    private int _startsPending;       // StartAsync calls still opening a device
    private string? _requestedDevice; // endpoint of the latest start request

    private async Task RefreshDevicesAsync()
    {
        _loading = true;
        try
        {
            string? current = SelectedEndpoint?.Id ?? Settings.Get("reader.device", "");
            _inputs = await Task.Run(AudioDevices.Inputs);
            _device.ItemsSource = _inputs.Select(d => d.DisplayName).ToList();
            int index = _inputs.ToList().FindIndex(d => d.Id == current && !d.IsLoopback);
            if (index < 0) index = _inputs.ToList().FindIndex(d => d.Id == current);
            _device.SelectedIndex = _inputs.Count == 0 ? -1 : Math.Max(0, index);
            Status(_inputs.Count == 0 ? "No audio inputs found." : $"{_inputs.Count} input(s) found.");
        }
        catch (Exception ex)
        {
            Status($"Could not list audio devices: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
        await DeviceChangedAsync();
    }

    private AudioEndpoint? SelectedEndpoint =>
        _device.SelectedIndex >= 0 && _device.SelectedIndex < _inputs.Count ? _inputs[_device.SelectedIndex] : null;

    private async Task DeviceChangedAsync()
    {
        if (_loading) return;
        var ep = SelectedEndpoint;
        if (ep is null) { _channel.ItemsSource = null; return; }
        Settings.Set("reader.device", ep.Id);
        _channel.ItemsSource = Enumerable.Range(1, ep.Channels).Select(c => c switch { 1 => "1 (left)", 2 => "2 (right)", _ => c.ToString(CultureInfo.InvariantCulture) }).ToList();
        _channel.SelectedIndex = Math.Clamp(Settings.Get("reader.channel", 0), 0, ep.Channels - 1);
        // Follow a different device; refreshing the list re-selects the same one, which needs no restart.
        if ((_reader.IsRunning || _startsPending > 0) && Key(ep) != _requestedDevice) await StartAsync();
    }

    private async Task ToggleAsync()
    {
        if (_reader.IsRunning) { _reader.Stop(); Status("Stopped."); return; }
        await StartAsync();
    }

    private async Task StartAsync()
    {
        var ep = SelectedEndpoint;
        if (ep is null) { Status("Choose an input first."); return; }
        int channel = Math.Max(0, _channel.SelectedIndex);
        _requestedDevice = Key(ep);
        _start.IsEnabled = false;
        _startsPending++;
        try
        {
            _reader.Rate = Ui.SelectedRate(_rate, withAuto: true);
            _reader.MinimumLevel = Math.Pow(10, _minLevel.Value / 20);
            if (!await _reader.StartAsync(ep, channel)) return; // superseded by a later start or stop
            // A channel picked while the device was opening was overwritten by the start; apply it now.
            channel = Math.Max(0, _channel.SelectedIndex);
            _reader.Channel = channel;
            ClearStats();
            _start.Text = "Stop";
            Status($"Reading {ep.DisplayName}, channel {channel + 1} ({_reader.FormatDescription}).");
            Log($"Started: {ep.Name}, channel {channel + 1}, {_reader.FormatDescription}");
        }
        catch (Exception ex)
        {
            _start.Text = "Start";
            Status($"Could not open {ep.Name}: {ex.Message}");
        }
        finally
        {
            _startsPending--;
            _start.IsEnabled = true;
        }
    }

    // An input and the loopback of the output with the same id are different sources.
    private static string Key(AudioEndpoint ep) => $"{ep.Id}|{ep.IsLoopback}";

    private void ClearStats()
    {
        Interlocked.Exchange(ref _frameCount, 0);
        Interlocked.Exchange(ref _jumpCount, 0);
        Interlocked.Exchange(ref _dropouts, 0);
        Volatile.Write(ref _latest, null);
        _hadSignal = false;
    }

    // Audio thread: keep it light.
    private void OnFrame(LtcDecodedFrame f)
    {
        var previous = Volatile.Read(ref _latest);
        Volatile.Write(ref _latest, f);
        Interlocked.Increment(ref _frameCount);
        if (previous is null) return;

        if (previous.Direction != f.Direction)
            _events.Enqueue($"{f.Timecode}  direction → {(f.Direction == LtcDirection.Reverse ? "reverse" : "forward")}");
        else if (!f.IsContinuous)
        {
            Interlocked.Increment(ref _jumpCount);
            _events.Enqueue($"{f.Timecode}  jump from {previous.Timecode} ({f.Timecode.TotalFrames - previous.Timecode.TotalFrames:+#;-#;0} frames)");
        }
        if (f.Issues.Count > 0 && !previous.Issues.SequenceEqual(f.Issues))
            _events.Enqueue($"{f.Timecode}  {string.Join("; ", f.Issues)}");
    }

    private void Log(string text)
    {
        _logLines.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(MaxLogLines, _logLines.Count - MaxLogLines);
        _logDirty = true;
    }

    private bool _logDirty;

    private void Refresh()
    {
        // Level meter
        float peak = _reader.IsRunning ? _reader.TakePeak() : 0;
        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        double shown = double.IsNegativeInfinity(db) ? 0 : Math.Clamp((db + 60) / 60, 0, 1);
        _meter.Progress = Math.Max(shown, _meter.Progress - 0.03); // fast attack, slow release
        _meter.ProgressColor = db > -3 ? Colors.OrangeRed : db > -20 ? Colors.MediumSeaGreen : Colors.SteelBlue;
        _meterText.Text = double.IsNegativeInfinity(db) ? "   -∞ dBFS" : string.Create(CultureInfo.InvariantCulture, $"{db,5:0.0} dBFS");

        // Lock state
        long? age = _reader.MillisecondsSinceLastFrame;
        bool signal = _reader.IsRunning && age is < SignalTimeoutMs;
        var f = Volatile.Read(ref _latest);
        if (signal != _hadSignal && _reader.IsRunning)
        {
            if (signal) Log($"LTC locked at {f?.Timecode}");
            else if (f is not null) { Interlocked.Increment(ref _dropouts); Log($"LTC lost after {f.Timecode}"); }
            _hadSignal = signal;
        }

        while (_events.TryDequeue(out var e)) Log(e);
        if (_logDirty)
        {
            _logDirty = false;
            _log.Text = string.Join(Environment.NewLine, _logLines);
        }

        if (!_reader.IsRunning)
        {
            _state.Text = "Stopped";
            _state.TextColor = Colors.Gray;
            _tc.Opacity = 0.35;
        }
        else if (!signal || f is null)
        {
            _state.Text = f is null ? "Waiting for LTC…" : "No LTC — holding last frame";
            _state.TextColor = Colors.Orange;
            _tc.Opacity = 0.35;
        }
        else
        {
            _state.Text = f!.Direction == LtcDirection.Reverse ? "LOCKED — reverse" : "LOCKED";
            _state.TextColor = Colors.MediumSeaGreen;
            _tc.Opacity = 1;
        }

        if (f is null) { _tc.Text = "--:--:--:--"; _details.Text = "No frame decoded yet."; return; }
        _tc.Text = f.Timecode.ToString();
        _details.Text = Describe(f);
    }

    private string Describe(LtcDecodedFrame f)
    {
        var frame = f.Frame;
        var detected = _reader.DetectedRate ?? frame.Rate;
        bool auto = Ui.SelectedRate(_rate, withAuto: true) is null;
        var sb = new StringBuilder();
        sb.AppendLine($"Time code    {f.Timecode}   ({(detected.IsDropFrame() ? "drop-frame" : "non-drop-frame")})");
        sb.AppendLine($"Rate         {detected.DisplayName()} fps{(auto ? " (detected)" : "")} — {detected.Description()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Measured     {_reader.MeasuredCodewordRate:0.0000} codewords/s   speed x{Math.Abs(f.Speed):0.000}   {(f.Direction == LtcDirection.Reverse ? "reverse" : "forward")}");
        sb.AppendLine($"User bits    {frame.UserBits.ToDisplayString()}   \"{frame.UserBits.ToText()}\"");
        sb.AppendLine($"BGF          {frame.BinaryGroupFlags.BitPattern()}  {frame.BinaryGroupFlags.DisplayName()}");
        sb.AppendLine($"Meaning      {UserBitsDescriber.Summary(frame.UserBits, frame.BinaryGroupFlags, frame.Rate)}");
        if (frame.GetDateTimeZone() is { } dtz && dtz.ToDateTimeOffset(f.Timecode) is { } instant)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Instant      {instant:yyyy-MM-dd HH:mm:ss.fff zzz} (ST 309)");
        if (frame.GetPageLine() is { } pl)
            sb.AppendLine($"Page/line    {pl}");
        sb.AppendLine($"Color frame  {(frame.ColorFrame ? "set — " + ColorFraming.Describe(f.Timecode) : "not set")}");
        sb.AppendLine($"Polarity     {(frame.PolarityCorrection ? "corrected (even zeros)" : "not corrected")}");
        sb.AppendLine($"Codeword     {f.Codeword.ToHex()}");
        sb.AppendLine($"Issues       {(f.Issues.Count == 0 ? "none" : string.Join("; ", f.Issues))}");
        sb.AppendLine($"Input        {_reader.FormatDescription}, channel {_reader.Channel + 1}");
        sb.Append(CultureInfo.InvariantCulture, $"Counters     {Interlocked.Read(ref _frameCount)} frames, {Interlocked.Read(ref _jumpCount)} jumps, {Interlocked.Read(ref _dropouts)} dropouts");
        return sb.ToString();
    }
}
