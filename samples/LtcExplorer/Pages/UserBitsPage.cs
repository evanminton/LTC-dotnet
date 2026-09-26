using System.Globalization;
using LinearTimecode;
using LinearTimecode.BinaryGroups;
using LinearTimecode.Describe;

namespace LtcExplorer.Pages;

/// <summary>
/// Build the binary groups (user bits) in every mode the standards define — raw, ISO characters, SMPTE ST 309 date and
/// time zone, SMPTE ST 262 page/line — see them explained, and send them to the Generate tab.
/// </summary>
public class UserBitsPage : ContentPage
{
    private static readonly string[] Modes =
    [
        "Raw hex (BGF 000 / 010)",
        "Text — 4 ISO 646 characters (BGF 001)",
        "Date & time zone — SMPTE ST 309 (BGF 100 / 110)",
        "Page/line single frame — SMPTE ST 262 (BGF 101 / 111)",
        "Page/line control code — ST 262 page 15",
        "Page/line auxiliary time address — ST 262 pages 0–2",
        "Page/line message string — ST 262 (multi-frame)",
    ];

    private readonly Picker _mode = new() { ItemsSource = Modes, SelectedIndex = 2 };
    private readonly Switch _clock = Ui.Toggle();

    // raw / text
    private readonly Entry _hex = new() { Text = "00000000", FontFamily = Ui.Mono };
    private readonly Entry _text = new() { Text = "REEL", MaxLength = 4 };

    // ST 309
    private readonly DatePicker _date = new() { Date = DateTime.Today, Format = "yyyy-MM-dd" };
    private readonly Picker _tz = new() { ItemsSource = TimeZoneCode.All.Select(z => $"{z.Hex}  {z.Description}").ToList() };
    private readonly Switch _dst = Ui.Toggle();
    private readonly Switch _mjd = Ui.Toggle();
    private readonly Button _today;

    // ST 262
    private readonly Entry _index = new() { Text = "3.0", FontFamily = Ui.Mono };
    private readonly Entry _bytes = new() { Text = "41 42 43", FontFamily = Ui.Mono };
    private readonly Switch _checksum = Ui.Toggle();
    private readonly Entry _controlLine = new() { Text = "0", Keyboard = Keyboard.Numeric };
    private readonly Entry _command = new() { Text = "12 34", FontFamily = Ui.Mono };
    private readonly Entry _aux = new() { Text = "10:00:00:00", FontFamily = Ui.Mono };
    private readonly Picker _auxRate = Ui.RatePicker(LtcFrameRate.Fps25);
    private readonly Switch _auxCf = Ui.Toggle();
    private readonly Switch _auxRun = Ui.Toggle(true);
    private readonly Entry _message = new() { Text = "HELLO WORLD" };
    private readonly Entry _messageId = new() { Text = "1", Keyboard = Keyboard.Numeric };
    private readonly Entry _prefix = new() { Text = "3.0", FontFamily = Ui.Mono };
    private readonly Entry _msgIndex = new() { Text = "3.1", FontFamily = Ui.Mono };
    private readonly Entry _suffix = new() { Text = "3.2", FontFamily = Ui.Mono };

    private readonly VerticalStackLayout _fields = new() { Spacing = 8 };
    private readonly Label _result = Ui.MonoLabel();
    private readonly Label _status = Ui.Caption("");

    private UserBits _bits;
    private BinaryGroupFlags _flags;
    private IReadOnlyList<PageLineFrame>? _messageFrames;
    private string? _error; // set while the inputs don't produce valid user bits

    public UserBitsPage()
    {
        Title = "User Bits";
        var local = TimeZoneCode.FromOffset(DateTimeOffset.Now.Offset) ?? TimeZoneCode.Utc;
        _tz.SelectedIndex = local.Code;
        _dst.IsToggled = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now);
        _today = Ui.Button("Now (local clock)", (_, _) =>
        {
            _date.Date = DateTime.Today;
            _tz.SelectedIndex = (TimeZoneCode.FromOffset(DateTimeOffset.Now.Offset) ?? TimeZoneCode.Utc).Code;
            _dst.IsToggled = TimeZoneInfo.Local.IsDaylightSavingTime(DateTime.Now);
        });

        _mode.SelectedIndexChanged += (_, _) => { BuildFields(); Update(); };
        foreach (var e in new[] { _hex, _text, _index, _bytes, _controlLine, _command, _aux, _message, _messageId, _prefix, _msgIndex, _suffix }) e.TextChanged += (_, _) => Update();
        foreach (var s in new[] { _clock, _dst, _mjd, _checksum, _auxCf, _auxRun }) s.Toggled += (_, _) => Update();
        _tz.SelectedIndexChanged += (_, _) => Update();
        _auxRate.SelectedIndexChanged += (_, _) => Update();
        _date.DateSelected += (_, _) => Update();

        Content = Ui.Scroll(
            Ui.Heading("Mode"),
            Ui.Field("Binary group usage", _mode, "The binary group flags (BGF2 BGF1 BGF0, ST 12-1 Table 1) tell readers which of these the 32 user bits carry."),
            Ui.Field("Clock time", _clock, "Time address is referenced to a time-of-day clock: BGF 010 / 110 / 111 instead of 000 / 100 / 101."),
            _fields,
            Ui.Heading("Result"),
            Ui.Panel(_result),
            new HorizontalStackLayout { Children = { Ui.Button("Use in generator", (_, _) => UseInGenerator()) } },
            _status);

        BuildFields();
        Update();
    }

    private void BuildFields()
    {
        _fields.Children.Clear();
        _fields.Children.Add(Ui.Heading(Modes[Math.Max(0, _mode.SelectedIndex)]));
        switch (_mode.SelectedIndex)
        {
            case 0:
                _fields.Children.Add(Ui.Field("User bits (hex)", _hex, "Eight binary groups, group 8 first; any meaning (ST 12-1 §8.4.1, §8.4.3)."));
                break;
            case 1:
                _fields.Children.Add(Ui.Field("Text", _text, "Four 8-bit characters: 1st in groups 7/8 … 4th in groups 1/2 (ST 12-1 §8.4.2)."));
                break;
            case 2:
                _fields.Children.Add(Ui.Field("Date", _date, "YYMMDD: the local date. MJD: the UTC date."));
                _fields.Children.Add(Ui.Field("Time zone (Table 2)", _tz, "Code for the offset in use — in summer pick the daylight offset and set DST."));
                _fields.Children.Add(Ui.Field("Daylight saving (BG8.2)", _dst));
                _fields.Children.Add(Ui.Field("MJD format (BG8.3)", _mjd, "Off: YYMMDD and the time address is local time. On: Modified Julian Date and the time address is UTC."));
                _fields.Children.Add(_today);
                _fields.Children.Add(Ui.Caption("The generator steps the date at the 23:59:59:xx → 00:00:00:00 rollover (ST 309 §5.4)."));
                break;
            case 3:
                _fields.Children.Add(Ui.Field("Directory index", _index, "page.line — BG8 = page, BG7 = line. Pages 0–2 timing, 3–14 applications, 15 control."));
                _fields.Children.Add(Ui.Field("Bytes 1–3 (hex)", _bytes, "Byte n = BG 2n−1 (low nibble) + BG 2n (high nibble)."));
                _fields.Children.Add(Ui.Field("Checksum in byte 1", _checksum, "Two's complement of the sum of bytes 2–4 (ST 262 §3.3.3)."));
                break;
            case 4:
                _fields.Children.Add(Ui.Field("Line (page 15)", _controlLine));
                _fields.Children.Add(Ui.Field("Command (2 bytes hex)", _command, "Bytes 2–3; byte 1 becomes the checksum of bytes 2–4 (§3.4.1)."));
                break;
            case 5:
                _fields.Children.Add(Ui.Field("Auxiliary time", _aux, "SMPTE RP 169: laid out like the primary address; hours tens/units double as the directory page/line. ';' for drop-frame."));
                _fields.Children.Add(Ui.Field("Rate", _auxRate, "Frame count of the auxiliary address; drop-frame sets the DF flag in BG 2."));
                _fields.Children.Add(Ui.Field("Color frame flag", _auxCf, "BG 2 bit 3: color frame ID applied to the auxiliary address."));
                _fields.Children.Add(Ui.Field("Run with the frames", _auxRun, "Generator advances the auxiliary address by one each codeword instead of repeating it."));
                break;
            default:
                _fields.Children.Add(Ui.Field("Message text", _message, "ISO 646 text, three characters per message frame, null filled."));
                _fields.Children.Add(Ui.Field("Message ID", _messageId, "Prefix/suffix specifier byte 2; byte 3 = number of message frames."));
                _fields.Children.Add(Ui.Field("Prefix index", _prefix, "Each frame type has its own directory index, set by the application dialect."));
                _fields.Children.Add(Ui.Field("Message index", _msgIndex));
                _fields.Children.Add(Ui.Field("Suffix index", _suffix));
                break;
        }
    }

    private void Update()
    {
        _messageFrames = null;
        _error = null;
        bool clock = _clock.IsToggled;
        try
        {
            switch (_mode.SelectedIndex)
            {
                case 0:
                    _bits = UserBits.Parse(string.IsNullOrWhiteSpace(_hex.Text) ? "0" : _hex.Text);
                    _flags = clock ? BinaryGroupFlags.ClockTime : BinaryGroupFlags.Unspecified;
                    break;
                case 1:
                    _bits = UserBits.FromText(_text.Text ?? "");
                    _flags = BinaryGroupFlags.EightBitCharacters;
                    break;
                case 2:
                    var dtz = new DateTimeZone(DateOnly.FromDateTime((DateTime?)_date.Date ?? DateTime.Today), TimeZoneCode.All[Math.Max(0, _tz.SelectedIndex)],
                        _mjd.IsToggled ? DateFormat.ModifiedJulianDate : DateFormat.Yymmdd, _dst.IsToggled);
                    _bits = dtz.ToUserBits();
                    _flags = clock ? BinaryGroupFlags.ClockTimeDateTimeZone : BinaryGroupFlags.DateTimeZone;
                    break;
                case 3:
                    _bits = PageLineFrame.SingleFrame(DirectoryIndex.Parse(_index.Text ?? ""), B(_bytes.Text, 0), B(_bytes.Text, 1), B(_bytes.Text, 2), _checksum.IsToggled).ToUserBits();
                    _flags = PageLineFlags(clock);
                    break;
                case 4:
                    _bits = PageLineFrame.ControlCode(int.Parse(_controlLine.Text ?? "0", CultureInfo.InvariantCulture), B(_command.Text, 0), B(_command.Text, 1)).ToUserBits();
                    _flags = PageLineFlags(clock);
                    break;
                case 5:
                    _bits = PageLineFrame.ForAuxiliaryTimeAddress(Timecode.Parse(_aux.Text ?? "", Ui.SelectedRate(_auxRate) ?? LtcFrameRate.Fps25), _auxCf.IsToggled).ToUserBits();
                    _flags = PageLineFlags(clock);
                    break;
                default:
                    var layout = new PageLineMessageLayout(DirectoryIndex.Parse(_prefix.Text ?? ""), DirectoryIndex.Parse(_msgIndex.Text ?? ""), DirectoryIndex.Parse(_suffix.Text ?? ""));
                    _messageFrames = layout.EncodeText(_message.Text ?? "", byte.Parse(_messageId.Text ?? "0", CultureInfo.InvariantCulture));
                    _bits = _messageFrames[0].ToUserBits();
                    _flags = PageLineFlags(clock);
                    break;
            }

            var rate = Ui.SelectedRate(_auxRate) ?? LtcFrameRate.Fps25;
            string text = $"User bits   {_bits}   BGF {_flags.BitPattern()} ({_flags.DisplayName()})\n\n{UserBitsDescriber.Explain(_bits, _flags, rate)}";
            if (_messageFrames is { } mf)
            {
                text += $"\n{mf.Count} frames, sent one per codeword and repeated:\n";
                for (int i = 0; i < mf.Count; i++) text += $"  {i,3}  UB {mf[i].ToUserBits()}  {mf[i]}\n";
            }
            _result.Text = text;
            _status.Text = "";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            _error = ex.Message;
            _status.Text = ex.Message;
        }
    }

    private static BinaryGroupFlags PageLineFlags(bool clock) => clock ? BinaryGroupFlags.ClockTimePageLine : BinaryGroupFlags.PageLine;

    private static byte B(string? text, int index)
    {
        var parts = (text ?? "").Split([' ', ',', ':'], StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? byte.Parse(parts[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : (byte)0;
    }

    private void UseInGenerator()
    {
        if (_error is not null)
        {
            _status.Text = $"Nothing sent — fix this first: {_error}";
            return;
        }
        AppState.PendingUserBits = (_bits, _flags, _messageFrames);
        AppState.PendingAuxiliary = _mode.SelectedIndex == 5 && _auxRun.IsToggled
            ? AuxiliaryTimeAddress.FromUserBits(_bits, Ui.SelectedRate(_auxRate) ?? LtcFrameRate.Fps25)
            : null;
        _status.Text = "Sent to the Generate tab.";
    }
}
