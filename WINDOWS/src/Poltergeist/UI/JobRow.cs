using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Poltergeist.Core;

namespace Poltergeist;

/// <summary>One line of the Uploads list: name, progress, and Cancel / Copy link / Open / Retry.</summary>
sealed class JobRow : Border
{
    readonly UploadJob _job;
    readonly TextBlock _name = Theme.Label("", 13, Theme.Text, FontWeights.Medium);
    readonly TextBlock _state = Theme.Label("", 12, Theme.Muted);
    readonly ProgressBar _bar = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _detail = Theme.Label("", 12, Theme.Muted, null, true);
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal };

    public JobRow(UploadJob job)
    {
        _job = job;
        Padding = new Thickness(0, 6, 0, 6);
        _name.TextTrimming = TextTrimming.CharacterEllipsis;

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _name.VerticalAlignment = VerticalAlignment.Center;
        _state.VerticalAlignment = VerticalAlignment.Center;
        _state.Margin = new Thickness(10, 0, 10, 0);
        top.Children.Add(_name);
        Grid.SetColumn(_state, 1); top.Children.Add(_state);
        Grid.SetColumn(_buttons, 2); top.Children.Add(_buttons);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(_bar);
        stack.Children.Add(_detail);
        Child = stack;

        job.PropertyChanged += OnChanged;
        Unloaded += (_, _) => job.PropertyChanged -= OnChanged;
        Refresh();
    }

    void OnChanged(object? s, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    void Refresh()
    {
        _name.Text = _job.Name;
        _buttons.Children.Clear();
        _detail.Visibility = Visibility.Collapsed;
        _bar.Visibility = Visibility.Collapsed;
        _state.Text = "";

        switch (_job.State)
        {
            case JobState.Running:
                _bar.Visibility = Visibility.Visible;
                _bar.Value = _job.Progress;
                _state.Text = _job.StatusText;
                if (_job.IsUpload) _buttons.Children.Add(Small("Cancel", () => App.Current.Uploads.Cancel(_job), false));
                break;
            case JobState.Done:
                _buttons.Children.Add(Small("Copy link", () => App.Current.CopyText(_job.Url ?? ""), true));
                _buttons.Children.Add(Small("Open", () => App.OpenUrl(_job.Url ?? ""), false));
                _detail.Text = _job.Url ?? "";
                _detail.Visibility = Visibility.Visible;
                break;
            case JobState.Failed:
                if (_job.CanRetry) _buttons.Children.Add(Small("Retry", () => App.Current.Uploads.Retry(_job), true));
                _detail.Text = _job.Message ?? "Failed.";
                _detail.Foreground = Theme.Danger;
                _detail.Visibility = Visibility.Visible;
                break;
        }
    }

    static Button Small(string text, Action click, bool prominent)
    {
        var b = Theme.Pill(text, click, prominent);
        b.Margin = new Thickness(6, 0, 0, 0);
        b.FontSize = 12;
        b.Padding = new Thickness(12, 5, 12, 5);
        return b;
    }
}
