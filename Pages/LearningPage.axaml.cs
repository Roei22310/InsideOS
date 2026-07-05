using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using InsideOS.Services.Learning;

namespace InsideOS.Pages;

/// <summary>
/// Learning Journey home: overall progress, the lesson library and a details
/// view per lesson. Entirely data-driven — cards are built from the
/// <see cref="LessonManager"/> catalog, so future lessons appear by adding
/// data only. Slots beyond the authored lessons render locked.
/// </summary>
public partial class LearningPage : UserControl
{
    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4D9FFF"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush SuccessChipBg = new SolidColorBrush(Color.Parse("#143FBF7F"));
    private static readonly IBrush SuccessChipBorder = new SolidColorBrush(Color.Parse("#333FBF7F"));
    private static readonly IBrush NeutralChipBg = new SolidColorBrush(Color.Parse("#0DFFFFFF"));
    private static readonly IBrush NeutralChipBorder = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly TransformOperations DetailsEnter = TransformOperations.Parse("translateY(10px)");
    private static readonly TransformOperations DetailsRest = TransformOperations.Parse("translateY(0px)");

    private readonly LessonManager _lessons;
    private readonly Action<Lesson> _startLesson;
    private Lesson? _detailLesson;

    public LearningPage(LessonManager lessons, Action<Lesson> startLesson)
    {
        InitializeComponent();
        _lessons = lessons;
        _startLesson = startLesson;
        _lessons.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh(); // completion can change while the page is hidden (e.g. onboarding)
    }

    private void Refresh()
    {
        ProgressText.Text = $"{_lessons.CompletedCount} / {_lessons.PlannedLessonCount} lessons completed";
        ProgressPercentText.Text = $"{_lessons.ProgressPercent}%";
        JourneyBar.Value = _lessons.ProgressPercent;

        LessonCards.Children.Clear();
        for (int number = 1; number <= _lessons.PlannedLessonCount; number++)
        {
            var lesson = _lessons.FindByNumber(number);
            LessonCards.Children.Add(lesson is null ? BuildLockedCard(number) : BuildLessonCard(lesson));
        }

        if (_detailLesson is not null)
            UpdateDetailStatus(_detailLesson);
    }

    // ---- lesson library cards ----

    private Control BuildLessonCard(Lesson lesson)
    {
        bool done = _lessons.IsCompleted(lesson);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };

        var chip = BuildNumberChip(lesson.Number, done);
        Grid.SetColumn(chip, 0);
        grid.Children.Add(chip);

        var textCol = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        textCol.Children.Add(new TextBlock
        {
            Text = lesson.Title,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
        });
        textCol.Children.Add(new TextBlock
        {
            Text = $"{lesson.Duration} · {DifficultyLabel(lesson.Difficulty)}",
            FontSize = 11.5,
            Foreground = MutedText,
        });
        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        var badge = done
            ? BuildBadge("Completed", SuccessBrush, SuccessChipBg, SuccessChipBorder)
            : BuildBadge("Not started", MutedText, NeutralChipBg, NeutralChipBorder);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Foreground = MutedText,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(chevron, 3);
        grid.Children.Add(chevron);

        var card = new Border { Child = grid };
        card.Classes.Add("lessonCard");
        card.Classes.Add("open");
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ShowDetails(lesson);
        };
        return card;
    }

    private Control BuildLockedCard(int number)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var chip = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = NeutralChipBg,
            BorderBrush = NeutralChipBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Viewbox
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = BuildIcon("IconLock", MutedText, 1.8),
            },
        };
        Grid.SetColumn(chip, 0);
        grid.Children.Add(chip);

        var textCol = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        textCol.Children.Add(new TextBlock
        {
            Text = $"Lesson {number}",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = SecondaryText,
        });
        textCol.Children.Add(new TextBlock
        {
            Text = "This lesson will become available in a future update.",
            FontSize = 11.5,
            Foreground = MutedText,
        });
        Grid.SetColumn(textCol, 1);
        grid.Children.Add(textCol);

        var badge = BuildBadge("Locked", MutedText, NeutralChipBg, NeutralChipBorder);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var card = new Border { Child = grid };
        card.Classes.Add("lessonCard");
        card.Classes.Add("locked");
        return card;
    }

    private Control BuildNumberChip(int number, bool done)
    {
        Control content = done
            ? new Viewbox
            {
                Width = 13,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = BuildIcon("IconCheck", SuccessBrush, 2.6),
            }
            : new TextBlock
            {
                Text = number.ToString(),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = SecondaryText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            Background = done ? SuccessChipBg : NeutralChipBg,
            BorderBrush = done ? SuccessChipBorder : NeutralChipBorder,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    private static Border BuildBadge(string text, IBrush foreground, IBrush background, IBrush border) => new()
    {
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(99),
        Padding = new Thickness(9, 3, 9, 4),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeight.Medium,
            Foreground = foreground,
        },
    };

    private static Path BuildIcon(string resourceKey, IBrush stroke, double thickness) => new()
    {
        Width = 24,
        Height = 24,
        Data = (Geometry)Application.Current!.FindResource(resourceKey)!,
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
    };

    // ---- details view ----

    internal void ShowDetails(Lesson lesson)
    {
        _detailLesson = lesson;
        DetailKicker.Text = $"LESSON {lesson.Number}";
        DetailTitle.Text = lesson.Title;
        DetailDuration.Text = lesson.Duration;
        DetailDifficulty.Text = DifficultyLabel(lesson.Difficulty);
        DetailDescription.Text = lesson.Description;

        ObjectivesList.Children.Clear();
        foreach (var objective in lesson.Objectives)
            ObjectivesList.Children.Add(BuildObjectiveRow(objective));

        UpdateDetailStatus(lesson);
        SwapTo(DetailsView, LibraryView);
    }

    private void UpdateDetailStatus(Lesson lesson)
    {
        bool done = _lessons.IsCompleted(lesson);
        DetailStatusChip.Background = done ? SuccessChipBg : NeutralChipBg;
        DetailStatusChip.BorderBrush = done ? SuccessChipBorder : NeutralChipBorder;
        DetailStatusText.Text = done ? "Completed" : "Not started";
        DetailStatusText.Foreground = done ? SuccessBrush : MutedText;
        DetailReplayHint.IsVisible = done;
    }

    private static Control BuildObjectiveRow(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = AccentBrush,
            Margin = new Thickness(1, 7, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(dot, 0);
        row.Children.Add(dot);

        var body = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryText,
            Margin = new Thickness(11, 0, 0, 0),
        };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);

        return row;
    }

    // ---- view switching (fade + gentle rise, mirrors the app's transitions) ----

    private void SwapTo(Control show, Control hide)
    {
        hide.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            hide.IsVisible = false;
            show.IsVisible = true;
            if (ReferenceEquals(show, DetailsView))
                DetailsView.RenderTransform = DetailsEnter;
            DispatcherTimer.RunOnce(() =>
            {
                show.Opacity = 1;
                if (ReferenceEquals(show, DetailsView))
                    DetailsView.RenderTransform = DetailsRest;
            }, TimeSpan.FromMilliseconds(25));
        }, TimeSpan.FromMilliseconds(150));
    }

    private void OnBack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _detailLesson = null;
        SwapTo(LibraryView, DetailsView);
    }

    private void OnStartLesson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_detailLesson is { } lesson)
            _startLesson(lesson);
    }

    private static string DifficultyLabel(LessonDifficulty difficulty) => difficulty switch
    {
        LessonDifficulty.Beginner => "Beginner",
        LessonDifficulty.Intermediate => "Intermediate",
        _ => "Advanced",
    };
}
