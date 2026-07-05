using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Learning;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.ViewModels;

namespace InsideOS.Pages;

public partial class ProcessExplorerPage : UserControl
{
    // Smart is the default: it surfaces active and user apps and lets sleeping
    // system processes settle to the bottom without hiding them.
    private enum SortColumn { Smart, Name, Cpu, Memory }

    private readonly ProcessSelection _selection;
    private readonly ILearnContentService _learnContent;
    private readonly ulong _totalMemoryBytes;
    private readonly Dictionary<int, ProcessRowViewModel> _rowsByPid = new();
    private readonly ObservableCollection<ProcessRowViewModel> _rows = new();

    private IReadOnlyList<ProcessSample>? _latestSamples;
    private SortColumn _sortColumn = SortColumn.Smart;
    private bool _sortDescending = true;
    private ProcessFilter _filter = ProcessFilter.All;
    private bool _attached;

    public ProcessExplorerPage(
        ProcessMonitorService monitor,
        ProcessSelection selection,
        ILearnContentService learnContent,
        ulong totalMemoryBytes)
    {
        InitializeComponent();
        _selection = selection;
        _learnContent = learnContent;
        _totalMemoryBytes = totalMemoryBytes;
        ProcessList.ItemsSource = _rows;
        monitor.ProcessesUpdated += OnProcessesUpdated;
        UpdateFilterHint();
    }

    /// <summary>
    /// One honest sentence describing what the current filter selects, phrased
    /// in the operating system's own terms. This turns the pills into a small
    /// lesson: the difference between "running over the last second", a fading
    /// recent burst, and a process that is simply loaded and waiting.
    /// </summary>
    private void UpdateFilterHint() => FilterHint.Text = _filter switch
    {
        ProcessFilter.Running =>
            "Running — used measurable processor time over the last second. macOS reports even a busy "
            + "process as “sleeping” at any single instant, so this looks across the whole second.",
        ProcessFilter.RecentlyActive =>
            "Recently Active — worked hard within the last few seconds, then went quiet. Kept here "
            + "briefly so a short burst doesn’t disappear the moment it ends.",
        ProcessFilter.Sleeping =>
            "Sleeping — loaded and waiting for something to do. It stays in memory so it can respond "
            + "instantly; this is completely normal.",
        _ =>
            "Every process the operating system is currently managing — from your own apps to the "
            + "background system services that keep macOS running.",
    };

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        RefreshRows(); // catch up on anything that changed while hidden
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Suspend UI work while hidden. Crucially, this also stops a background
        // refresh from nulling the shared process selection (which drives Action
        // Flow) if the selected process happens to exit while another page is up.
        _attached = false;
    }

    private void OnProcessesUpdated(IReadOnlyList<ProcessSample> samples) =>
        Dispatcher.UIThread.Post(() =>
        {
            _latestSamples = samples;
            if (_attached)
                RefreshRows();
        });

    private void RefreshRows()
    {
        if (_latestSamples is not { } samples)
            return;

        var alivePids = new HashSet<int>();
        foreach (var sample in samples)
        {
            alivePids.Add(sample.Pid);
            if (_rowsByPid.TryGetValue(sample.Pid, out var row))
                row.Update(sample);
            else
                _rowsByPid[sample.Pid] = new ProcessRowViewModel(sample);
        }

        List<int>? exited = null;
        foreach (int pid in _rowsByPid.Keys)
        {
            if (!alivePids.Contains(pid))
                (exited ??= new List<int>()).Add(pid);
        }
        if (exited is not null)
        {
            foreach (int pid in exited)
                _rowsByPid.Remove(pid);
        }

        string? query = SearchBox.Text?.Trim();
        IEnumerable<ProcessRowViewModel> visible = _rowsByPid.Values
            .Where(r => r.MatchesFilter(_filter));
        if (!string.IsNullOrEmpty(query))
            visible = visible.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        ApplyRowOrder(SortRows(visible).ToList());

        CountText.Text = _rows.Count == samples.Count
            ? $"{samples.Count} processes"
            : $"{_rows.Count} of {samples.Count} processes";

        UpdateDetails();
    }

    private IEnumerable<ProcessRowViewModel> SortRows(IEnumerable<ProcessRowViewModel> rows) =>
        (_sortColumn, _sortDescending) switch
        {
            // Smart: activity buckets first, then CPU, then memory as tie-breaks.
            (SortColumn.Smart, _) => rows
                .OrderByDescending(r => r.SmartTier)
                .ThenByDescending(r => r.SortCpu)
                .ThenByDescending(r => r.SortMemory)
                .ThenBy(r => r.Pid),
            (SortColumn.Cpu, true) => rows.OrderByDescending(r => r.SortCpu).ThenBy(r => r.Pid),
            (SortColumn.Cpu, false) => rows.OrderBy(r => r.SortCpu).ThenBy(r => r.Pid),
            (SortColumn.Memory, true) => rows.OrderByDescending(r => r.SortMemory).ThenBy(r => r.Pid),
            (SortColumn.Memory, false) => rows.OrderBy(r => r.SortMemory).ThenBy(r => r.Pid),
            (SortColumn.Name, false) => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
            _ => rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
        };

    private void OnFilterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag } pill
            || !Enum.TryParse<ProcessFilter>(tag, out var filter))
            return;
        e.Handled = true;
        _filter = filter;
        foreach (var child in FilterBar.Children)
            if (child is Border other)
                other.Classes.Remove("selected");
        pill.Classes.Add("selected");
        UpdateFilterHint();
        RefreshRows();
    }

    /// <summary>
    /// Reconciles the bound collection with the desired order using minimal
    /// Remove/Move/Insert operations. Row instances are reused, so selection,
    /// scroll position and running animations survive every update.
    /// </summary>
    private void ApplyRowOrder(List<ProcessRowViewModel> desired)
    {
        var desiredSet = new HashSet<ProcessRowViewModel>(desired);
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(_rows[i]))
                _rows.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            if (i < _rows.Count && ReferenceEquals(_rows[i], row))
                continue;

            int currentIndex = _rows.IndexOf(row);
            if (currentIndex >= 0)
                _rows.Move(currentIndex, i);
            else
                _rows.Insert(i, row);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDetails();
        // Only propagate a real user pick. When the ListBox selection clears
        // because the page is being hidden (detach) or the selected row was
        // filtered out, keep the shared selection so Action Flow keeps showing
        // the process the user was inspecting instead of going blank.
        if ((ProcessList.SelectedItem as ProcessRowViewModel)?.LatestSample is { } sample)
            _selection.Select(sample);
    }

    private void UpdateDetails()
    {
        if (ProcessList.SelectedItem is not ProcessRowViewModel row)
        {
            DetailsContent.IsVisible = false;
            DetailsEmpty.IsVisible = true;
            return;
        }

        var sample = row.LatestSample;
        DetailsEmpty.IsVisible = false;
        DetailsContent.IsVisible = true;

        DetailName.Text = sample.Name;
        DetailStatusText.Text = ProcessRowViewModel.StatusLabel(sample.EffectiveStatus);
        DetailStatusDot.Fill = ProcessRowViewModel.BrushForStatus(sample.EffectiveStatus);
        DetailKind.Text = row.IsUserApplication ? "Your application" : "System process";
        DetailPid.Text = sample.Pid.ToString();
        DetailThreads.Text = sample.ThreadCount?.ToString() ?? "Not available";
        DetailStart.Text = sample.StartTime is { } start ? FormatStart(start) : "Not available";

        if (sample.CpuPercent is { } cpu)
        {
            DetailCpu.Text = $"{cpu:0.0}%";
            DetailCpuBar.Value = Math.Clamp(cpu, 0, 100);
            DetailCpuBar.Foreground = ProcessRowViewModel.BrushForLoad(cpu);
        }
        else
        {
            DetailCpu.Text = "—";
            DetailCpuBar.Value = 0;
        }

        if (sample.MemoryBytes is { } memory)
        {
            DetailMemory.Text = Format.Bytes(memory);
            if (_totalMemoryBytes > 0)
            {
                double share = 100.0 * memory / _totalMemoryBytes;
                DetailMemoryBar.Value = Math.Clamp(share, 0, 100);
                DetailMemoryShare.Text = $"{share:0.0}% of installed memory";
            }
        }
        else
        {
            DetailMemory.Text = "—";
            DetailMemoryBar.Value = 0;
            DetailMemoryShare.Text = "";
        }

        // "What this means" — reuse the learning content engine so a sleeping
        // process, say, is explained rather than left to alarm. Feeds it a
        // snapshot built from the sample (disk/network aren't sampled here).
        var snapshot = BuildSnapshot(sample);
        DetailWhatLabel.Text = row.IsSleeping ? "WHAT “SLEEPING” MEANS" : "WHAT THIS MEANS";
        DetailWhy.Text = _learnContent.DescribeWhy(LearnTopicId.Process, snapshot);
        DetailWorry.Text = _learnContent.DescribeWorry(LearnTopicId.Process, snapshot);

        // Explain missing values instead of leaving a bare dash. Word it for
        // exactly what's missing so it never contradicts the "System process /
        // Your application" label above.
        bool threadsMissing = sample.ThreadCount is null;
        bool startMissing = sample.StartTime is null;
        DetailUnavailableNote.IsVisible = threadsMissing || startMissing;
        DetailUnavailableNote.Text = (threadsMissing, startMissing) switch
        {
            (true, true) =>
                "Thread count and start time aren't shown because macOS reveals these details only "
                + "to a process's owner — reading them for another user's process needs administrator access.",
            (true, false) =>
                "Thread count isn't shown because macOS reveals it only to a process's owner — "
                + "reading it for another user's process needs administrator access.",
            _ =>
                "A start time isn't recorded for this process.",
        };
    }

    private static ProcessFlowSnapshot BuildSnapshot(ProcessSample s) => new(
        s.Pid, s.Name, s.EffectiveStatus, // per-second snapshot carries the per-second state
        new FlowMetric(s.CpuPercent, s.CpuIsPrecise ? MetricQuality.Measured : MetricQuality.Calculated),
        new FlowMetric(s.MemoryBytes is { } m ? m : null, MetricQuality.Measured),
        new FlowMetric(null, MetricQuality.Unavailable),
        new FlowMetric(null, MetricQuality.Unavailable),
        null, null, null, null,
        ProcessExited: false);

    private static string FormatStart(DateTime start)
    {
        var today = DateTime.Now.Date;
        if (start.Date == today)
            return $"Today {start:HH:mm}";
        if (start.Date == today.AddDays(-1))
            return $"Yesterday {start:HH:mm}";
        return start.ToString("MMM d, HH:mm");
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => RefreshRows();

    private void OnNameHeaderPressed(object? sender, PointerPressedEventArgs e) => SetSort(SortColumn.Name, defaultDescending: false);

    private void OnCpuHeaderPressed(object? sender, PointerPressedEventArgs e) => SetSort(SortColumn.Cpu, defaultDescending: true);

    private void OnMemoryHeaderPressed(object? sender, PointerPressedEventArgs e) => SetSort(SortColumn.Memory, defaultDescending: true);

    private void SetSort(SortColumn column, bool defaultDescending)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = defaultDescending;
        }

        string arrow = _sortDescending ? " ▾" : " ▴";
        NameHeader.Text = "PROCESS" + (_sortColumn == SortColumn.Name ? arrow : "");
        CpuHeader.Text = "CPU" + (_sortColumn == SortColumn.Cpu ? arrow : "");
        MemoryHeader.Text = "MEMORY" + (_sortColumn == SortColumn.Memory ? arrow : "");

        RefreshRows();
    }
}
