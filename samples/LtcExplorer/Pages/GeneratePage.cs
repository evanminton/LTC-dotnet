using System.Globalization;
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LtcExplorer.Pages;

/// <summary>Build an LTC signal with every generator and codeword option, preview it, and save it as a WAV file.</summary>
public class GeneratePage : ContentPage
{
    private static readonly int[] SampleRates = [22_050, 44_100, 48_000, 88_200, 96_000, 192_000];

    private readonly Entry _start = new() { Text = "01:00:00:00", FontFamily = Ui.Mono };
    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps25);
    private readonly Switch _now = Ui.Toggle();
    private readonly Entry _seconds = new() { Text = "30", Keyboard = Keyboard.Numeric };
    private readonly Picker _sampleRate = new() { ItemsSource = SampleRates.Select(s => $"{s:N0} Hz").ToList(), SelectedIndex = 2 };
    private readonly Picker _format = new() { ItemsSource = Enum.GetNames<WavSampleFormat>(), SelectedIndex = 0 };
    private readonly Slider _level = new(-40, 0, -6);
    private readonly Label _levelText = new();
    private readonly Entry _rise = new() { Text = "40", Keyboard = Keyboard.Numeric };
    private readonly Entry _speed = new() { Text = "1.0", Keyboard = Keyboard.Numeric };
    private readonly Switch _invert = Ui.Toggle();
    private readonly Switch _reverse = Ui.Toggle();
    private readonly Entry _userBits = new() { Text = "00000000", FontFamily = Ui.Mono, MaxLength = 11 };
    private readonly Entry _text = new() { Placeholder = "up to 4 characters", MaxLength = 4 };
    private readonly Picker _bgf = Ui.BgfPicker();
    private readonly Switch _colorFrame = Ui.Toggle();
    private readonly Switch _polarity = Ui.Toggle(true);
    private readonly Label _summary = Ui.MonoLabel();
    private readonly Label _status = Ui.Caption("");
    private readonly Label _message = Ui.Caption("");
    private readonly Button _clearMessage;
    private IReadOnlyList<PageLineFrame>? _messageFrames;
    private AuxiliaryTimeAddress? _runningAux;
    private readonly WaveformDrawable _wave = new();
    private readonly GraphicsView _waveView;

    public GeneratePage()
    {
        Title = "Generate";
        _waveView = new GraphicsView { Drawable = _wave, HeightRequest = 180 };
        _clearMessage = Ui.Button("Stop per-frame user bits", (_, _) => { _messageFrames = null; _runningAux = null; Preview(); });

        _level.ValueChanged += (_, _) => { UpdateLevelText(); Preview(); };
        _text.TextChanged += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.NewTextValue)) return;
            try
            {
                _userBits.Text = UserBits.FromText(e.NewTextValue).ToString();
                _bgf.SelectedIndex = (int)BinaryGroupFlags.EightBitCharacters;
            }
            catch (ArgumentException ex) { _status.Text = ex.Message; }
        };
        foreach (var entry in new[] { _start, _userBits, _rise, _speed }) entry.TextChanged += (_, _) => Preview();
        foreach (var picker in new[] { _rate, _bgf, _sampleRate }) picker.SelectedIndexChanged += (_, _) => Preview();
        foreach (var sw in new[] { _colorFrame, _polarity, _invert, _reverse, _now }) sw.Toggled += (_, _) => Preview();
        UpdateLevelText();

        Content = Ui.Scroll(
            Ui.Heading("Time code"),
            Ui.Field("Start", _start, "HH:MM:SS:FF — use ';' before the frames for drop-frame."),
            Ui.Field("Frame rate", _rate, "Sets the bit rate (80 × codewords/s) and the flag layout (24/25/30-frame)."),
            Ui.Field("Start at time of day", _now, "Clock-time start; pair with BGF 010/110/111 (§8.5)."),
            Ui.Field("Duration (s)", _seconds),

            Ui.Heading("Codeword content"),
            Ui.Field("User bits (hex)", _userBits, "Eight binary groups, group 8 first (§8.4, Table 4)."),
            Ui.Field("User bits as text", _text, "Four 8-bit characters, §8.4.2 layout (sets BGF 001)."),
            Ui.Field("Binary group flags", _bgf, "BGF2 BGF1 BGF0 — what the user bits mean and whether the time is clock time (Table 1)."),
            Ui.Field("Color frame flag", _colorFrame, "Address is color-framed (§8.3.2). Not used at 24 frames."),
            Ui.Caption("Date & time zone (ST 309) and page/line (ST 262) user bits are built on the User Bits tab → 'Use in generator'."),
            new HorizontalStackLayout { Spacing = 10, Children = { _message, _clearMessage } },
            Ui.Field("Polarity correction", _polarity, "Keep an even number of zeros per codeword so the sync word polarity is stable (§9.2.3)."),

            Ui.Heading("Signal"),
            Ui.Field("Sample rate", _sampleRate),
            Ui.Field("WAV format", _format),
            Ui.Field("Peak level", new HorizontalStackLayout { Spacing = 10, Children = { new ContentView { Content = _level, WidthRequest = 260 }, _levelText } }),
            Ui.Field("Rise time (µs)", _rise, "10–90 % edge; the spec asks for 40 ± 10 µs (§9.6.1). 0 = square wave."),
            Ui.Field("Speed", _speed, "Varispeed factor; 1.0 = nominal."),
            Ui.Field("Invert polarity", _invert, "Biphase mark is polarity-insensitive, so receivers read either."),
            Ui.Field("Reverse", _reverse, "Send bit 79 first and count down, as a tape played backwards."),

            Ui.Heading("First codeword"),
            Ui.Panel(_waveView),
            _summary,
            new HorizontalStackLayout { Children = { Ui.Button("Generate WAV", async (_, _) => await GenerateAsync()) } },
            _status);

        Preview();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (AppState.PendingUserBits is { } pending)
        {
            AppState.PendingUserBits = null;
            _text.Text = "";
            _userBits.Text = pending.Bits.ToString();
            _bgf.SelectedIndex = (int)pending.Flags;
            _messageFrames = pending.Message;
            _runningAux = AppState.PendingAuxiliary;
            AppState.PendingAuxiliary = null;
            Preview();
        }
    }

    private void UpdateLevelText() => _levelText.Text = string.Create(CultureInfo.InvariantCulture, $"{_level.Value:0} dBFS");

    private LtcFrame BuildFrame(out string? error)
    {
        error = null;
        var rate = Ui.SelectedRate(_rate) ?? LtcFrameRate.Fps25;
        Timecode tc;
        if (_now.IsToggled) tc = Timecode.FromTimeOfDay(DateTime.Now.TimeOfDay, rate);
        else if (!Timecode.TryParse(_start.Text, rate, out tc, out error)) tc = Timecode.Zero(rate);

        var ub = UserBits.Empty;
        try { ub = UserBits.Parse(string.IsNullOrWhiteSpace(_userBits.Text) ? "0" : _userBits.Text); }
        catch (FormatException ex) { error ??= ex.Message; }

        return new LtcFrame(tc)
        {
            UserBits = ub,
            BinaryGroupFlags = (BinaryGroupFlags)Math.Max(0, _bgf.SelectedIndex),
            ColorFrame = _colorFrame.IsToggled,
            PolarityCorrection = _polarity.IsToggled,
        };
    }

    private LtcGenerator BuildGenerator(LtcFrame frame, int sampleRate)
    {
        double rise = double.TryParse(_rise.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? Math.Clamp(r, 0, 1000) : 40;
        double speed = double.TryParse(_speed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? Math.Clamp(s, 0.05, 20) : 1;
        var frames = _messageFrames;
        var aux = _runningAux;
        return new LtcGenerator(frame, sampleRate)
        {
            FrameHook = aux is not null ? AuxiliaryTimeAddress.RunningHook(aux, frame.BinaryGroupFlags == BinaryGroupFlags.ClockTimePageLine)
                : frames is null ? null
                : (Func<long, LtcFrame, LtcFrame>)((i, f) => f.WithPageLine(frames[(int)(i % frames.Count)], f.BinaryGroupFlags == BinaryGroupFlags.ClockTimePageLine)),
            Amplitude = (float)Math.Pow(10, _level.Value / 20),
            RiseTime = TimeSpan.FromTicks((long)Math.Round(rise * 10)),
            Invert = _invert.IsToggled,
            Reverse = _reverse.IsToggled,
            Speed = speed,
        };
    }

    private int SampleRate => SampleRates[Math.Max(0, _sampleRate.SelectedIndex)];

    private void Preview()
    {
        var frame = BuildFrame(out string? error);
        var gen = BuildGenerator(frame, SampleRate);
        int samples = (int)(LtcGenerator.SamplesFor(1, frame.Rate, SampleRate) / gen.Speed);
        _wave.Samples = gen.Read(Math.Max(2, samples));
        var cw = frame.ToCodeword();
        _wave.Codeword = gen.Reverse ? null : cw; // reversed audio carries bit 79 first, so cell labels would not line up
        _waveView.Invalidate();

        var rate = frame.Rate;
        _summary.Text = string.Create(CultureInfo.InvariantCulture,
            $"{frame}\nUser bits  {UserBitsDescriber.Summary(frame.UserBits, frame.BinaryGroupFlags, frame.Rate)}\nHex  {cw.ToHex()}\nRate {rate.DisplayName()} — {rate.Description()}\nBit period {rate.BitPeriod().TotalMicroseconds / gen.Speed:0.0} µs, codeword {rate.CodewordDuration().TotalMilliseconds / gen.Speed:0.###} ms");
        _message.Text = _runningAux is { } ra ? $"RP 169 auxiliary time address running from {ra.Timecode}."
            : _messageFrames is { } mf ? $"ST 262 message string: {mf.Count} frames cycling through the user bits." : "";
        _clearMessage.IsVisible = _messageFrames is not null || _runningAux is not null;
        var notes = frame.Validate();
        _status.Text = error ?? (notes.Count > 0 ? string.Join("\n", notes) : "");
    }

    private async Task GenerateAsync()
    {
        var frame = BuildFrame(out string? error);
        if (error is not null) { _status.Text = error; return; }
        if (!double.TryParse(_seconds.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0 || seconds > 3600)
        {
            _status.Text = "Duration must be between 0 and 3600 seconds.";
            return;
        }

        int sr = SampleRate;
        var format = (WavSampleFormat)Enum.Parse(typeof(WavSampleFormat), (string)_format.SelectedItem);
        var gen = BuildGenerator(frame, sr);
        _status.Text = "Generating…";

        string name = $"LTC_{frame.Timecode.ToString().Replace(':', '-').Replace(';', '-')}_{frame.Rate.Token()}_{sr}.wav";
        string dir = DeviceInfo.Platform == DevicePlatform.WinUI
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LtcExplorer")
            : FileSystem.CacheDirectory;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);

        await Task.Run(() =>
        {
            float[] audio = gen.Read((int)Math.Ceiling(seconds * sr));
            WavFile.Write(path, audio, sr, format);
        });

        AppState.LastGeneratedPath = path;
        _status.Text = $"Saved {path}";

        if (DeviceInfo.Platform != DevicePlatform.WinUI)
            await Share.Default.RequestAsync(new ShareFileRequest { Title = name, File = new ShareFile(path) });
    }
}
