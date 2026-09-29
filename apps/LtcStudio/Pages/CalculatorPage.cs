using System.Globalization;
using System.Text;
using LinearTimecode;

namespace LtcStudio.Pages;

/// <summary>Time code arithmetic: add, difference, conversion between rates, and facts about an address.</summary>
public class CalculatorPage : ContentPage
{
    private readonly Entry _a = new() { Text = "00:09:59;29", FontFamily = Ui.Mono };
    private readonly Entry _b = new() { Text = "01:00:00;00", FontFamily = Ui.Mono };
    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps29_97Drop);
    private readonly Entry _frames = new() { Text = "1", Keyboard = Keyboard.Numeric };
    private readonly Picker _to = Ui.RatePicker(LtcFrameRate.Fps25);
    private readonly Label _out = Ui.MonoLabel();

    public CalculatorPage()
    {
        Title = "Calculator";
        foreach (var e in new[] { _a, _b, _frames }) e.TextChanged += (_, _) => Update();
        _rate.SelectedIndexChanged += (_, _) => Update();
        _to.SelectedIndexChanged += (_, _) => Update();

        Content = Ui.Scroll(
            Ui.Heading("Inputs"),
            Ui.Field("Time code A", _a, "';' before the frames selects drop-frame at 29.97/59.94."),
            Ui.Field("Time code B", _b),
            Ui.Field("Frame rate", _rate),
            Ui.Field("Add to A (addresses)", _frames, "Negative to subtract. Wraps at 24 hours."),
            Ui.Field("Convert A to", _to, "Same real elapsed time, nearest address."),
            Ui.Heading("Results"),
            Ui.Panel(_out));
        Update();
    }

    private void Update()
    {
        var rate = Ui.SelectedRate(_rate) ?? LtcFrameRate.Fps30;
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        if (!Timecode.TryParse(_a.Text, rate, out var a, out string? errA)) { _out.Text = $"A: {errA}"; return; }
        rate = a.Rate;

        sb.AppendLine($"A               {a}  ({rate.DisplayName()} fps)");
        sb.AppendLine($"Address #       {a.TotalFrames} of {rate.AddressesPerDay()} per day");
        if (rate.IsFramePair()) sb.AppendLine($"Video frames    {a.VideoFrameIndex} and {a.VideoFrameIndex + 1}");
        var real = a.ToTimeSpan(); // can pass 24 h (non-drop NTSC rates), so show whole hours rather than wrap
        sb.AppendLine(inv, $"Real time       {(int)real.TotalHours:00}:{real:mm\\:ss\\.fffffff}");
        sb.AppendLine($"Next / previous {a.Next()} / {a.Previous()}");
        sb.AppendLine($"Color framing   {ColorFraming.Describe(a)}");

        if (long.TryParse(_frames.Text, NumberStyles.AllowLeadingSign, inv, out long n))
            sb.AppendLine($"A + {n,-11} {a.AddFrames(n)}");

        var to = Ui.SelectedRate(_to) ?? LtcFrameRate.Fps25;
        sb.AppendLine($"A at {to.DisplayName(),-10} {a.ConvertTo(to)}");

        sb.AppendLine();
        if (Timecode.TryParse(_b.Text, rate, out var b, out string? errB))
        {
            sb.AppendLine($"B               {b}  ({b.Rate.DisplayName()} fps)");
            if (b.Rate == a.Rate)
            {
                int d = b.TotalFrames - a.TotalFrames;
                sb.AppendLine($"B − A           {d} addresses ({Timecode.FromTotalFrames(Math.Abs(d), b.Rate)}{(d < 0 ? " before A" : "")})");
            }
            sb.AppendLine(inv, $"Real time B − A {(b.ToTimeSpan() - a.ToTimeSpan()).TotalSeconds:0.######} s");
        }
        else
        {
            sb.AppendLine($"B: {errB}");
        }

        sb.AppendLine();
        sb.AppendLine(inv, $"Codeword        {rate.CodewordDuration().TotalMilliseconds:0.####} ms   bit {rate.BitPeriodMicroseconds():0.##} µs   {rate.BitRate():0.###} bit/s");
        _out.Text = sb.ToString();
    }
}
