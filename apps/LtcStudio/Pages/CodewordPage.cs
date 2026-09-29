using LinearTimecode;
using LinearTimecode.Describe;

namespace LtcStudio.Pages;

/// <summary>
/// Build a codeword from its fields, or paste one (hex bytes / bit string), and see every bit explained.
/// </summary>
public class CodewordPage : ContentPage
{
    private readonly Editor _input = new() { FontFamily = Ui.Mono, HeightRequest = 70, AutoSize = EditorAutoSizeOption.TextChanges };
    private readonly Picker _rate = Ui.RatePicker(LtcFrameRate.Fps30);
    private readonly Entry _userBits = new() { Text = "00000000", FontFamily = Ui.Mono };
    private readonly Picker _bgf = Ui.BgfPicker();
    private readonly Switch _colorFrame = Ui.Toggle();
    private readonly Switch _polarity = Ui.Toggle(true);
    private readonly Label _explain = Ui.MonoLabel();
    private readonly Label _bitTable = Ui.MonoLabel(12);
    private readonly BitGridDrawable _grid = new();
    private readonly GraphicsView _gridView;

    public CodewordPage()
    {
        Title = "Codeword";
        _gridView = new GraphicsView { Drawable = _grid, HeightRequest = 230 };

        _input.TextChanged += (_, _) => Update();
        _rate.SelectedIndexChanged += (_, _) => Update();
        _userBits.TextChanged += (_, _) => Update();
        _bgf.SelectedIndexChanged += (_, _) => Update();
        _colorFrame.Toggled += (_, _) => Update();
        _polarity.Toggled += (_, _) => Update();

        var examples = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        AddExample(examples, "30 fps", "01:37:52:16", LtcFrameRate.Fps30);
        AddExample(examples, "29.97 DF", "00:10:00;00", LtcFrameRate.Fps29_97Drop);
        AddExample(examples, "25 fps", "10:00:00:00", LtcFrameRate.Fps25);
        AddExample(examples, "24 fps", "23:59:59:23", LtcFrameRate.Fps24);
        AddExample(examples, "59.94 DF pair", "01:23:45;13", LtcFrameRate.Fps59_94Drop);
        AddExample(examples, "Hex bytes", "06 01 02 0D 07 03 01 00 FC BF", LtcFrameRate.Fps30);
        AddExample(examples, "Bad BCD", "0F 00 00 00 00 00 00 00 FC BF", LtcFrameRate.Fps25);
        AddExample(examples, "Skipped DF address", "00:01:00;00", LtcFrameRate.Fps29_97Drop);

        Content = Ui.Scroll(
            Ui.Heading("Input"),
            Ui.Caption("A time code (HH:MM:SS:FF, ';' for drop-frame), 8 or 10 hex bytes (byte 0 = bits 0–7, LSB first), or 64/80 binary digits (bit 0 first)."),
            _input,
            examples,
            Ui.Field("Frame rate / layout", _rate, "Selects the 24/25/30-frame flag layout (Table 3) and the drop-frame variant."),
            Ui.Field("User bits (hex)", _userBits, "Applied when the input is a time code."),
            Ui.Field("Binary group flags", _bgf, "Applied when the input is a time code."),
            Ui.Field("Color frame flag", _colorFrame),
            Ui.Field("Polarity correction", _polarity),

            Ui.Heading("Bits"),
            Ui.Caption("teal = time address · purple = binary groups · red = flags · orange = sync word · grey = unassigned. Filled = 1."),
            Ui.Panel(_gridView),

            Ui.Heading("Explanation"),
            Ui.Panel(_explain),
            Ui.Heading("Bit table"),
            _bitTable);

        _input.Text = "01:00:00:00";
    }

    private void AddExample(FlexLayout host, string title, string text, LtcFrameRate rate)
    {
        var b = new Button { Text = title, Margin = new Thickness(0, 0, 8, 8), FontSize = 12 };
        b.Clicked += (_, _) =>
        {
            _rate.SelectedIndex = LtcFrameRateExtensions.All.ToList().IndexOf(rate);
            _input.Text = text;
        };
        host.Children.Add(b);
    }

    private void Update()
    {
        var rate = Ui.SelectedRate(_rate) ?? LtcFrameRate.Fps30;
        string text = (_input.Text ?? "").Trim();
        LtcCodeword? cw = null;
        string explanation;

        if (Timecode.TryParse(text, rate, out var tc, out string? tcError))
        {
            UserBits ub = UserBits.Empty;
            try { ub = UserBits.Parse(string.IsNullOrWhiteSpace(_userBits.Text) ? "0" : _userBits.Text); } catch (FormatException) { }
            var frame = new LtcFrame(tc)
            {
                UserBits = ub,
                BinaryGroupFlags = (BinaryGroupFlags)Math.Max(0, _bgf.SelectedIndex),
                ColorFrame = _colorFrame.IsToggled,
                PolarityCorrection = _polarity.IsToggled,
            };
            cw = frame.ToCodeword();
            explanation = LtcDescriber.Explain(cw.Value, tc.Rate);
            rate = tc.Rate;
        }
        else
        {
            try
            {
                cw = LtcCodeword.Parse(text);
                explanation = LtcDescriber.Explain(cw.Value, rate);
            }
            catch (FormatException ex)
            {
                explanation = text.Contains(':') || text.Contains(';') ? tcError ?? ex.Message : ex.Message;
            }
        }

        _explain.Text = explanation;
        if (cw is { } c)
        {
            _grid.Codeword = c;
            _grid.Layout = rate.Base();
            _bitTable.Text = LtcDescriber.BitTable(c, rate.Base());
        }
        _gridView.Invalidate();
    }
}
