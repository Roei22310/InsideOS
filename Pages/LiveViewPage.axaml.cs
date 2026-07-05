using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Explanations;
using InsideOS.Services.Insights;
using InsideOS.Services.Learning;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;
using InsideOS.ViewModels;

namespace InsideOS.Pages;

/// <summary>
/// The central dashboard: a composition of the services that already run —
/// live metrics, process monitoring, the timeline story stream, the insight
/// feed, the explanation engine and the lesson manager. No new monitoring,
/// no duplicated logic; the page only renders and routes clicks.
/// </summary>
public partial class LiveViewPage : UserControl
{
    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush HealthGood = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush HealthWarn = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush HealthHot = new SolidColorBrush(Color.Parse("#E56262"));

    private static readonly Color[] AvatarPalette =
    {
        Color.Parse("#4D9FFF"), Color.Parse("#7A5CFF"), Color.Parse("#3FBF7F"),
        Color.Parse("#E5A455"), Color.Parse("#45C4D6"), Color.Parse("#F27DA8"),
        Color.Parse("#B48CFF"), Color.Parse("#E56262"),
    };

    private readonly LiveMetricsService _metrics;
    private readonly SystemStoryService _story;
    private readonly InsightService _insights;
    private readonly LessonManager _lessons;
    private readonly Action<ProcessSample> _openProcess;
    private readonly Action<TimelineStorySnapshot> _openStory;
    private readonly Action<string> _navigate;

    // A dedicated engine instance narrates the most active process from the
    // same measured data Action Flow uses (disk/network honestly unavailable
    // here, so those rules simply never fire on the dashboard).
    private readonly RuleBasedExplanationEngine _explainer = new();

    private ProcessSample? _activeSample;
    private bool _attached;

    public LiveViewPage(
        LiveMetricsService metrics,
        ProcessMonitorService processes,
        SystemStoryService story,
        InsightService insights,
        LessonManager lessons,
        Action<ProcessSample> openProcess,
        Action<TimelineStorySnapshot> openStory,
        Action<string> navigate)
    {
        InitializeComponent();
        _metrics = metrics;
        _story = story;
        _insights = insights;
        _lessons = lessons;
        _openProcess = openProcess;
        _openStory = openStory;
        _navigate = navigate;

        var info = metrics.StaticInfo;
        SysFacts.Text = $"{info.OsVersion} · {info.CpuModel} · {Format.Bytes(info.TotalMemoryBytes)}";
        CpuCard.Subtitle = $"Share of time all {info.LogicalCores} cores spend busy";

        metrics.SnapshotUpdated += s => Dispatcher.UIThread.Post(() => ApplyMetrics(s));
        processes.ProcessesUpdated += s => Dispatcher.UIThread.Post(() => ApplyProcesses(s));
        _story.StoryChanged += _ => Dispatcher.UIThread.Post(RefreshTimeline);
        _insights.InsightsUpdated += list => Dispatcher.UIThread.Post(() => ApplyInsights(list));
        _lessons.Changed += () => Dispatcher.UIThread.Post(ApplyLearning);
        ApplyLearning();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RefreshTimeline();
        ApplyInsights(_insights.CurrentInsights);
        ApplyLearning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false; // no rendering work while another page is shown
    }

    // ---- system health + resource cards ----

    private void ApplyMetrics(MetricsSnapshot s)
    {
        if (!_attached)
            return;

        // Resource cards (existing MetricCard control animates its own bar).
        if (s.CpuUsagePercent is { } cpu)
        {
            CpuCard.Value = $"{cpu:0.0}%";
            CpuCard.BarValue = cpu;
        }
        if (s.Memory is { } memory && memory.TotalBytes > 0)
        {
            MemoryCard.Value = Format.Bytes(memory.UsedBytes);
            MemoryCard.BarValue = 100.0 * memory.UsedBytes / memory.TotalBytes;
            MemoryCard.Subtitle = $"In use of {Format.Bytes(memory.TotalBytes)}";
        }
        if (s.Disk is { } disk && disk.TotalBytes > 0)
        {
            DiskCard.Value = Format.Bytes(disk.UsedBytes);
            DiskCard.BarValue = 100.0 * disk.UsedBytes / disk.TotalBytes;
            DiskCard.Subtitle = $"Used of {Format.Bytes(disk.TotalBytes)}";
        }
        if (s.DownloadBytesPerSecond is { } down && s.UploadBytesPerSecond is { } up)
        {
            NetworkCard.Value = Format.Speed(down + up);
            NetworkCard.Subtitle = $"↓ {Format.Speed(down)}  ·  ↑ {Format.Speed(up)}";
        }

        // Health summary — three plain-language judgements from measured data.
        if (s.CpuUsagePercent is not { } cpuNow || s.Memory is not { } mem || mem.TotalBytes == 0)
            return;
        double memPct = 100.0 * mem.UsedBytes / mem.TotalBytes;
        double netTotal = (s.DownloadBytesPerSecond ?? 0) + (s.UploadBytesPerSecond ?? 0);

        string cpuText = cpuNow < 30 ? "CPU usage is low"
            : cpuNow < 60 ? "CPU usage is moderate" : "CPU usage is high";
        string memText = memPct < 60 ? "Memory pressure is low"
            : memPct < 85 ? "Memory pressure is moderate" : "Memory pressure is high";
        string netText = netTotal < 100 * 1024 ? "Network is mostly idle"
            : netTotal < 2 * 1024 * 1024 ? "Network is lightly active" : "Network is busy";

        bool hot = cpuNow >= 85 || memPct >= 92;
        bool warm = cpuNow >= 60 || memPct >= 85;
        SetText(HealthHeadline, hot ? "System is under heavy load."
            : warm ? "System is working hard." : "System is healthy.");
        HealthDot.Fill = hot ? HealthHot : warm ? HealthWarn : HealthGood;
        SetText(HealthDetail, $"{cpuText}  ·  {memText}  ·  {netText}");

        string facts2 = $"Up {Format.Uptime(s.Uptime)}";
        if (s.Battery is { } battery)
            facts2 += $"  ·  Battery {battery.Percent}%";
        SetText(SysFacts2, facts2);
    }

    // ---- most active process + current activity ----

    private void ApplyProcesses(IReadOnlyList<ProcessSample> samples)
    {
        if (!_attached || samples.Count == 0)
            return;

        var byCpu = samples
            .Where(s => s.CpuPercent is not null)
            .OrderByDescending(s => s.CpuPercent)
            .ToList();
        var top = byCpu.FirstOrDefault();

        // Most active process card.
        if (top is not null)
        {
            bool switched = _activeSample?.Pid != top.Pid;
            _activeSample = top;
            ActiveEmpty.IsVisible = false;
            ActiveBody.IsVisible = true;
            ActiveStats.IsVisible = true;

            if (switched)
            {
                var tint = AvatarPalette[StableHash(top.Name) % AvatarPalette.Length];
                ActiveAvatar.Background = new SolidColorBrush(tint, 0.14);
                ActiveAvatar.BorderBrush = new SolidColorBrush(tint, 0.32);
                ActiveInitial.Foreground = new SolidColorBrush(tint);
                ActiveInitial.Text = top.Name.Length > 0
                    ? char.ToUpperInvariant(top.Name[0]).ToString() : "?";
                SetText(ActiveName, top.Name);
            }
            SetText(ActiveCpu, $"{top.CpuPercent:0.0}%");
            SetText(ActiveMem, top.MemoryBytes is { } m ? Format.Bytes(m) : "—");
            ActiveStatusDot.Fill = ProcessRowViewModel.BrushForStatus(top.Status);
            SetText(ActiveStatus, ProcessRowViewModel.StatusLabel(top.Status));

            var explanation = _explainer.Explain(new ProcessFlowSnapshot(
                top.Pid, top.Name, top.Status,
                new FlowMetric(top.CpuPercent,
                    top.CpuIsPrecise ? MetricQuality.Measured : MetricQuality.Calculated),
                new FlowMetric(top.MemoryBytes is { } mb ? mb : null, MetricQuality.Measured),
                new FlowMetric(null, MetricQuality.Unavailable),
                new FlowMetric(null, MetricQuality.Unavailable),
                null, null, null, null,
                ProcessExited: false));
            if (explanation.Text.Length > 0 && ActiveExplanation.Text != explanation.Text)
            {
                ActiveExplanation.Opacity = 0;
                DispatcherTimer.RunOnce(() =>
                {
                    ActiveExplanation.Text = explanation.Text;
                    ActiveExplanation.Opacity = 1;
                }, TimeSpan.FromMilliseconds(180));
            }
        }

        // Current activity narrative — counted, never invented.
        int active = samples.Count(s => (s.CpuPercent ?? 0) >= 5);
        int sleeping = samples.Count(s => s.Status == ProcessStatus.Sleeping);
        SetText(ActivityLine1, top is null
            ? "Watching processes…"
            : $"{top.Name} is currently the most active process.");
        SetText(ActivityLine2,
            $"{active} {(active == 1 ? "process is" : "processes are")} actively using the CPU right now.");
        SetText(ActivityLine3,
            $"{samples.Count} processes are managed by the operating system — {sleeping} of them are sleeping.");

        var slots = new[] { (Top1Name, Top1Val), (Top2Name, Top2Val), (Top3Name, Top3Val) };
        for (int i = 0; i < slots.Length; i++)
        {
            if (i < byCpu.Count && byCpu[i].CpuPercent is { } cpuVal)
            {
                SetText(slots[i].Item1, byCpu[i].Name);
                SetText(slots[i].Item2, $"{cpuVal:0.0}%");
                slots[i].Item2.Foreground = ProcessRowViewModel.BrushForLoad(cpuVal);
            }
            else
            {
                SetText(slots[i].Item1, "");
                SetText(slots[i].Item2, "");
            }
        }
    }

    // ---- recent timeline ----

    private void RefreshTimeline()
    {
        if (!_attached)
            return;
        var latest = _story.GetStories().TakeLast(5).Reverse().ToList();
        TimelineRows.Children.Clear();
        foreach (var snapshot in latest)
            TimelineRows.Children.Add(BuildStoryRow(snapshot));
        TimelineEmpty.IsVisible = latest.Count == 0;
    }

    private Control BuildStoryRow(TimelineStorySnapshot snapshot)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(snapshot.Severity switch
            {
                TimelineSeverity.High => Color.Parse("#E56262"),
                TimelineSeverity.Notice => Color.Parse("#E5A455"),
                _ => Color.Parse("#4D9FFF"),
            }),
            Margin = new Thickness(1, 4, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        grid.Children.Add(dot);

        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = snapshot.ProcessName,
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = snapshot.Events[^1].Title,
            FontSize = 11,
            Foreground = MutedText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var time = new TextBlock
        {
            Text = snapshot.LastTime.ToString("HH:mm"),
            FontSize = 10.5,
            Foreground = MutedText,
            Margin = new Thickness(10, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        var row = new Border { Child = grid };
        row.Classes.Add("rowHover");
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _openStory(snapshot); // exact same path the Timeline page uses
        };
        return row;
    }

    // ---- live insights (top 3) ----

    private void ApplyInsights(IReadOnlyList<Insight> insights)
    {
        if (!_attached)
            return;
        var top = insights.Take(3).ToList();
        InsightRows.Children.Clear();
        foreach (var insight in top)
            InsightRows.Children.Add(BuildInsightRow(insight));
        InsightsEmptyDash.IsVisible = top.Count == 0;
    }

    private static Control BuildInsightRow(Insight insight)
    {
        var tint = insight.Category switch
        {
            InsightCategory.Cpu => Color.Parse("#4D9FFF"),
            InsightCategory.Memory => Color.Parse("#7A5CFF"),
            InsightCategory.Disk => Color.Parse("#E5A455"),
            InsightCategory.Network => Color.Parse("#3FBF7F"),
            InsightCategory.Battery => Color.Parse("#E5A455"),
            InsightCategory.Application => Color.Parse("#9AA3B4"),
            _ => Color.Parse("#45C4D6"),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var iconBox = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(tint, 0.14),
            BorderBrush = new SolidColorBrush(tint, 0.30),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Viewbox
            {
                Width = 11,
                Height = 11,
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
        grid.Children.Add(iconBox);

        var body = new StackPanel { Spacing = 2, Margin = new Thickness(9, 0, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = insight.Title,
            FontSize = 11.5,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{insight.Confidence switch
            {
                InsightConfidence.High => "High",
                InsightConfidence.Medium => "Medium",
                _ => "Low",
            }} confidence · {insight.Timestamp:HH:mm}",
            FontSize = 10,
            Foreground = MutedText,
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    // ---- learning ----

    private void ApplyLearning()
    {
        var next = _lessons.Lessons.FirstOrDefault(l => !_lessons.IsCompleted(l));
        SetText(LessonTitle, next is not null
            ? $"Lesson {next.Number} · {next.Title}"
            : "All available lessons completed — more are on the way.");
        SetText(LessonMeta,
            $"{_lessons.CompletedCount} of {_lessons.PlannedLessonCount} lessons completed · {_lessons.ProgressPercent}%");
        LearnBar.Value = _lessons.ProgressPercent;
    }

    // ---- interactions ----

    private void OnActiveCardPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_activeSample is { } sample)
        {
            e.Handled = true;
            _openProcess(sample);
        }
    }

    private void OnViewTimeline(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _navigate("timeline");

    private void OnContinueLearning(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _navigate("learning");

    // ---- helpers ----

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text)
            block.Text = text; // change-only writes keep the page flicker-free
    }

    private static int StableHash(string name)
    {
        int hash = 0;
        foreach (char c in name)
            hash = hash * 31 + c;
        return Math.Abs(hash);
    }
}
