using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InsideOS.Services.Learning;

namespace InsideOS.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush ChipBg = new SolidColorBrush(Color.Parse("#12FFFFFF"));
    private static readonly IBrush ChipBorder = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#EDEFF4"));

    private readonly LessonManager _lessons;
    private readonly Action _restartOnboarding;

    public SettingsPage(LessonManager lessons, Action restartOnboarding)
    {
        InitializeComponent();
        _lessons = lessons;
        _restartOnboarding = restartOnboarding;
        _lessons.Changed += () => Dispatcher.UIThread.Post(UpdateProgress);
        UpdateProgress();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateProgress(); // completion can change while the page is hidden
    }

    private void UpdateProgress()
    {
        int percent = _lessons.ProgressPercent;
        ProgressSummary.Text =
            $"Lesson {Math.Min(_lessons.CompletedCount + 1, _lessons.PlannedLessonCount)} of {_lessons.PlannedLessonCount} · Progress {percent}%";
        ProgressBarControl.Value = percent;

        LessonList.Children.Clear();
        foreach (var lesson in _lessons.Lessons)
        {
            bool done = _lessons.IsCompleted(lesson);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var numberChip = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = ChipBg,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = lesson.Number.ToString(),
                    FontSize = 11,
                    Foreground = PendingBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(numberChip, 0);
            row.Children.Add(numberChip);

            var title = new TextBlock
            {
                Text = lesson.Title,
                FontSize = 12.5,
                Foreground = TitleBrush,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);

            var status = new TextBlock
            {
                Text = done ? "Completed" : "Not started",
                FontSize = 11.5,
                Foreground = done ? DoneBrush : PendingBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);

            LessonList.Children.Add(row);
        }
    }

    private void OnRestartIntroduction(object? sender, RoutedEventArgs e) => _restartOnboarding();
}
