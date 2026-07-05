using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;

namespace InsideOS.Services.History;

/// <summary>
/// The reusable history engine: fixed-capacity ring buffers fed by the
/// monitoring services that already run (live metrics + process monitor).
/// No new monitoring, no per-sample allocations — appends write into
/// preallocated arrays, and readers copy into caller-provided buffers.
/// Capacity currently covers 15 minutes at one sample/second; raising
/// <see cref="Capacity"/> (or snapshotting rings to disk) is all a longer
/// history would need. Timeline, Replay, Insights, the Dashboard and future
/// analytics can all read the same rings.
/// </summary>
public sealed class MetricHistoryService : IDisposable
{
    public const int Capacity = 920; // 15 min @ 1 Hz + slack

    private sealed class Ring
    {
        private readonly DateTime[] _times = new DateTime[Capacity];
        private readonly double[] _values = new double[Capacity];
        private int _next;
        private int _count;

        public void Add(DateTime time, double value)
        {
            _times[_next] = time;
            _values[_next] = value;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity)
                _count++;
        }

        public int Read(DateTime cutoff, DateTime[] times, double[] values)
        {
            int n = 0;
            int start = (_next - _count + Capacity) % Capacity;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % Capacity;
                if (_times[idx] < cutoff)
                    continue;
                times[n] = _times[idx];
                values[n] = _values[idx];
                n++;
            }
            return n;
        }
    }

    private sealed class PidRing
    {
        public string Name = "";
        public ProcessSample? LastSample;
        public long LastTick;
        public readonly DateTime[] Times = new DateTime[Capacity];
        public readonly double[] Cpu = new double[Capacity];
        public readonly double[] Memory = new double[Capacity]; // -1 = unknown
        public int Next;
        public int Count;

        public void Add(DateTime time, double cpu, double? memory)
        {
            Times[Next] = time;
            Cpu[Next] = cpu;
            Memory[Next] = memory ?? -1;
            Next = (Next + 1) % Capacity;
            if (Count < Capacity)
                Count++;
        }
    }

    private const int MaxTrackedPids = 60;
    private const double TrackCpuThreshold = 1.5; // start recording a pid at this load

    private readonly LiveMetricsService _metrics;
    private readonly ProcessMonitorService _processes;
    private readonly object _lock = new();
    private readonly Dictionary<HistoryMetric, Ring> _rings;
    private readonly Dictionary<int, PidRing> _pids = new();
    private long _processTick;
    private int _started;
    private volatile bool _disposed;

    /// <summary>Raised about once per second after new samples land (background thread).</summary>
    public event Action? Updated;

    public MetricHistoryService(LiveMetricsService metrics, ProcessMonitorService processes)
    {
        _metrics = metrics;
        _processes = processes;
        _rings = Enum.GetValues<HistoryMetric>().ToDictionary(m => m, _ => new Ring());
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _metrics.SnapshotUpdated += OnMetrics;
        _processes.ProcessesUpdated += OnProcesses;
    }

    public void Dispose()
    {
        _disposed = true;
        _metrics.SnapshotUpdated -= OnMetrics;
        _processes.ProcessesUpdated -= OnProcesses;
    }

    /// <summary>
    /// Copies the samples of one metric inside the window into the caller's
    /// buffers (chronological order) and returns the count. Buffers must hold
    /// <see cref="Capacity"/> entries; callers reuse them across refreshes.
    /// </summary>
    public int Read(HistoryMetric metric, TimeSpan window, DateTime[] times, double[] values)
    {
        var cutoff = DateTime.Now - window;
        lock (_lock)
            return _rings[metric].Read(cutoff, times, values);
    }

    /// <summary>The most active processes (by average CPU) over the window.</summary>
    public IReadOnlyList<ProcessHistoryStat> GetTopProcesses(TimeSpan window, int count)
    {
        var cutoff = DateTime.Now - window;
        var stats = new List<ProcessHistoryStat>();
        lock (_lock)
        {
            foreach (var (pid, ring) in _pids)
            {
                double cpuSum = 0, memSum = 0, peak = 0;
                double firstHalf = 0, secondHalf = 0;
                int n = 0, memN = 0;
                int start = (ring.Next - ring.Count + Capacity) % Capacity;
                var inWindow = new List<double>(ring.Count);
                for (int i = 0; i < ring.Count; i++)
                {
                    int idx = (start + i) % Capacity;
                    if (ring.Times[idx] < cutoff)
                        continue;
                    double cpu = ring.Cpu[idx];
                    inWindow.Add(cpu);
                    cpuSum += cpu;
                    if (cpu > peak)
                        peak = cpu;
                    if (ring.Memory[idx] >= 0)
                    {
                        memSum += ring.Memory[idx];
                        memN++;
                    }
                    n++;
                }
                if (n < 5 || ring.LastSample is null)
                    continue;
                int half = inWindow.Count / 2;
                for (int i = 0; i < inWindow.Count; i++)
                {
                    if (i < half) firstHalf += inWindow[i];
                    else secondHalf += inWindow[i];
                }
                stats.Add(new ProcessHistoryStat(
                    pid,
                    ring.Name,
                    cpuSum / n,
                    peak,
                    memN > 0 ? memSum / memN : null,
                    secondHalf / Math.Max(1, inWindow.Count - half) - firstHalf / Math.Max(1, half),
                    ring.LastSample));
            }
        }
        return stats.OrderByDescending(s => s.AvgCpu).Take(count).ToList();
    }

    private void OnMetrics(MetricsSnapshot s)
    {
        if (_disposed)
            return;
        var now = DateTime.Now;
        lock (_lock)
        {
            if (s.CpuUsagePercent is { } cpu)
                _rings[HistoryMetric.CpuUsage].Add(now, cpu);
            if (s.Memory is { } mem)
                _rings[HistoryMetric.MemoryUsed].Add(now, mem.UsedBytes);
            if (s.Disk is { } disk)
                _rings[HistoryMetric.DiskUsed].Add(now, disk.UsedBytes);
            if (s.DownloadBytesPerSecond is { } down)
                _rings[HistoryMetric.NetworkDown].Add(now, down);
            if (s.UploadBytesPerSecond is { } up)
                _rings[HistoryMetric.NetworkUp].Add(now, up);
            if (s.Battery is { } battery)
                _rings[HistoryMetric.Battery].Add(now, battery.Percent);
        }
        Updated?.Invoke();
    }

    private void OnProcesses(IReadOnlyList<ProcessSample> samples)
    {
        if (_disposed)
            return;
        var now = DateTime.Now;
        lock (_lock)
        {
            _processTick++;
            int active = 0;
            foreach (var s in samples)
            {
                double cpu = s.CpuPercent ?? 0;
                if (cpu >= 5)
                    active++;

                // Per-process history: record pids that show real activity.
                if (_pids.TryGetValue(s.Pid, out var ring))
                {
                    ring.Name = s.Name;
                    ring.LastSample = s;
                    ring.LastTick = _processTick;
                    ring.Add(now, cpu, s.MemoryBytes);
                }
                else if (cpu >= TrackCpuThreshold
                         && (_pids.Count < MaxTrackedPids || cpu >= 10))
                {
                    ring = new PidRing { Name = s.Name, LastSample = s, LastTick = _processTick };
                    ring.Add(now, cpu, s.MemoryBytes);
                    _pids[s.Pid] = ring;
                }
            }
            _rings[HistoryMetric.ProcessCount].Add(now, samples.Count);
            _rings[HistoryMetric.ActiveProcessCount].Add(now, active);

            if (_processTick % 30 == 0)
            {
                foreach (var pid in _pids
                             .Where(kv => kv.Value.LastTick < _processTick - 10)
                             .Select(kv => kv.Key).ToList())
                    _pids.Remove(pid);
            }
        }
    }
}
