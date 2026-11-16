using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LiveRecorder;

public sealed class PreviewTransportControls : MediaTransportControls
{
    private readonly List<(FrameworkElement Button, long Token)> _buttonVisibilityHandlers = [];
    private TextBlock? _totalTime;
    private TimeSpan _duration;

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            _duration = value;
            if (_totalTime is not null)
                _totalTime.Text = FormatDuration();
        }
    }

    protected override void OnApplyTemplate()
    {
        foreach ((FrameworkElement button, long token) in _buttonVisibilityHandlers)
            button.UnregisterPropertyChangedCallback(VisibilityProperty, token);
        _buttonVisibilityHandlers.Clear();
        base.OnApplyTemplate();
        _totalTime = null;

        // Reuse the WinUI template parts so playback, seeking, volume, and casting
        // keep their built-in behavior.
        if (GetTemplateChild("ControlPanelGrid") is not Grid panel ||
            GetTemplateChild("MediaTransportControls_Timeline_Grid") is not Grid timeline ||
            GetTemplateChild("MediaTransportControls_Command_Border") is not Border commands ||
            GetTemplateChild("MediaControlsCommandBar") is not CommandBar commandBar ||
            GetTemplateChild("ProgressSlider") is not Slider progress ||
            GetTemplateChild("TimeTextGrid") is not Grid time ||
            GetTemplateChild("TimeElapsedElement") is not TextBlock elapsed ||
            GetTemplateChild("TimeRemainingElement") is not TextBlock remaining)
            return;

        Grid.SetColumn(commands, 0);
        Grid.SetRow(commands, 1);
        commands.VerticalAlignment = VerticalAlignment.Center;
        commandBar.HorizontalAlignment = HorizontalAlignment.Left;
        commandBar.Margin = new Thickness(4, 0, 0, 0);
        progress.Margin = new Thickness(3, 2, 7, 1);
        // WinUI resizes these spacers after the template is applied.
        if (GetTemplateChild("LeftSeparator") is AppBarSeparator left)
            commandBar.PrimaryCommands.Remove(left);
        if (GetTemplateChild("RightSeparator") is AppBarSeparator right)
            commandBar.PrimaryCommands.Remove(right);
        if (GetTemplateChild("PlayPauseButton") is AppBarButton playPause &&
            commandBar.PrimaryCommands.Remove(playPause))
            commandBar.PrimaryCommands.Insert(0, playPause);

        // The default bar centers its buttons across a full row. The single-row
        // layout only reserves space for controls that the current source shows.
        FrameworkElement[] buttons = commandBar.PrimaryCommands.OfType<FrameworkElement>()
            .Where(button => button is not AppBarSeparator).ToArray();
        foreach (FrameworkElement button in buttons)
        {
            long token = button.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => FitCommands());
            _buttonVisibilityHandlers.Add((button, token));
        }
        FitCommands();

        timeline.Children.Remove(time);
        time.Children.Remove(elapsed);
        remaining.Visibility = Visibility.Collapsed;
        elapsed.VerticalAlignment = VerticalAlignment.Center;
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        times.Children.Add(elapsed);
        times.Children.Add(new TextBlock { Text = "/", Style = elapsed.Style, VerticalAlignment = VerticalAlignment.Center });
        _totalTime = new TextBlock { Text = FormatDuration(), Style = elapsed.Style, VerticalAlignment = VerticalAlignment.Center };
        times.Children.Add(_totalTime);
        time.Children.Add(times);
        time.Height = double.NaN;
        time.Margin = new Thickness(8, 0, 14, 0);
        time.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(time, 2);
        Grid.SetRow(time, 1);
        panel.Children.Add(time);

        void FitCommands() => commands.Width = commandBar.Margin.Left + buttons
            .Where(button => button.Visibility == Visibility.Visible)
            .Sum(button => double.IsNaN(button.Width) ? Math.Max(40, button.MinWidth) : button.Width);
    }

    private string FormatDuration() => _duration > TimeSpan.Zero && _duration != TimeSpan.MaxValue
        ? $"{(long)_duration.TotalHours}:{_duration.Minutes:00}:{_duration.Seconds:00}"
        : "--:--";
}
