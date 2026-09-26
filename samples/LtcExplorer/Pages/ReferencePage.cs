using LinearTimecode.Describe;

namespace LtcExplorer.Pages;

/// <summary>Every option in ST 12-1 LTC and in the library, in plain language.</summary>
public class ReferencePage : ContentPage
{
    public ReferencePage()
    {
        Title = "Reference";
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 6 };
        stack.Children.Add(Ui.Caption("SMPTE ST 12-1:2014 — Time and Control Code, Linear Time Code application."));

        foreach (var group in LtcOptions.All)
        {
            stack.Children.Add(Ui.Heading(group.Title));
            stack.Children.Add(Ui.Caption(group.Summary));
            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(new GridLength(150)), new ColumnDefinition(new GridLength(220)), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 12,
                RowSpacing = 6,
            };
            for (int i = 0; i < group.Options.Count; i++)
            {
                var o = group.Options[i];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Add(new Label { Text = o.Code, FontFamily = Ui.Mono, FontSize = 13 }, 0, i);
                grid.Add(new Label { Text = o.Name, FontAttributes = FontAttributes.Bold, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap }, 1, i);
                grid.Add(new Label { Text = o.Description, FontSize = 13, LineBreakMode = LineBreakMode.WordWrap }, 2, i);
            }
            stack.Children.Add(Ui.Panel(grid));
        }

        Content = new ScrollView { Content = stack };
    }
}
