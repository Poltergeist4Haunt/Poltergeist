using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Poltergeist;

/// <summary>A pill with a main action ("Upload") and a ▾ menu of alternatives (copy to clipboard…).</summary>
sealed class SplitButton : Border
{
    public SplitButton(string title, Action primary, params (string Title, Action Action)[] choices)
    {
        CornerRadius = new CornerRadius(17);
        Background = Theme.AccentSoft;
        BorderBrush = Theme.AccentLine;
        BorderThickness = new Thickness(1);
        ClipToBounds = true;

        var main = new Button { Style = Theme.Style("IconBtn"), Width = double.NaN, Height = 32, FontFamily = Theme.UiFont, FontSize = 13, Padding = new Thickness(18, 0, 14, 0), Foreground = Theme.Accent };
        main.Content = new TextBlock { Text = title, FontWeight = FontWeights.Medium, Foreground = Theme.Accent, VerticalAlignment = VerticalAlignment.Center };
        main.Template = FlatTemplate(new Thickness(18, 0, 14, 0));
        main.Click += (_, _) => primary();

        var arrow = new Button { Width = 32, Height = 32, Foreground = Theme.Accent, ToolTip = "More ways to finish", Focusable = false, Cursor = Cursors.Hand };
        arrow.Content = new TextBlock { Text = "", FontFamily = Theme.IconFont, FontSize = 10, Foreground = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        arrow.Template = FlatTemplate(new Thickness(0));
        var menu = new ContextMenu { PlacementTarget = arrow, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var (t, a) in choices)
        {
            var item = new MenuItem { Header = t };
            var act = a;
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        arrow.Click += (_, _) => { menu.PlacementTarget = arrow; menu.IsOpen = true; };

        var sep = new Border { Width = 1, Height = 18, Background = Theme.AccentLine };
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { main, sep, arrow } };
    }

    static ControlTemplate FlatTemplate(Thickness padding)
    {
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border), "bd");
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        bd.SetValue(Border.PaddingProperty, padding);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x22, 0xf2, 0x7d, 0xb3)), "bd"));
        tpl.Triggers.Add(hover);
        return tpl;
    }
}
