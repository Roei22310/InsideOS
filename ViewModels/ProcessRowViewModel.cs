using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;

namespace InsideOS.ViewModels;

/// <summary>
/// Bindable row for the process list. Instances live as long as their process
/// does; per-second updates mutate properties in place (raising change
/// notifications only for values that actually changed) so the ListBox never
/// rebuilds rows — no flicker, selection and scroll are preserved.
/// </summary>
public sealed class ProcessRowViewModel : INotifyPropertyChanged
{
    private const double CpuBarFullWidth = 44;

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
        }
        else
        {
            SortCpu = -1;
            CpuText = "—";
            CpuBarWidth = 0;
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

        StatusText = StatusLabel(sample.Status);
        StatusBrush = BrushForStatus(sample.Status);
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
