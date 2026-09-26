using System.Globalization;
using System.Text;
using LinearTimecode;
using LinearTimecode.Audio;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LtcExplorer.Pages;

/// <summary>Open a WAV file and decode the LTC in it, with every decoder option.</summary>
public class ReadPage : ContentPage
{
    private static readonly FilePickerFileType WavType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = [".wav", ".wave"],
        [DevicePlatform.Android] = ["audio/wav", "audio/x-wav", "audio/*"],
        [DevicePlatform.iOS] = ["com.microsoft.waveform-audio"],
        [DevicePlatform.MacCatalyst] = ["com.microsoft.waveform-audio"],
    });

    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps25, withAuto: true);
    private readonly Entry _channel = new() { Text = "1", Keyboard = Keyboard.Numeric };
    private readonly Slider _minLevel = new(-70, -10, -40);
    private readonly Label _minLevelText = new();
    private readonly Switch _showAll = Ui.Toggle(true);
    private readonly Label _summary = Ui.MonoLabel();
    private readonly Label _frames = Ui.MonoLabel(12);
    private readonly Label _status = Ui.Caption("Pick a WAV file containing LTC.");
    private string? _path;
    private int _decodeId; // only the latest decode updates the page

    public ReadPage()
    {
        Title = "Read";
        _rate.SelectedIndex = 0;
        _minLevel.ValueChanged += (_, _) => _minLevelText.Text = $"{_minLevel.Value:0} dBFS";
        _minLevelText.Text = $"{_minLevel.Value:0} dBFS";

        Content = Ui.Scroll(
            Ui.Heading("Source"),
            new HorizontalStackLayout
            {
                Children =
                {
                    Ui.Button("Open WAV…", async (_, _) => await PickAsync()),
                    Ui.Button("Use last generated", async (_, _) =>
                    {
                        if (AppState.LastGeneratedPath is { } p) { _path = p; await DecodeAsync(); }
                        else _status.Text = "Nothing generated yet — use the Generate tab.";
                    }),
                    Ui.Button("Decode again", async (_, _) => await DecodeAsync()),
                },
            },
            _status,

            Ui.Heading("Decoder options"),
            Ui.Field("Frame rate", _rate, "Fixes the flag layout. Auto-detect uses the measured speed, frame numbers and the drop-frame flag; 48/50/60 fps pairs read as 24/25/30."),
            Ui.Field("Channel", _channel, "1-based channel of the WAV file."),
            Ui.Field("Minimum level", new HorizontalStackLayout { Spacing = 10, Children = { new ContentView { Content = _minLevel, WidthRequest = 260 }, _minLevelText } }, "Quieter input is treated as silence."),
            Ui.Field("List every frame", _showAll, "Off: summary only."),

            Ui.Heading("Summary"),
            Ui.Panel(_summary),
            Ui.Heading("Frames"),
            Ui.Caption("time in file · time code · direction · speed · user bits · BGF · flags"),
            _frames);
    }

    private async Task PickAsync()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a WAV file", FileTypes = WavType });
            if (result is null) return;
            _path = result.FullPath;
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                // Some platforms only hand out a stream; copy it somewhere readable.
                _path = Path.Combine(FileSystem.CacheDirectory, result.FileName);
                await using var src = await result.OpenReadAsync();
                await using var dst = File.Create(_path);
                await src.CopyToAsync(dst);
            }
            await DecodeAsync();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task DecodeAsync()
    {
        if (_path is null) { _status.Text = "Pick a file first."; return; }
        string path = _path;
        int id = ++_decodeId;
        _status.Text = $"Decoding {Path.GetFileName(path)}…";
        LtcFrameRate? rate = Ui.SelectedRate(_rate, withAuto: true);
        int channel = int.TryParse(_channel.Text, out int c) ? c : 1;
        double minLevel = Math.Pow(10, _minLevel.Value / 20);
        bool all = _showAll.IsToggled;

        try
        {
            var (summary, list) = await Task.Run(() =>
            {
                var wav = WavFile.Read(path);
                if (channel < 1 || channel > wav.ChannelCount) throw new InvalidDataException($"Channel {channel} out of range 1–{wav.ChannelCount}.");
                var decoder = new LtcDecoder(wav.SampleRate, rate) { MinimumLevel = minLevel };
                var frames = decoder.Process(wav.Channels[channel - 1]);
                return (Summarize(path, wav, decoder, frames, rate is null), all ? List(frames, wav.SampleRate) : "");
            });
            if (id != _decodeId) return; // a newer decode has started
            _summary.Text = summary;
            _frames.Text = list;
            _status.Text = $"Decoded {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            if (id == _decodeId) _status.Text = ex.Message;
        }
    }

    private static string Summarize(string path, WavFile wav, LtcDecoder decoder, IReadOnlyList<LtcDecodedFrame> frames, bool detected)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File        {Path.GetFileName(path)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Audio       {wav.SampleRate} Hz, {wav.SourceBitsPerSample}-bit, {wav.ChannelCount} ch, {wav.Duration:hh\\:mm\\:ss\\.fff}");
        if (frames.Count == 0) { sb.AppendLine("No LTC found."); return sb.ToString(); }
        var first = frames[0];
        var last = frames[^1];
        sb.AppendLine($"Frames      {frames.Count}   {first.Timecode} → {last.Timecode}");
        sb.AppendLine($"Rate        {(detected ? "detected " : "")}{decoder.DetectedRate?.DisplayName()} fps — {decoder.DetectedRate?.Description()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Measured    {decoder.MeasuredCodewordRate:0.####} codewords/s   speed x{Math.Abs(last.Speed):0.0000}   {last.Direction}");
        sb.AppendLine($"Jumps       {frames.Skip(1).Count(f => !f.IsContinuous)}");
        sb.AppendLine($"User bits   {last.Frame.UserBits.ToDisplayString()}   \"{last.Frame.UserBits.ToText()}\"");
        sb.AppendLine($"BGF         {last.Frame.BinaryGroupFlags.BitPattern()}  {last.Frame.BinaryGroupFlags.DisplayName()}");
        sb.AppendLine($"Meaning     {UserBitsDescriber.Summary(last.Frame.UserBits, last.Frame.BinaryGroupFlags, last.Frame.Rate)}");
        if (last.Frame.GetDateTimeZone() is { } dtz && dtz.ToDateTimeOffset(last.Timecode) is { } instant)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Instant     {instant:yyyy-MM-dd HH:mm:ss.fff zzz} (last frame, ST 309)");
        var pageLine = frames.Select(f => f.Frame.GetPageLine()).OfType<PageLineFrame>().ToList();
        if (pageLine.Count > 0)
        {
            sb.AppendLine($"Page/line   {pageLine.Count} frames; directories {string.Join(", ", pageLine.Select(p => p.Index.ToString()).Distinct().Take(16))}");
            foreach (var m in PageLineMessageLayout.Default.Decode(pageLine).DistinctBy(x => (x.MessageId, x.Text)).Take(10))
                sb.AppendLine($"Message     {m}  (layout 3.0/3.1/3.2)");
        }
        sb.AppendLine($"Color frame {(last.Frame.ColorFrame ? "set — " + ColorFraming.Describe(last.Timecode) : "not set")}");
        sb.AppendLine($"Polarity    {(frames.All(f => f.Frame.PolarityCorrection) ? "corrected (even zeros in every codeword)" : "not corrected in some codewords")}");
        int issues = frames.Count(f => f.Issues.Count > 0);
        if (issues > 0) sb.AppendLine($"Issues      {issues} frame(s): {string.Join("; ", frames.SelectMany(f => f.Issues).Distinct().Take(3))}");
        return sb.ToString();
    }

    private static string List(IReadOnlyList<LtcDecodedFrame> frames, int sampleRate)
    {
        var sb = new StringBuilder();
        foreach (var (i, f) in frames.Take(5000).Index())
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{f.StartSample / sampleRate,9:0.000}s  {f.Timecode}  {(f.Direction == LtcDirection.Reverse ? "REV" : "FWD")} x{Math.Abs(f.Speed):0.000}  {f.Frame.UserBits.ToDisplayString()}  {f.Frame.BinaryGroupFlags.BitPattern()}{(f.Frame.ColorFrame ? " CF" : "")}  {UserBitsDescriber.Summary(f.Frame.UserBits, f.Frame.BinaryGroupFlags, f.Frame.Rate)}{(f.IsContinuous || i == 0 ? "" : "  ← jump")}");
        }
        if (frames.Count > 5000) sb.AppendLine($"… {frames.Count - 5000} more");
        return sb.ToString();
    }
}
