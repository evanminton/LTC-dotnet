using System.Globalization;
using System.Text;
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;
using LtcStudio.Audio;

namespace LtcStudio.Pages;

/// <summary>Generates LTC live to a sound-card output, with every generator and codeword option changeable while running.</summary>
public class LiveGeneratorPage : ContentPage
{
    private const int ModeStart = 0, ModeTimeOfDay = 1;

    private readonly LiveGenerator _gen = new();
    private IReadOnlyList<AudioEndpoint> _outputs = [];
    private IReadOnlyList<OutputChannel> _channels = [];

    // Output
    private readonly Picker _device = new();
    private readonly Picker _channel = new();
    private readonly Entry _latency = new() { Text = "40", Keyboard = Keyboard.Numeric, WidthRequest = 90, HorizontalOptions = LayoutOptions.Start };
    private readonly Button _start;

    // Time code
    private readonly Picker _mode = new() { ItemsSource = new List<string> { "Start from a time code", "Time of day (jam to the PC clock)" } };
    private readonly Entry _startTc = new() { Text = "01:00:00:00", FontFamily = Ui.Mono, WidthRequest = 180, HorizontalOptions = LayoutOptions.Start };
    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps25);
    private readonly Switch _followClock = Ui.Toggle(true);
    private readonly Entry _locateTc = new() { Text = "10:00:00:00", FontFamily = Ui.Mono, WidthRequest = 180 };

    // Codeword content
    private readonly Entry _userBits = new() { Text = "00000000", FontFamily = Ui.Mono, MaxLength = 11 };
    private readonly Entry _text = new() { Placeholder = "up to 4 characters", MaxLength = 4 };
    private readonly Picker _bgf = Ui.BgfPicker();
    private readonly Switch _colorFrame = Ui.Toggle();
    private readonly Switch _polarity = Ui.Toggle(true);
    private readonly Label _message = Ui.Caption("");
    private readonly Button _clearMessage;
    private IReadOnlyList<PageLineFrame>? _messageFrames;
    private AuxiliaryTimeAddress? _runningAux;
    private int _seenUserBits;

    // Signal
    private readonly Slider _level = new(-40, 0, -12);
    private readonly Label _levelText = new();
    private readonly Entry _rise = new() { Text = "40", Keyboard = Keyboard.Numeric };
    private readonly Slider _speed = new(0.25, 4, 1);
    private readonly Label _speedText = new();
    private readonly Switch _invert = Ui.Toggle();
    private readonly Switch _reverse = Ui.Toggle();

    // Display
    private readonly Label _tc = new()
    {
        Text = "--:--:--:--",
        FontFamily = Ui.Mono,
        FontSize = 88,
        FontAttributes = FontAttributes.Bold,
        HorizontalOptions = LayoutOptions.Center,
    };
    private readonly Label _state = new() { Text = "Stopped", FontSize = 18, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center };
    private readonly Label _details = Ui.MonoLabel();
    private readonly Label _status = Ui.Caption("");
    private readonly Label _log = Ui.MonoLabel(12);
    private readonly List<string> _logLines = [];

    private readonly IDispatcherTimer _timer;
    private bool _loading;
    private DateTime _startedAt;
    private int _driftChecks;
    private long _lastClockCheck;

    public LiveGeneratorPage()
    {
        Title = "Live Generator";
        _start = Ui.Button("Start", async (_, _) => await ToggleAsync());
        _start.MinimumWidthRequest = 110;
        _clearMessage = Ui.Button("Stop per-frame user bits", (_, _) => { _messageFrames = null; _runningAux = null; ApplyLive(); });

        _mode.SelectedIndex = Math.Clamp(Settings.Get("gen.mode", ModeStart), 0, 1);
        _rate.SelectedIndex = Math.Clamp(Settings.Get("gen.rate", _rate.SelectedIndex), 0, LtcFrameRateExtensions.All.Count - 1);
        _startTc.Text = Settings.Get("gen.start", "01:00:00:00");
        _level.Value = Math.Clamp(Settings.Get("gen.level", -12.0), -40, 0);
        _latency.Text = Settings.Get("gen.latency", "40");
        UpdateLabels();

        _device.SelectedIndexChanged += async (_, _) => await DeviceChangedAsync();
        _channel.SelectedIndexChanged += (_, _) =>
        {
            if (_channel.SelectedIndex < 0 || _channel.SelectedIndex >= _channels.Count) return;
            _gen.Channel = _channels[_channel.SelectedIndex].Channel;
            Settings.Set("gen.channel", _channel.SelectedIndex);
        };
        _mode.SelectedIndexChanged += (_, _) => { Settings.Set("gen.mode", _mode.SelectedIndex); UpdateVisibility(); };
        _rate.SelectedIndexChanged += async (_, _) =>
        {
            Settings.Set("gen.rate", _rate.SelectedIndex);
            if (_gen.IsRunning) { Log("Frame rate changed — restarting the generator."); await StartAsync(); }
        };
        _startTc.TextChanged += (_, _) => Settings.Set("gen.start", _startTc.Text ?? "");
        _latency.TextChanged += (_, _) => Settings.Set("gen.latency", _latency.Text ?? "40");
        _text.TextChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.NewTextValue)) return;
            try
            {
                _userBits.Text = UserBits.FromText(e.NewTextValue).ToString();
                _bgf.SelectedIndex = (int)BinaryGroupFlags.EightBitCharacters;
            }
            catch (ArgumentException ex) { Status(ex.Message); }
        };
        _userBits.TextChanged += (_, _) => ApplyLive();
        _bgf.SelectedIndexChanged += (_, _) => ApplyLive();
        foreach (var sw in new[] { _colorFrame, _polarity, _invert, _reverse }) sw.Toggled += (_, _) => ApplyLive();
        _rise.TextChanged += (_, _) => ApplyLive();
        _level.ValueChanged += (_, _) => { UpdateLabels(); Settings.Set("gen.level", _level.Value); ApplyLive(); };
        _speed.ValueChanged += (_, _) => { UpdateLabels(); ApplyLive(); };

        Content = Ui.Scroll(
            Ui.Heading("Output"),
            Ui.Field("Device", _device, "The code is played at the device's own shared-mode sample rate, so Windows does not resample it."),
            Ui.Field("Channel", _channel, "Send LTC on one channel and leave the others silent, or on all of them."),
            Ui.Field("Latency (ms)", _latency, "WASAPI buffer. Time of day is compensated by this much; 20–60 ms is typical."),
            new HorizontalStackLayout
            {
                Children =
                {
                    _start,
                    Ui.Button("Refresh devices", async (_, _) => await RefreshDevicesAsync()),
                },
            },
            _status,

            Ui.Panel(new VerticalStackLayout { Spacing = 4, Children = { _tc, _state } }),

            Ui.Heading("Time code"),
            Ui.Field("Source", _mode),
            Ui.Field("Start", _startTc, "HH:MM:SS:FF — use ';' before the frames for drop-frame."),
            Ui.Field("Frame rate", _rate, "Sets the bit rate (80 × codewords/s) and the flag layout. Changing it restarts the output."),
            Ui.Field("Follow the PC clock", _followClock, "Time of day only: re-jam when the code drifts two frames or more from the PC clock (sound-card clocks are not the PC clock)."),
            Ui.Field("Locate", new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    _locateTc,
                    Ui.Button("Locate", (_, _) => Locate(_locateTc.Text)),
                    Ui.Button("Jam to time of day", (_, _) => JamToClock("Jammed to time of day")),
                },
            }, "Jumps the running code to a new address from the next codeword (readers will report a jump)."),

            Ui.Heading("Codeword content (applies live)"),
            Ui.Field("User bits (hex)", _userBits, "Eight binary groups, group 8 first (§8.4, Table 4)."),
            Ui.Field("User bits as text", _text, "Four 8-bit characters, §8.4.2 layout (sets BGF 001)."),
            Ui.Field("Binary group flags", _bgf, "BGF2 BGF1 BGF0 — what the user bits mean and whether the time is clock time (Table 1)."),
            Ui.Field("Color frame flag", _colorFrame, "Address is color-framed (§8.3.2). Not used at 24 frames."),
            Ui.Field("Polarity correction", _polarity, "Keep an even number of zeros per codeword (§9.2.3)."),
            Ui.Caption("Date & time zone (ST 309), page/line (ST 262) and RP 169 user bits are built on the User Bits tab → 'Use in generator'."),
            new HorizontalStackLayout { Spacing = 10, Children = { _message, _clearMessage } },

            Ui.Heading("Signal (applies live)"),
            Ui.Field("Peak level", new HorizontalStackLayout { Spacing = 10, Children = { new ContentView { Content = _level, WidthRequest = 260 }, _levelText } }, "−12 dBFS ≈ 1 V p-p on a +4 dBu-aligned interface; set to suit the receiver."),
            Ui.Field("Rise time (µs)", _rise, "10–90 % edge; the spec asks for 40 ± 10 µs (§9.6.1). 0 = square wave."),
            Ui.Field("Speed", new HorizontalStackLayout { Spacing = 10, Children = { new ContentView { Content = _speed, WidthRequest = 260 }, _speedText, Ui.Button("1.0×", (_, _) => _speed.Value = 1) } }, "Varispeed; the address still advances one per codeword."),
            Ui.Field("Invert polarity", _invert, "Biphase mark is polarity-insensitive, so receivers read either."),
            Ui.Field("Reverse", _reverse, "Send bit 79 first and count down, as a tape played backwards."),

            Ui.Heading("Status"),
            Ui.Panel(_details),
            Ui.Heading("Events"),
            new HorizontalStackLayout { Children = { Ui.Button("Clear events", (_, _) => { _logLines.Clear(); _log.Text = ""; }) } },
            _log);

        _gen.Stopped += error => MainThread.BeginInvokeOnMainThread(() =>
        {
            _start.Text = "Start";
            if (error is not null) { Status($"Output stopped: {error}"); Log($"Output stopped: {error}"); }
        });

        UpdateVisibility();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Loaded += async (_, _) => { if (_outputs.Count == 0) await RefreshDevicesAsync(); };
    }

    /// <summary>Stops output (window closing).</summary>
    public void Shutdown()
    {
        _timer.Stop();
        _gen.Stop();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (AppState.UserBitsVersion != _seenUserBits && AppState.PendingUserBits is { } pending)
        {
            _seenUserBits = AppState.UserBitsVersion;
            _text.Text = "";
            _userBits.Text = pending.Bits.ToString();
            _bgf.SelectedIndex = (int)pending.Flags;
            _messageFrames = pending.Message;
            _runningAux = AppState.PendingAuxiliary;
            ApplyLive();
            Log("User bits received from the User Bits tab.");
        }
    }

    private LtcFrameRate Rate => Ui.SelectedRate(_rate) ?? LtcFrameRate.Fps25;

    private bool TimeOfDay => _mode.SelectedIndex == ModeTimeOfDay;

    private int LatencyMs => int.TryParse(_latency.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) ? Math.Clamp(ms, 5, 500) : 40;

    private void Status(string text) => _status.Text = text;

    private void Log(string text)
    {
        _logLines.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}  {text}");
        if (_logLines.Count > 200) _logLines.RemoveAt(_logLines.Count - 1);
        _log.Text = string.Join(Environment.NewLine, _logLines);
    }

    private void UpdateLabels()
    {
        _levelText.Text = string.Create(CultureInfo.InvariantCulture, $"{_level.Value:0} dBFS");
        _speedText.Text = string.Create(CultureInfo.InvariantCulture, $"{_speed.Value:0.000}×");
    }

    private void UpdateVisibility()
    {
        _startTc.IsEnabled = !TimeOfDay;
        _followClock.IsEnabled = TimeOfDay;
    }

    // ---------- devices ----------

    private AudioEndpoint? SelectedEndpoint =>
        _device.SelectedIndex >= 0 && _device.SelectedIndex < _outputs.Count ? _outputs[_device.SelectedIndex] : null;

    private async Task RefreshDevicesAsync()
    {
        _loading = true;
        try
        {
            string current = SelectedEndpoint?.Id ?? Settings.Get("gen.device", "");
            _outputs = await Task.Run(AudioDevices.Outputs);
            _device.ItemsSource = _outputs.Select(d => d.DisplayName).ToList();
            int index = _outputs.ToList().FindIndex(d => d.Id == current);
            _device.SelectedIndex = _outputs.Count == 0 ? -1 : Math.Max(0, index);
            Status(_outputs.Count == 0 ? "No audio outputs found." : $"{_outputs.Count} output(s) found.");
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

    private async Task DeviceChangedAsync()
    {
        if (_loading) return;
        var ep = SelectedEndpoint;
        if (ep is null) { _channel.ItemsSource = null; return; }
        Settings.Set("gen.device", ep.Id);
        _channels = OutputChannel.For(ep.Channels);
        _channel.ItemsSource = _channels.Select(c => c.Name).ToList();
        _channel.SelectedIndex = Math.Clamp(Settings.Get("gen.channel", 0), 0, _channels.Count - 1);
        if (_gen.IsRunning) await StartAsync();
    }

    // ---------- transport ----------

    private async Task ToggleAsync()
    {
        if (_gen.IsRunning) { _gen.Stop(); Status("Stopped."); Log("Stopped."); return; }
        await StartAsync();
    }

    private async Task StartAsync()
    {
        var ep = SelectedEndpoint;
        if (ep is null) { Status("Choose an output first."); return; }
        string? error = null;
        var rate = Rate;
        Timecode start = default;
        if (!TryBuildFrame(out var content, out error) ||
            (!TimeOfDay && !Timecode.TryParse(_startTc.Text, rate, out start, out error)))
        {
            error ??= "Invalid start time code.";
            // A restart that can't go ahead leaves the old generator playing; say at which rate.
            Status(_gen.IsRunning ? $"{error} Still playing at {_gen.Get(g => g.Rate).DisplayName()} fps." : error);
            return;
        }
        if (TimeOfDay) start = content.Timecode;

        int channel = _channel.SelectedIndex >= 0 && _channel.SelectedIndex < _channels.Count ? _channels[_channel.SelectedIndex].Channel : -1;
        int latencyMs = LatencyMs;
        bool timeOfDay = TimeOfDay;
        _start.IsEnabled = false;
        try
        {
            await Task.Run(() => _gen.Start(ep, channel, latencyMs, (sampleRate, latency) =>
            {
                // Time of day: the first sample reaches the output one buffer from now.
                var first = content with { Timecode = timeOfDay ? ClockTimecode(content, rate, latency) : start };
                var g = new LtcGenerator(first, sampleRate);
                Configure(g, first);
                return g;
            }));
            _startedAt = DateTime.Now;
            _driftChecks = 0;
            _start.Text = "Stop";
            Status($"Playing on {ep.Name} ({_gen.FormatDescription}), {(channel < 0 ? "all channels" : $"channel {channel + 1}")}.");
            Log($"Started {rate.DisplayName()} fps on {ep.Name}, {_gen.FormatDescription}, latency {latencyMs} ms.");
        }
        catch (Exception ex)
        {
            _start.Text = "Start";
            Status($"Could not open {ep.Name}: {ex.Message}");
        }
        finally
        {
            _start.IsEnabled = true;
        }
    }

    private void Locate(string? text)
    {
        if (!_gen.IsRunning) { Status("Start the output first."); return; }
        // Parse at the rate actually playing: after a failed restart the picker can show a rate the generator isn't using.
        var rate = _gen.Get(g => g.Rate);
        if (!Timecode.TryParse(text, rate, out var tc, out string? error)) { Status(error ?? "Invalid time code."); return; }
        _gen.Use(g => g.NextFrame = g.NextFrame with { Timecode = tc });
        Log($"Located to {tc}.");
    }

    private void JamToClock(string reason)
    {
        if (!_gen.IsRunning) { Status("Start the output first."); return; }
        var latency = _gen.Latency;
        _gen.Use(g =>
        {
            // NextFrame starts after the codeword now being written; aim it at the clock one codeword + one buffer ahead.
            var next = g.NextFrame;
            var tc = ClockTimecode(next, next.Rate, latency + next.Rate.CodewordDuration());
            g.NextFrame = next with { Timecode = tc };
        });
        _driftChecks = 0;
        Log($"{reason}.");
    }

    // ---------- building frames ----------

    private bool TryBuildFrame(out LtcFrame frame, out string? error)
    {
        error = null;
        var ub = UserBits.Empty;
        try { ub = UserBits.Parse(string.IsNullOrWhiteSpace(_userBits.Text) ? "0" : _userBits.Text); }
        catch (FormatException ex) { error = $"User bits: {ex.Message}"; }

        frame = new LtcFrame(Timecode.Zero(Rate))
        {
            UserBits = ub,
            BinaryGroupFlags = (BinaryGroupFlags)Math.Max(0, _bgf.SelectedIndex),
            ColorFrame = _colorFrame.IsToggled,
            PolarityCorrection = _polarity.IsToggled,
        };
        return error is null;
    }

    // Time of day for the frame's ST 309 convention: UTC with an MJD date, the date's zone with YYMMDD, else local.
    private static Timecode ClockTimecode(LtcFrame frame, LtcFrameRate rate, TimeSpan ahead)
    {
        var now = DateTimeOffset.UtcNow + ahead;
        var time = frame.GetDateTimeZone() switch
        {
            { TimeAddressIsUtc: true } => now.UtcDateTime.TimeOfDay,
            { TimeZone.Offset: { } offset } => now.ToOffset(offset).TimeOfDay,
            _ => now.ToLocalTime().TimeOfDay,
        };
        return Timecode.FromTimeOfDay(time, rate);
    }

    private void Configure(LtcGenerator g, LtcFrame content)
    {
        double rise = double.TryParse(_rise.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? Math.Clamp(r, 0, 1000) : 40;
        var frames = _messageFrames;
        var aux = _runningAux;
        g.FrameHook = aux is not null ? AuxiliaryTimeAddress.RunningHook(aux, content.BinaryGroupFlags == BinaryGroupFlags.ClockTimePageLine)
            : frames is null ? null
            : (i, f) => f.WithPageLine(frames[(int)(i % frames.Count)], f.BinaryGroupFlags == BinaryGroupFlags.ClockTimePageLine);
        g.Amplitude = (float)Math.Pow(10, _level.Value / 20);
        g.RiseTime = TimeSpan.FromTicks((long)Math.Round(rise * 10));
        g.Invert = _invert.IsToggled;
        g.Reverse = _reverse.IsToggled;
        if (Math.Abs(g.Speed - _speed.Value) > 1e-9) g.Speed = _speed.Value;
    }

    // Pushes the current settings into the running generator (from the next codeword on).
    private void ApplyLive()
    {
        _message.Text = _runningAux is { } ra ? $"RP 169 auxiliary time address running from {ra.Timecode}."
            : _messageFrames is { } mf ? $"ST 262 message string: {mf.Count} frames cycling through the user bits." : "";
        _clearMessage.IsVisible = _messageFrames is not null || _runningAux is not null;

        if (!TryBuildFrame(out var content, out string? error)) { Status(error!); return; }
        var notes = new LtcFrame(Timecode.Zero(Rate)) { BinaryGroupFlags = content.BinaryGroupFlags, UserBits = content.UserBits }.Validate();
        Status(notes.Count > 0 ? string.Join("\n", notes) : _gen.IsRunning ? "Settings applied." : "");
        _gen.Use(g =>
        {
            // Configure first: a Reverse change re-steps the next frame from the one playing, which it can only do
            // while NextFrame hasn't been set for this codeword. Then keep that address and apply the new content.
            Configure(g, content);
            g.NextFrame = content with { Timecode = g.NextFrame.Timecode };
        });
    }

    // ---------- display ----------

    private void Refresh()
    {
        if (!_gen.IsRunning)
        {
            _state.Text = "Stopped";
            _state.TextColor = Colors.Gray;
            _tc.Opacity = 0.35;
            _details.Text = DescribeSettings();
            return;
        }

        var snapshot = _gen.Get(g => (g.CurrentFrame, g.Speed, g.Reverse));
        if (snapshot.CurrentFrame is not { } current) return;

        // CurrentFrame is being written into the buffer; what leaves the socket is about one buffer behind.
        long behind = (long)Math.Round(_gen.Latency.TotalSeconds * current.Rate.CodewordRate() * snapshot.Speed);
        var audible = current.Timecode.AddFrames(snapshot.Reverse ? behind : -behind);
        _tc.Text = audible.ToString();
        _tc.Opacity = 1;
        _state.Text = snapshot.Reverse ? "PLAYING — reverse" : Math.Abs(snapshot.Speed - 1) > 1e-6 ? $"PLAYING — {snapshot.Speed:0.000}×" : "PLAYING";
        _state.TextColor = Colors.MediumSeaGreen;

        if (TimeOfDay && _followClock.IsToggled && !snapshot.Reverse && Math.Abs(snapshot.Speed - 1) < 1e-6)
            CheckDrift(current, audible);

        var sb = new StringBuilder();
        sb.AppendLine($"Output       {_gen.FormatDescription}, {(_gen.Channel < 0 ? "all channels" : $"channel {_gen.Channel + 1}")}, latency {_gen.Latency.TotalMilliseconds:0} ms");
        sb.AppendLine($"Running for  {DateTime.Now - _startedAt:hh\\:mm\\:ss}");
        sb.AppendLine($"Frame        {current}");
        sb.AppendLine($"Meaning      {UserBitsDescriber.Summary(current.UserBits, current.BinaryGroupFlags, current.Rate)}");
        sb.AppendLine($"Codeword     {current.ToCodeword().ToHex()}");
        sb.Append(DescribeSettings());
        _details.Text = sb.ToString();
    }

    // Once a second: compare the audible code with the PC clock; re-jam after three checks two or more frames apart.
    private void CheckDrift(LtcFrame current, Timecode audible)
    {
        long now = Environment.TickCount64;
        if (now - _lastClockCheck < 1000) return;
        _lastClockCheck = now;

        var clock = ClockTimecode(current, current.Rate, TimeSpan.Zero);
        long perDay = current.Rate.AddressesPerDay();
        long diff = ((audible.TotalFrames - clock.TotalFrames) % perDay + perDay) % perDay;
        if (diff > perDay / 2) diff -= perDay;
        // The audible estimate is good to about a frame, so only act on two or more.
        if (Math.Abs(diff) < 2) { _driftChecks = 0; return; }
        if (++_driftChecks < 3) return;
        JamToClock($"Re-jammed to the PC clock (code was {diff:+#;-#} frame(s) off)");
    }

    private string DescribeSettings()
    {
        var rate = Rate;
        double speed = _speed.Value;
        return string.Create(CultureInfo.InvariantCulture,
            $"Rate         {rate.DisplayName()} fps — {rate.Description()}\nBit period   {rate.BitPeriodMicroseconds() / speed:0.0} µs, codeword {rate.CodewordDuration().TotalMilliseconds / speed:0.###} ms, {rate.BitRate() * speed:0} bit/s\nSource       {(TimeOfDay ? "time of day" + (_followClock.IsToggled ? ", following the PC clock" : "") : "start at " + _startTc.Text)}");
    }
}
