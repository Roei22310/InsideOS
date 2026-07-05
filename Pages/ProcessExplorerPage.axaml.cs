using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.ViewModels;

namespace InsideOS.Pages;

public partial class ProcessExplorerPage : UserControl
{
    private enum SortColumn { Name, Cpu, Memory }

    private readonly ProcessSelection _selection;
    private readonly ulong _totalMemoryBytes;
    private readonly Dictionary<int, ProcessRowViewModel> _rowsByPid = new();
    private readonly ObservableCollection<ProcessRowViewModel> _rows = new();

    private IReadOnlyList<ProcessSample>? _latestSamples;
    private SortColumn _sortColumn = SortColumn.Cpu;
    private bool _sortDescending = true;

    public ProcessExplorerPage(ProcessMonitorService monitor, ProcessSelection selection, ulong totalMemoryBytes)
    {
        InitializeComponent();
        _selection = selection;
        _totalMemoryBytes = totalMemoryBytes;
        ProcessList.ItemsSource = _rows;
        monitor.ProcessesUpdated += OnProcessesUpdated;
    }

    private void OnProcessesUpdated(IReadOnlyList<ProcessSample> samples) =>
        Dispatcher.UIThread.Post(() =>
        {
            _latestSamples = samples;
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
        IEnumerable<ProcessRowViewModel> visible = _rowsByPid.Values;
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
            (SortColumn.Cpu, true) => rows.OrderByDescending(r => r.SortCpu).ThenBy(r => r.Pid),
            (SortColumn.Cpu, false) => rows.OrderBy(r => r.SortCpu).ThenBy(r => r.Pid),
            (SortColumn.Memory, true) => rows.OrderByDescending(r => r.SortMemory).ThenBy(r => r.Pid),
            (SortColumn.Memory, false) => rows.OrderBy(r => r.SortMemory).ThenBy(r => r.Pid),
            (SortColumn.Name, false) => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
            _ => rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
        };

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
        _selection.Select((ProcessList.SelectedItem as ProcessRowViewModel)?.LatestSample);
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
        DetailStatusText.Text = ProcessRowViewModel.StatusLabel(sample.Status);
        DetailStatusDot.Fill = ProcessRowViewModel.BrushForStatus(sample.Status);
        DetailPid.Text = sample.Pid.ToString();
        DetailThreads.Text = sample.ThreadCount?.ToString() ?? "—";
        DetailStart.Text = sample.StartTime is { } start ? FormatStart(start) : "—";

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
    }

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
