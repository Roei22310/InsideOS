using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using InsideOS.Services.Insights;
using InsideOS.Services.Narration;
using InsideOS.Services.Timeline;

namespace InsideOS.Pages;

/// <summary>
/// System Story Timeline: renders the grouped stories produced by
/// <see cref="SystemStoryService"/> as timeline cards on a connector line,
/// with category filters and process search. Rendering pauses while the page
/// is hidden and reloads on attach, so the timeline costs nothing off-screen.
/// </summary>
public partial class TimelinePage : UserControl
{
    private const int MaxRows = 80;

    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush HairlineBrush = new SolidColorBrush(Color.Parse("#242933"));
    private static readonly IBrush DotRim = new SolidColorBrush(Color.Parse("#16181E"));

    private static readonly Color CpuColor = Color.Parse("#4D9FFF");
    private static readonly Color MemoryColor = Color.Parse("#7A5CFF");
    private static readonly Color DiskColor = Color.Parse("#E5A455");
    private static readonly Color NetworkColor = Color.Parse("#3FBF7F");
    private static readonly Color ProcessColor = Color.Parse("#9AA3B4");
    private static readonly Color LearningColor = Color.Parse("#F27DA8");

    private static readonly Color[] AvatarPalette =
    {
        Color.Parse("#4D9FFF"), Color.Parse("#7A5CFF"), Color.Parse("#3FBF7F"),
        Color.Parse("#E5A455"), Color.Parse("#45C4D6"), Color.Parse("#F27DA8"),
        Color.Parse("#B48CFF"), Color.Parse("#E56262"),
    };

    private static readonly TransformOperations RowEnter = TransformOperations.Parse("translateY(-8px)");
    private static readonly TransformOperations RowRest = TransformOperations.Parse("translateY(0px)");

    private readonly SystemStoryService _story;
    private readonly InsightService _insights;
    private readonly Action<TimelineStorySnapshot> _openStory;
    private readonly Dictionary<int, Grid> _rows = new();
    private readonly Dictionary<int, TimelineStorySnapshot> _snapshots = new();
    private readonly Dictionary<string, (Border Card, NarratedActivity Shown)> _insightCards = new();
    private string _filter = "all";
    private string _search = "";
    private string _summaryKey = "";
    private bool _attached;

    public TimelinePage(SystemStoryService story, InsightService insights, Action<TimelineStorySnapshot> openStory)
    {
        InitializeComponent();
        _story = story;
        _insights = insights;
        _openStory = openStory;
        _story.StoryChanged += snapshot => Dispatcher.UIThread.Post(() => OnStoryChanged(snapshot));
        _story.StoriesReset += () => Dispatcher.UIThread.Post(() => { if (_attached) Reload(); });
        _insights.InsightsUpdated += list => Dispatcher.UIThread.Post(() => ApplyInsights(list));
        _insights.SummaryUpdated += summary => Dispatcher.UIThread.Post(() => ApplySummary(summary));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Reload();
        ApplyInsights(_insights.CurrentInsights);
        ApplySummary(_insights.CurrentSummary);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false; // stop building cards while hidden; Reload() catches up
    }

    private void Reload()
    {
        _rows.Clear();
        _snapshots.Clear();
        StoryList.Children.Clear();
        foreach (var snapshot in _story.GetStories()) // oldest → newest
        {
            _snapshots[snapshot.StoryId] = snapshot;
            InsertRow(snapshot, animate: false);
        }
        UpdateEmptyState();
    }

    private void OnStoryChanged(TimelineStorySnapshot snapshot)
    {
        _snapshots[snapshot.StoryId] = snapshot;
        if (!_attached)
            return;
        if (_rows.TryGetValue(snapshot.StoryId, out var row))
        {
            row.Children.Clear();
            PopulateRow(row, snapshot);
            row.IsVisible = Matches(snapshot);
        }
        else
        {
            InsertRow(snapshot, animate: true);
        }
        UpdateEmptyState();
    }

    private void InsertRow(TimelineStorySnapshot snapshot, bool animate)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("52,26,*"),
            Tag = snapshot.StoryId,
        };
        PopulateRow(row, snapshot);
        row.IsVisible = Matches(snapshot);
        _rows[snapshot.StoryId] = row;
        StoryList.Children.Insert(0, row); // newest first

        if (StoryList.Children.Count > MaxRows)
        {
            var oldest = StoryList.Children[^1];
            StoryList.Children.RemoveAt(StoryList.Children.Count - 1);
            if (oldest is Grid { Tag: int oldId })
            {
                _rows.Remove(oldId);
                _snapshots.Remove(oldId);
            }
        }

        if (animate)
        {
            row.Opacity = 0;
            row.RenderTransform = RowEnter;
            row.Transitions =
            [
                new Avalonia.Animation.DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(280),
                },
                new Avalonia.Animation.TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(320),
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            ];
            DispatcherTimer.RunOnce(() =>
            {
                row.Opacity = 1;
                row.RenderTransform = RowRest;
            }, TimeSpan.FromMilliseconds(30));
        }
    }

    private void PopulateRow(Grid row, TimelineStorySnapshot snapshot)
    {
        // Time column
        var time = new TextBlock
        {
            Text = snapshot.StartTime.ToString("HH:mm"),
            FontSize = 11.5,
            Foreground = MutedText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 15, 0, 0),
        };
        Grid.SetColumn(time, 0);
        row.Children.Add(time);

        // Connector line + severity dot
        var connector = new Panel();
        connector.Children.Add(new Rectangle
        {
            Width = 2,
            Fill = HairlineBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        });
        connector.Children.Add(new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(SeverityColor(snapshot.Severity)),
            BorderBrush = DotRim,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0),
        });
        Grid.SetColumn(connector, 1);
        row.Children.Add(connector);

        // Card
        var card = new Border
        {
            Child = BuildCardContent(snapshot),
            Margin = new Thickness(10, 0, 0, 14),
        };
        card.Classes.Add("storyCard");
        int id = snapshot.StoryId;
        card.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (_snapshots.TryGetValue(id, out var current))
                _openStory(current);
        };
        Grid.SetColumn(card, 2);
        row.Children.Add(card);
    }

    private Control BuildCardContent(TimelineStorySnapshot snapshot)
    {
        var stack = new StackPanel { Spacing = 8 };

        // Header: avatar · process name · category badges
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var avatar = BuildAvatar(snapshot);
        Grid.SetColumn(avatar, 0);
        header.Children.Add(avatar);

        var name = new TextBlock
        {
            Text = snapshot.ProcessName,
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
            Margin = new Thickness(11, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var category in snapshot.Categories)
            badges.Children.Add(BuildBadge(category));
        Grid.SetColumn(badges, 2);
        header.Children.Add(badges);

        stack.Children.Add(header);

        if (snapshot.Events.Count == 1)
        {
            var evt = snapshot.Events[0];
            stack.Children.Add(new TextBlock
            {
                Text = evt.Title,
                FontSize = 12.5,
                FontWeight = FontWeight.Medium,
                Foreground = SecondaryText,
            });
            stack.Children.Add(new TextBlock
            {
                Text = evt.Detail,
                FontSize = 11.5,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = MutedText,
            });
        }
        else
        {
            // Grouped story: the sequence of steps…
            var steps = new StackPanel { Spacing = 6, Margin = new Thickness(2, 0, 0, 0) };
            foreach (var evt in snapshot.Events)
            {
                var stepRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                var dot = new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = new SolidColorBrush(CategoryColor(evt.Category)),
                    Margin = new Thickness(1, 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetColumn(dot, 0);
                stepRow.Children.Add(dot);

                var title = new TextBlock
                {
                    Text = evt.Title,
                    FontSize = 12,
                    Foreground = SecondaryText,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(9, 0, 10, 0),
                };
                Grid.SetColumn(title, 1);
                stepRow.Children.Add(title);

                var stepTime = new TextBlock
                {
                    Text = evt.Time.ToString("HH:mm:ss"),
                    FontSize = 10.5,
                    Foreground = MutedText,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(stepTime, 2);
                stepRow.Children.Add(stepTime);

                steps.Children.Add(stepRow);
            }
            stack.Children.Add(steps);

            // …and the combined likely explanation.
            if (snapshot.Explanation is { } explanation)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = HairlineBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                stack.Children.Add(new TextBlock
                {
                    Text = "LIKELY EXPLANATION",
                    FontSize = 9.5,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 1.1,
                    Foreground = MutedText,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = explanation,
                    FontSize = 12,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = SecondaryText,
                });
            }
        }

        return stack;
    }

    private Control BuildAvatar(TimelineStorySnapshot snapshot)
    {
        Control content;
        Color tint;
        if (snapshot.Pid < 0)
        {
            // Learning story: book glyph instead of a process monogram.
            tint = LearningColor;
            content = new Viewbox
            {
                Width = 13,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Path
                {
                    Width = 24,
                    Height = 24,
                    Data = (Geometry)Application.Current!.FindResource("IconBook")!,
                    Stroke = new SolidColorBrush(tint),
                    StrokeThickness = 1.8,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                },
            };
        }
        else
        {
            tint = AvatarColor(snapshot.ProcessName);
            content = new TextBlock
            {
                Text = snapshot.ProcessName.Length > 0
                    ? char.ToUpperInvariant(snapshot.ProcessName[0]).ToString()
                    : "?",
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(tint),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(tint, 0.14),
            BorderBrush = new SolidColorBrush(tint, 0.32),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    private static Control BuildBadge(TimelineCategory category)
    {
        var color = CategoryColor(category);
        return new Border
        {
            Background = new SolidColorBrush(color, 0.13),
            BorderBrush = new SolidColorBrush(color, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(8, 2, 8, 3),
            Child = new TextBlock
            {
                Text = BadgeLabel(category),
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private static string BadgeLabel(TimelineCategory category) => category switch
    {
        TimelineCategory.Cpu => "CPU",
        TimelineCategory.Memory => "Memory",
        TimelineCategory.Disk => "Disk",
        TimelineCategory.Network => "Network",
        TimelineCategory.Process => "Process",
        _ => "Learning",
    };

    private static Color CategoryColor(TimelineCategory category) => category switch
    {
        TimelineCategory.Cpu => CpuColor,
        TimelineCategory.Memory => MemoryColor,
        TimelineCategory.Disk => DiskColor,
        TimelineCategory.Network => NetworkColor,
        TimelineCategory.Process => ProcessColor,
        _ => LearningColor,
    };

    private static Color SeverityColor(TimelineSeverity severity) => severity switch
    {
        TimelineSeverity.High => Color.Parse("#E56262"),
        TimelineSeverity.Notice => Color.Parse("#E5A455"),
        _ => Color.Parse("#4D9FFF"),
    };

    private static Color AvatarColor(string name)
    {
        int hash = 0;
        foreach (char c in name)
            hash = hash * 31 + c;
        return AvatarPalette[Math.Abs(hash) % AvatarPalette.Length];
    }

    // ---- filters & search ----

    private void OnFilterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag } pill)
            return;
        e.Handled = true;
        _filter = tag;
        foreach (var child in FilterBar.Children)
            if (child is Border other)
                other.Classes.Remove("selected");
        pill.Classes.Add("selected");
        ApplyFilters();
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text?.Trim() ?? "";
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        foreach (var child in StoryList.Children)
        {
            if (child is Grid { Tag: int id } row && _snapshots.TryGetValue(id, out var snapshot))
                row.IsVisible = Matches(snapshot);
        }
        UpdateEmptyState();
    }

    private bool Matches(TimelineStorySnapshot snapshot)
    {
        if (_search.Length > 0
            && !snapshot.ProcessName.Contains(_search, StringComparison.OrdinalIgnoreCase))
            return false;
        return _filter switch
        {
            "cpu" => snapshot.Categories.Contains(TimelineCategory.Cpu),
            "memory" => snapshot.Categories.Contains(TimelineCategory.Memory),
            "disk" => snapshot.Categories.Contains(TimelineCategory.Disk),
            "network" => snapshot.Categories.Contains(TimelineCategory.Network),
            "processes" => snapshot.Categories.Contains(TimelineCategory.Process),
            "learning" => snapshot.Categories.Contains(TimelineCategory.Learning),
            _ => true,
        };
    }

    // ---- System Intelligence panel ----

    private void ApplySummary(DailySummary? summary)
    {
        if (!_attached || summary is null)
            return;
        string key = string.Join("\n", summary.Lines);
        if (key == _summaryKey)
            return; // unchanged — leave the text alone
        _summaryKey = key;
        SummaryLines.Children.Clear();
        foreach (var line in summary.Lines)
        {
            SummaryLines.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 11.5,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SecondaryText,
            });
        }
    }

    private void ApplyInsights(IReadOnlyList<NarratedActivity> insights)
    {
        if (!_attached)
            return;

        // Remove cards whose insight disappeared (fade out, then detach).
        var liveIds = insights.Select(i => i.Id).ToHashSet();
        foreach (var (id, entry) in _insightCards.Where(kv => !liveIds.Contains(kv.Key)).ToList())
        {
            _insightCards.Remove(id);
            var card = entry.Card;
            card.Opacity = 0;
            DispatcherTimer.RunOnce(() => InsightList.Children.Remove(card), TimeSpan.FromMilliseconds(260));
        }

        // Add new / update changed / keep untouched cards in engine order.
        for (int index = 0; index < insights.Count; index++)
        {
            var insight = insights[index];
            if (_insightCards.TryGetValue(insight.Id, out var entry))
            {
                if (!ReferenceEquals(entry.Shown, insight) && entry.Shown != insight)
                {
                    entry.Card.Child = BuildInsightContent(insight);
                    _insightCards[insight.Id] = (entry.Card, insight);
                }
                EnsurePosition(entry.Card, index);
            }
            else
            {
                var card = new Border { Opacity = 0 };
                card.Classes.Add("insightCard");
                card.Child = BuildInsightContent(insight);
                _insightCards[insight.Id] = (card, insight);
                InsightList.Children.Insert(Math.Min(index, InsightList.Children.Count), card);
                DispatcherTimer.RunOnce(() => card.Opacity = 1, TimeSpan.FromMilliseconds(30));
            }
        }

        InsightsEmpty.IsVisible = insights.Count == 0;
    }

    private void EnsurePosition(Border card, int index)
    {
        int current = InsightList.Children.IndexOf(card);
        if (current < 0 || current == index || index >= InsightList.Children.Count)
            return;
        InsightList.Children.RemoveAt(current);
        InsightList.Children.Insert(Math.Min(index, InsightList.Children.Count), card);
    }

    private static Control BuildInsightContent(NarratedActivity insight)
    {
        var tint = InsightTint(insight.Category);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };

        var iconBox = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(tint, 0.14),
            BorderBrush = new SolidColorBrush(tint, 0.30),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Viewbox
            {
                Width = 13,
                Height = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Path
                {
                    Width = 24,
                    Height = 24,
                    Data = (Geometry)Application.Current!.FindResource(insight.IconKey)!,
                    Stroke = new SolidColorBrush(tint),
                    StrokeThickness = 1.9,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                },
            },
        };
        Grid.SetColumn(iconBox, 0);
        grid.Children.Add(iconBox);

        var body = new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 0, 0) };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock
        {
            Text = insight.Title,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        titleRow.Children.Add(title);
        var time = new TextBlock
        {
            Text = insight.Timestamp.ToString("HH:mm"),
            FontSize = 10,
            Foreground = MutedText,
            Margin = new Thickness(8, 1, 0, 0),
        };
        Grid.SetColumn(time, 1);
        titleRow.Children.Add(time);
        body.Children.Add(titleRow);

        body.Children.Add(new TextBlock
        {
            Text = insight.Detail,
            FontSize = 11,
            LineHeight = 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedText,
        });

        var confidence = ConfidenceVisual(insight.Confidence);
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(confidence.Color, 0.13),
            BorderBrush = new SolidColorBrush(confidence.Color, 0.32),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(99),
            Padding = new Thickness(7, 1, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 0),
            Child = new TextBlock
            {
                Text = confidence.Label,
                FontSize = 8.5,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 0.8,
                Foreground = new SolidColorBrush(confidence.Color),
            },
        });

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static (string Label, Color Color) ConfidenceVisual(NarrationConfidence confidence) => confidence switch
    {
        NarrationConfidence.High => ("HIGH CONFIDENCE", Color.Parse("#3FBF7F")),
        NarrationConfidence.Medium => ("MEDIUM CONFIDENCE", Color.Parse("#4D9FFF")),
        _ => ("LOW CONFIDENCE", Color.Parse("#9AA3B4")),
    };

    private static Color InsightTint(ActivityCategory category) => category switch
    {
        ActivityCategory.Cpu => CpuColor,
        ActivityCategory.Memory => MemoryColor,
        ActivityCategory.Disk => DiskColor,
        ActivityCategory.Network => NetworkColor,
        ActivityCategory.Application => ProcessColor,
        ActivityCategory.Battery => DiskColor,
        _ => Color.Parse("#45C4D6"),
    };

    private void UpdateEmptyState()
    {
        bool anyRows = StoryList.Children.Count > 0;
        bool anyVisible = StoryList.Children.Any(c => c.IsVisible);
        EmptyState.IsVisible = !anyVisible;
        if (!anyRows)
        {
            EmptyTitle.Text = "Your computer is quiet.";
            EmptyBody.Text = "Interact with applications to begin discovering what your operating system is doing.";
        }
        else if (!anyVisible)
        {
            EmptyTitle.Text = "No matching events.";
            EmptyBody.Text = "Try a different filter or search term.";
        }
    }
}
