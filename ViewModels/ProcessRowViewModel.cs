using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;

namespace InsideOS.ViewModels;

/// <summary>How the process list is filtered. Ordered from broadest to narrowest.</summary>
public enum ProcessFilter
{
    All,
    Running,
    RecentlyActive,
    Sleeping,
}

/// <summary>
/// Bindable row for the process list. Instances live as long as their process
/// does; per-second updates mutate properties in place (raising change
/// notifications only for values that actually changed) so the ListBox never
/// rebuilds rows — no flicker, selection and scroll are preserved.
/// </summary>
public sealed class ProcessRowViewModel : INotifyPropertyChanged
{
    private const double CpuBarFullWidth = 44;

    // "Active" = measurably using a core; "recently active" remembers a burst
    // for a short window so a process that just did work doesn't instantly sink.
    private const double ActiveCpuThreshold = 5;
    private const int RecentlyActiveWindowTicks = 12;

    public static readonly IBrush LoadNormal = new SolidColorBrush(Color.Parse("#4D9FFF"));
    public static readonly IBrush LoadWarn = new SolidColorBrush(Color.Parse("#E8B44C"));
    public static readonly IBrush LoadHot = new SolidColorBrush(Color.Parse("#E85C5C"));
    private static readonly IBrush StatusGreen = new SolidColorBrush(Color.Parse("#3FBF7F"));
    private static readonly IBrush StatusMuted = new SolidColorBrush(Color.Parse("#656F82"));
    private static readonly IBrush StatusAmber = new SolidColorBrush(Color.Parse("#E8B44C"));
    private static readonly IBrush StatusRed = new SolidColorBrush(Color.Parse("#E85C5C"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Pid { get; }
    public string PidText { get; }

    private string _name;
    public string Name { get => _name; private set => Set(ref _name, value, nameof(Name)); }

    private string _cpuText = "—";
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value, nameof(CpuText)); }

    private double _cpuBarWidth;
    public double CpuBarWidth { get => _cpuBarWidth; private set => Set(ref _cpuBarWidth, value, nameof(CpuBarWidth)); }

    private IBrush _cpuBarBrush = LoadNormal;
    public IBrush CpuBarBrush { get => _cpuBarBrush; private set => Set(ref _cpuBarBrush, value, nameof(CpuBarBrush)); }

    private string _memoryText = "—";
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value, nameof(MemoryText)); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value, nameof(StatusText)); }

    private IBrush _statusBrush = StatusMuted;
    public IBrush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value, nameof(StatusBrush)); }

    /// <summary>Raw values for sorting and the details panel.</summary>
    public double SortCpu { get; private set; } = -1;
    public ulong SortMemory { get; private set; }
    public ProcessSample LatestSample { get; private set; }

    private int _recentlyActiveTicks;

    /// <summary>Using a measurable share of a core right now.</summary>
    public bool IsCurrentlyActive => SortCpu >= ActiveCpuThreshold;

    /// <summary>Doing processor work across the current sample window.
    /// Derived from the same <see cref="ProcessSample.EffectiveStatus"/> the
    /// STATUS column displays, so the filter and the visible label can never
    /// disagree.</summary>
    public bool IsRunningNow => LatestSample.EffectiveStatus == ProcessStatus.Running;

    /// <summary>Was active within the last few seconds (smoothes brief bursts).</summary>
    public bool WasRecentlyActive => _recentlyActiveTicks > 0;

    /// <summary>Genuinely dormant over the sample window — same single source
    /// of truth as the displayed label, so Running and Sleeping stay disjoint
    /// by construction.</summary>
    public bool IsSleeping => LatestSample.EffectiveStatus
        is ProcessStatus.Sleeping or ProcessStatus.Idle or ProcessStatus.Waiting;

    /// <summary>
    /// A process this user owns (a "user application") rather than a system
    /// daemon. Only libproc-derived values are reliable ownership signals:
    /// macOS exposes an exact CPU delta and thread count only for our own
    /// processes. (Start time is NOT a signal — ps reports it for every
    /// process, so it would misclassify root daemons as user apps.)
    /// </summary>
    public bool IsUserApplication => LatestSample.CpuIsPrecise
        || LatestSample.ThreadCount is not null;

    /// <summary>
    /// Priority bucket for smart sorting: currently active first, then recently
    /// active, then the user's own idle apps, then everything else. Within a
    /// bucket the page tie-breaks by CPU and memory.
    /// </summary>
    public int SmartTier =>
        IsCurrentlyActive ? 3
        : WasRecentlyActive ? 2
        : IsUserApplication ? 1
        : 0;

    public bool MatchesFilter(ProcessFilter filter) => filter switch
    {
        ProcessFilter.Running => IsRunningNow,
        ProcessFilter.RecentlyActive => WasRecentlyActive,
        ProcessFilter.Sleeping => IsSleeping,
        _ => true,
    };

    public ProcessRowViewModel(ProcessSample sample)
    {
        Pid = sample.Pid;
        PidText = sample.Pid.ToString();
        _name = sample.Name;
        LatestSample = sample;
        Update(sample);
    }

    public void Update(ProcessSample sample)
    {
        LatestSample = sample;
        Name = sample.Name;

        if (sample.CpuPercent is { } cpu)
        {
            SortCpu = cpu;
            CpuText = $"{cpu:0.0}%";
            CpuBarWidth = System.Math.Clamp(cpu, 0, 100) / 100 * CpuBarFullWidth;
            CpuBarBrush = BrushForLoad(cpu);
            if (cpu >= ActiveCpuThreshold)
                _recentlyActiveTicks = RecentlyActiveWindowTicks;
            else if (_recentlyActiveTicks > 0)
                _recentlyActiveTicks--;
        }
        else
        {
            SortCpu = -1;
            CpuText = "—";
            CpuBarWidth = 0;
            if (_recentlyActiveTicks > 0)
                _recentlyActiveTicks--;
        }

        if (sample.MemoryBytes is { } memory)
        {
            SortMemory = memory;
            MemoryText = Format.Bytes(memory);
        }
        else
        {
            SortMemory = 0;
            MemoryText = "—";
        }

        StatusText = StatusLabel(sample.EffectiveStatus);
        StatusBrush = BrushForStatus(sample.EffectiveStatus);
    }

    public static IBrush BrushForLoad(double cpuPercent) =>
        cpuPercent >= 85 ? LoadHot : cpuPercent >= 60 ? LoadWarn : LoadNormal;

    public static IBrush BrushForStatus(ProcessStatus status) => status switch
    {
        ProcessStatus.Running => StatusGreen,
        ProcessStatus.Stopped => StatusAmber,
        ProcessStatus.Zombie => StatusRed,
        _ => StatusMuted,
    };

    public static string StatusLabel(ProcessStatus status) => status switch
    {
        ProcessStatus.Running => "Running",
        ProcessStatus.Sleeping => "Sleeping",
        ProcessStatus.Idle => "Idle",
        ProcessStatus.Stopped => "Stopped",
        ProcessStatus.Waiting => "Waiting",
        ProcessStatus.Zombie => "Zombie",
        _ => "Unknown",
    };

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
