using LinearTimecode;
using LinearTimecode.BinaryGroups;
using LtcExplorer.Pages;

namespace LtcExplorer;

public class App : Application
{
    public App()
    {
        UserAppTheme = AppTheme.Unspecified;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var tabs = new TabbedPage { Title = "LTC Explorer" };
        tabs.Children.Add(new NavigationPage(new GeneratePage()) { Title = "Generate" });
        tabs.Children.Add(new NavigationPage(new ReadPage()) { Title = "Read" });
        tabs.Children.Add(new NavigationPage(new CodewordPage()) { Title = "Codeword" });
        tabs.Children.Add(new NavigationPage(new UserBitsPage()) { Title = "User Bits" });
        tabs.Children.Add(new NavigationPage(new CalculatorPage()) { Title = "Calculator" });
        tabs.Children.Add(new NavigationPage(new ReferencePage()) { Title = "Reference" });
        return new Window(tabs) { Title = "LTC Explorer", Width = 1100, Height = 820 };
    }
}

/// <summary>State shared between pages.</summary>
internal static class AppState
{
    /// <summary>Path of the last WAV written by the Generate page.</summary>
    public static string? LastGeneratedPath { get; set; }

    /// <summary>User bits (and optional ST 262 message frames) sent from the User Bits tab to the Generate tab.</summary>
    public static (UserBits Bits, BinaryGroupFlags Flags, IReadOnlyList<PageLineFrame>? Message)? PendingUserBits { get; set; }

    /// <summary>A running RP 169 auxiliary time address sent from the User Bits tab (advances one address per codeword).</summary>
    public static AuxiliaryTimeAddress? PendingAuxiliary { get; set; }
}

/// <summary>Shared styling and control helpers.</summary>
internal static class Ui
{
    public static string Mono => DeviceInfo.Platform == DevicePlatform.WinUI ? "Consolas"
        : DeviceInfo.Platform == DevicePlatform.Android ? "monospace"
        : "Menlo";

    public static Label MonoLabel(double size = 13) => new()
    {
        FontFamily = Mono,
        FontSize = size,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    public static Label Heading(string text) => new() { Text = text, FontSize = 18, FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 12, 0, 4) };

    public static Label Caption(string text) => new() { Text = text, FontSize = 13, Opacity = 0.75, LineBreakMode = LineBreakMode.WordWrap };

    /// <summary>A labelled row: caption on the left (fixed width), control on the right, optional hint below.</summary>
    public static View Field(string label, View control, string? hint = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(new GridLength(170)), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 10,
        };
        grid.Add(new Label { Text = label, VerticalOptions = LayoutOptions.Center, FontAttributes = FontAttributes.Bold }, 0, 0);
        grid.Add(control, 1, 0);
        if (hint is null) return grid;
        var hintLabel = Caption(hint);
        hintLabel.FontSize = 12;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.Add(hintLabel, 1, 1);
        return grid;
    }

    /// <summary>A picker listing every frame rate with its display name; optionally with an "Auto-detect" first entry.</summary>
    public static Picker RatePicker(LtcFrameRate initial, bool withAuto = false)
    {
        var items = LtcFrameRateExtensions.All.Select(r => $"{r.DisplayName()} fps").ToList();
        if (withAuto) items.Insert(0, "Auto-detect");
        var p = new Picker { ItemsSource = items };
        p.SelectedIndex = LtcFrameRateExtensions.All.ToList().IndexOf(initial) + (withAuto ? 1 : 0);
        return p;
    }

    /// <summary>Reads the rate from a picker made by <see cref="RatePicker"/>; null for "Auto-detect".</summary>
    public static LtcFrameRate? SelectedRate(Picker p, bool withAuto = false)
    {
        int i = p.SelectedIndex - (withAuto ? 1 : 0);
        return i < 0 ? null : LtcFrameRateExtensions.All[i];
    }

    /// <summary>A picker listing all eight binary group flag combinations (Table 1).</summary>
    public static Picker BgfPicker() => new()
    {
        ItemsSource = BinaryGroupFlagsExtensions.All.Select(f => $"{f.BitPattern()}  {f.DisplayName()}").ToList(),
        SelectedIndex = 0,
    };

    public static Switch Toggle(bool on = false) => new() { IsToggled = on, HorizontalOptions = LayoutOptions.Start };

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(0, 4, 8, 4) };
        b.Clicked += onClick;
        return b;
    }

    public static ScrollView Scroll(params View[] children)
    {
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 8 };
        foreach (var c in children) stack.Children.Add(c);
        return new ScrollView { Content = stack };
    }

    public static Border Panel(View content) => new() { Padding = 12, Content = content, StrokeThickness = 1, Stroke = Colors.Gray.WithAlpha(0.4f) };
}
