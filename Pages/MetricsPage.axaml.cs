using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using InsideOS.Controls;
using InsideOS.Services.History;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;
using InsideOS.ViewModels;

namespace InsideOS.Pages;

/// <summary>
/// Historical analysis dashboard: renders the rolling history recorded by
/// <see cref="MetricHistoryService"/> as live charts with stats, trend
/// labels, timeline story markers and a most-active-applications table.
/// Pure consumer — no monitoring of its own, buffers reused every refresh.
/// </summary>
public partial class MetricsPage : UserControl
{
    private static readonly IBrush PrimaryText = new SolidColorBrush(Color.Parse("#EDEFF4"));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#9AA3B4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush TrendUp = new SolidColorBrush(Color.Parse("#E5A455"));
    private static readonly IBrush TrendDown = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush TrendFlat = new SolidColorBrush(Color.Parse("#656F82"));

    private sealed class ChartSlot
    {
        public required HistoryMetric Metric;
        public required HistoryChart Chart;
        public required TextBlock Now;
        public required TextBlock Stats;
        public required TextBlock Trend;
        public required Func<double, string> Format;
        public required double TrendEpsilon;
        public double? ForcedMin;
        public double? ForcedMax;
        public double MinSpanFloor = 1;
        public TimelineCategory? MarkerCategory;
        // Last computed stats, reused by the health summary.
        public int Count;
        public double Cur, Min, Max, Avg, TrendDelta;
        public DateTime MaxTime;
    }

    private sealed class ProcSlot
    {
        public required Border Row;
        public required TextBlock Name;
        public required TextBlock AvgCpu;
        public required TextBlock PeakCpu;
        public required TextBlock AvgMem;
        public required TextBlock Trend;
        public ProcessSample? Sample;
    }

    private readonly MetricHistoryService _history;
    private readonly SystemStoryService _story;
    private readonly Action<TimelineStorySnapshot> _openStory;
    private readonly Action<ProcessSample> _openProcess;
    private readonly DateTime[] _timesBuf = new DateTime[MetricHistoryService.Capacity];
    private readonly double[] _valuesBuf = new double[MetricHistoryService.Capacity];
    private readonly List<ChartSlot> _slots = new();
    private readonly List<ProcSlot> _procSlots = new();
    private TimeSpan _window = TimeSpan.FromMinutes(5);
    private bool _attached;

    public MetricsPage(
        MetricHistoryService history,
        SystemStoryService story,
        Action<TimelineStorySnapshot> openStory,
        Action<ProcessSample> openProcess,
        ulong totalMemoryBytes)
    {
        InitializeComponent();
        _history = history;
        _story = story;
        _openStory = openStory;
        _openProcess = openProcess;

        AddSlot(HistoryMetric.CpuUsage, CpuChart, CpuNow, CpuStats, CpuTrend,
            v => $"{v:0.0}%", eps: 2.5, color: "#4D9FFF", forcedMin: 0, forcedMax: 100,
            markers: TimelineCategory.Cpu);
        AddSlot(HistoryMetric.MemoryUsed, MemChart, MemNow, MemStats, MemTrend,
            Format.Bytes, eps: 64 * 1024.0 * 1024, color: "#7A5CFF",
            forcedMin: 0, forcedMax: totalMemoryBytes, markers: TimelineCategory.Memory);
        AddSlot(HistoryMetric.NetworkDown, DownChart, DownNow, DownStats, DownTrend,
            Format.Speed, eps: 30 * 1024.0, color: "#3FBF7F", minSpanFloor: 20 * 1024.0,
            markers: TimelineCategory.Network);
        AddSlot(HistoryMetric.NetworkUp, UpChart, UpNow, UpStats, UpTrend,
            Format.Speed, eps: 30 * 1024.0, color: "#45C4D6", minSpanFloor: 20 * 1024.0,
            markers: TimelineCategory.Network);
        AddSlot(HistoryMetric.DiskUsed, DiskChart, DiskNow, DiskStats, DiskTrend,
            Format.Bytes, eps: 128 * 1024.0 * 1024, color: "#E5A455",
            markers: TimelineCategory.Disk);
        AddSlot(HistoryMetric.Battery, BatteryChart, BatteryNow, BatteryStats, BatteryTrend,
            v => $"{v:0}%", eps: 1.5, color: "#3FBF7F", forcedMin: 0, forcedMax: 100);
        AddSlot(HistoryMetric.ProcessCount, ProcChart, ProcNow, ProcStats, ProcTrend,
            v => $"{v:0}", eps: 4, color: "#9AA3B4", minSpanFloor: 10,
            markers: TimelineCategory.Process);
        AddSlot(HistoryMetric.ActiveProcessCount, ActiveChart, ActiveNow, ActiveStats, ActiveTrend,
            v => $"{v:0}", eps: 2, color: "#F27DA8", minSpanFloor: 5);

        for (int i = 0; i < 5; i++)
            _procSlots.Add(BuildProcSlot());

        _history.Updated += () => Dispatcher.UIThread.Post(Refresh);
    }

    private void AddSlot(HistoryMetric metric, HistoryChart chart, TextBlock now, TextBlock stats,
        TextBlock trend, Func<double, string> format, double eps, string color,
        double? forcedMin = null, double? forcedMax = null, double minSpanFloor = 1,
        TimelineCategory? markers = null)
    {
        chart.LineColor = Color.Parse(color);
        chart.Formatter = format;
        chart.MarkerClicked += payload =>
        {
            if (payload is TimelineStorySnapshot snapshot)
                _openStory(snapshot);
        };
        _slots.Add(new ChartSlot
        {
            Metric = metric, Chart = chart, Now = now, Stats = stats, Trend = trend,
            Format = format, TrendEpsilon = eps, ForcedMin = forcedMin, ForcedMax = forcedMax,
            MinSpanFloor = minSpanFloor, MarkerCategory = markers,
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false; // charts cost nothing while the page is hidden
    }

    // ---- refresh (once per second while visible) ----

    private void Refresh()
    {
        if (!_attached)
            return;

        // Story markers, grouped once per refresh.
        var cutoff = DateTime.Now - _window;
        var markersByCategory = new Dictionary<TimelineCategory, List<ChartMarker>>();
        foreach (var snapshot in _story.GetStories())
        {
            if (snapshot.Pid < 0 || snapshot.LastTime < cutoff)
                continue;
            foreach (var category in snapshot.Categories)
            {
                if (!markersByCategory.TryGetValue(category, out var list))
                    markersByCategory[category] = list = new List<ChartMarker>();
                list.Add(new ChartMarker(
                    snapshot.LastTime,
                    snapshot.Severity switch
                    {
                        TimelineSeverity.High => Color.Parse("#E56262"),
                        TimelineSeverity.Notice => Color.Parse("#E5A455"),
                        _ => Color.Parse("#4D9FFF"),
                    },
                    $"{snapshot.ProcessName} — {snapshot.Events[^1].Title}",
                    snapshot));
            }
        }

        foreach (var slot in _slots)
        {
            int n = _history.Read(slot.Metric, _window, _timesBuf, _valuesBuf);
            slot.Count = n;
            slot.Chart.SetData(_timesBuf, _valuesBuf, n, _window,
                slot.ForcedMin, slot.ForcedMax, slot.MinSpanFloor);
            slot.Chart.SetMarkers(slot.MarkerCategory is { } category
                && markersByCategory.TryGetValue(category, out var list)
                    ? list
                    : Array.Empty<ChartMarker>());

            if (n == 0)
            {
                SetText(slot.Now, "—");
                SetText(slot.Stats, "No samples in this window yet.");
                SetText(slot.Trend, "");
                continue;
            }

            double min = double.MaxValue, max = double.MinValue, sum = 0;
            double firstHalf = 0, secondHalf = 0;
            int half = n / 2;
            DateTime maxTime = _timesBuf[0];
            for (int i = 0; i < n; i++)
            {
                double v = _valuesBuf[i];
                sum += v;
                if (v < min) min = v;
                if (v > max) { max = v; maxTime = _timesBuf[i]; }
                if (i < half) firstHalf += v;
                else secondHalf += v;
            }
            slot.Cur = _valuesBuf[n - 1];
            slot.Min = min;
            slot.Max = max;
            slot.Avg = sum / n;
            slot.MaxTime = maxTime;
            slot.TrendDelta = n >= 10
                ? secondHalf / Math.Max(1, n - half) - firstHalf / Math.Max(1, half)
                : 0;

            SetText(slot.Now, slot.Format(slot.Cur));
            SetText(slot.Stats,
                $"MIN {slot.Format(min)} · MAX {slot.Format(max)} · AVG {slot.Format(slot.Avg)}");
            var (label, brush) = n < 10
                ? ("", TrendFlat)
                : Math.Abs(slot.TrendDelta) <= slot.TrendEpsilon
                    ? ("→ STABLE", TrendFlat)
                    : slot.TrendDelta > 0
                        ? ("↗ RISING", TrendUp)
                        : ("↘ FALLING", TrendDown);
            SetText(slot.Trend, label);
            slot.Trend.Foreground = brush;
        }

        BatteryNA.IsVisible = _slots.First(s => s.Metric == HistoryMetric.Battery).Count == 0;

        RefreshHealth();
        RefreshProcesses();
    }

    private void RefreshHealth()
    {
        var cpu = Slot(HistoryMetric.CpuUsage);
        var mem = Slot(HistoryMetric.MemoryUsed);
        var down = Slot(HistoryMetric.NetworkDown);
        var up = Slot(HistoryMetric.NetworkUp);
        var disk = Slot(HistoryMetric.DiskUsed);
        if (cpu.Count < 10)
        {
            SetText(HealthLine1, "Collecting measurements — the summary sharpens as history builds up.");
            SetText(HealthLine2, "");
            SetText(HealthLine3, "");
            SetText(HealthLine4, "");
            return;
        }

        string cpuLine = Math.Abs(cpu.TrendDelta) <= cpu.TrendEpsilon
            ? $"CPU has remained stable around {cpu.Avg:0}%."
            : cpu.TrendDelta > 0
                ? $"CPU usage has been climbing — now {cpu.Cur:0}% against a {cpu.Avg:0}% average."
                : $"CPU usage has been settling down — now {cpu.Cur:0}%.";
        if (cpu.Max >= 40 && cpu.Max >= cpu.Avg * 3)
            cpuLine += $" It peaked at {cpu.Max:0}% at {cpu.MaxTime:HH:mm}.";
        SetText(HealthLine1, cpuLine);

        SetText(HealthLine2, Math.Abs(mem.TrendDelta) <= mem.TrendEpsilon
            ? $"Memory usage has remained steady around {Format.Bytes(mem.Avg)}."
            : mem.TrendDelta > 0
                ? $"Memory usage has steadily increased (+{Format.Bytes(mem.TrendDelta)} across the window)."
                : $"Memory usage has decreased ({Format.Bytes(Math.Abs(mem.TrendDelta))} freed).");

        double netMax = Math.Max(down.Max, up.Max);
        DateTime netMaxTime = down.Max >= up.Max ? down.MaxTime : up.MaxTime;
        bool recentSpike = netMax >= 1024 * 1024
            && netMaxTime >= DateTime.Now - TimeSpan.FromSeconds(_window.TotalSeconds / 4);
        SetText(HealthLine3, recentSpike
            ? $"Network activity recently spiked ({Format.Speed(netMax)} at {netMaxTime:HH:mm})."
            : down.Avg + up.Avg < 50 * 1024
                ? "Network activity has been minimal."
                : $"Network activity has been moderate — averaging {Format.Speed(down.Avg + up.Avg)}.");

        SetText(HealthLine4, Math.Abs(disk.Cur - disk.Avg) < 50 * 1024.0 * 1024 && disk.Max - disk.Min < 200 * 1024.0 * 1024
            ? "Disk usage has barely changed."
            : $"Disk usage moved by about {Format.Bytes(Math.Abs(disk.Max - disk.Min))} in this window.");
    }

    private void RefreshProcesses()
    {
        var top = _history.GetTopProcesses(_window, _procSlots.Count);
        for (int i = 0; i < _procSlots.Count; i++)
        {
            var slot = _procSlots[i];
            if (i < top.Count)
            {
                var stat = top[i];
                slot.Sample = stat.LastSample;
                slot.Row.IsVisible = true;
                SetText(slot.Name, stat.Name);
                SetText(slot.AvgCpu, $"{stat.AvgCpu:0.0}%");
                slot.AvgCpu.Foreground = ProcessRowViewModel.BrushForLoad(stat.AvgCpu);
                SetText(slot.PeakCpu, $"{stat.PeakCpu:0.0}%");
                SetText(slot.AvgMem, stat.AvgMemoryBytes is { } m ? Format.Bytes(m) : "—");
                var (arrow, brush) = Math.Abs(stat.TrendDelta) <= 1.5
                    ? ("→", TrendFlat)
                    : stat.TrendDelta > 0 ? ("↗", TrendUp) : ("↘", TrendDown);
                SetText(slot.Trend, arrow);
                slot.Trend.Foreground = brush;
            }
            else
            {
                slot.Row.IsVisible = false;
                slot.Sample = null;
            }
        }
        ProcEmpty.IsVisible = top.Count == 0;
    }

    private ChartSlot Slot(HistoryMetric metric) => _slots.First(s => s.Metric == metric);

    private ProcSlot BuildProcSlot()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,76,76,96,52") };
        var name = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Foreground = PrimaryText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(name);
        var cells = new TextBlock[4];
        for (int c = 0; c < 4; c++)
        {
            cells[c] = new TextBlock
            {
                FontSize = 11.5,
                TextAlignment = TextAlignment.Right,
                Foreground = SecondaryText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cells[c], c + 1);
            grid.Children.Add(cells[c]);
        }

        var row = new Border { Child = grid, IsVisible = false };
        row.Classes.Add("procRow");
        var slot = new ProcSlot
        {
            Row = row, Name = name, AvgCpu = cells[0], PeakCpu = cells[1],
            AvgMem = cells[2], Trend = cells[3],
        };
        row.PointerPressed += (_, e) =>
        {
            if (slot.Sample is { } sample)
            {
                e.Handled = true;
                _openProcess(sample);
            }
        };
        ProcRows.Children.Add(row);
        return slot;
    }

    // ---- time range ----

    private void OnRangePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag } pill)
            return;
        e.Handled = true;
        foreach (var child in RangeBar.Children)
            if (child is Border other)
                other.Classes.Remove("selected");
        pill.Classes.Add("selected");
        _window = TimeSpan.FromMinutes(int.Parse(tag));
        Refresh();
    }

    private static void SetText(TextBlock block, string text)
    {
        if (block.Text != text)
            block.Text = text;
    }
}
