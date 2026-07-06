using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InsideOS.Services.Narration;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Insights;

/// <summary>
/// Evidence collector and feed for System Intelligence. Subscribes to the
/// services that already exist (live metrics, process monitor, timeline
/// stories) — no new monitoring — keeps small rolling windows, and asks the
/// shared <see cref="NarrationEngine"/> for the system-level interpretation
/// every few seconds. This class collects and feeds; it never reasons. Also
/// maintains the "Today's Story" summary for the observation window.
/// </summary>
public sealed class InsightService : IDisposable
{
    private const int AnalyzeEveryTicks = 3;   // metrics arrive once per second
    private const int SummaryEveryTicks = 30;
    private const int SystemCpuWindow = 180;
    private const int NetWindow = 15;
    private const int PidCpuWindow = 130;      // ~2 min of per-process samples
    private const int EventRetentionMinutes = 10;
    private const int MaxLiveInsights = 6;

    private sealed class PidStats
    {
        public string Name = "";
        public readonly List<double> Cpu = new();
        public ulong? MemoryBytes;
        public long LastTick;
    }

    private readonly LiveMetricsService _metrics;
    private readonly ProcessMonitorService _processes;
    private readonly SystemStoryService _story;
    private readonly object _state = new();

    private readonly List<double> _systemCpu = new();
    private readonly List<double> _netIn = new();
    private readonly List<double> _netOut = new();
    private readonly Dictionary<int, PidStats> _pids = new();
    private readonly List<EvidenceEvent> _events = new();
    private readonly Dictionary<int, int> _eventsSeenPerStory = new();
    private readonly Dictionary<string, double> _cpuSecondsByName = new();
    private readonly int _ownPid = Environment.ProcessId;
    private readonly DateTime _since = DateTime.Now;

    private bool? _onBattery;
    private double _cpuSum;
    private long _cpuCount;
    private double _netBytesTotal;
    private long _metricsTick;
    private long _processTick;
    private int _started;
    private volatile bool _disposed;

    private IReadOnlyList<NarratedActivity> _current = Array.Empty<NarratedActivity>();
    private DailySummary? _summary;

    /// <summary>Raised on a background thread whenever the live set actually changes.</summary>
    public event Action<IReadOnlyList<NarratedActivity>>? InsightsUpdated;

    public event Action<DailySummary>? SummaryUpdated;

    public InsightService(
        LiveMetricsService metrics,
        ProcessMonitorService processes,
        SystemStoryService story)
    {
        _metrics = metrics;
        _processes = processes;
        _story = story;
    }

    public IReadOnlyList<NarratedActivity> CurrentInsights
    {
        get { lock (_state) return _current; }
    }

    public DailySummary? CurrentSummary
    {
        get { lock (_state) return _summary; }
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _metrics.SnapshotUpdated += OnMetrics;
        _processes.ProcessesUpdated += OnProcesses;
        _story.StoryChanged += OnStoryChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        _metrics.SnapshotUpdated -= OnMetrics;
        _processes.ProcessesUpdated -= OnProcesses;
        _story.StoryChanged -= OnStoryChanged;
    }

    // ---- evidence collection ----

    private void OnMetrics(MetricsSnapshot snapshot)
    {
        if (_disposed)
            return;
        IReadOnlyList<NarratedActivity>? changed = null;
        DailySummary? summary = null;
        lock (_state)
        {
            _metricsTick++;
            if (snapshot.CpuUsagePercent is double cpu)
            {
                Push(_systemCpu, cpu, SystemCpuWindow);
                _cpuSum += cpu;
                _cpuCount++;
            }
            if (snapshot.DownloadBytesPerSecond is double dl)
            {
                Push(_netIn, dl, NetWindow);
                _netBytesTotal += dl;
            }
            if (snapshot.UploadBytesPerSecond is double ul)
            {
                Push(_netOut, ul, NetWindow);
                _netBytesTotal += ul;
            }
            if (snapshot.Battery is { } battery)
                _onBattery = battery.StateDescription.Contains("discharg", StringComparison.OrdinalIgnoreCase);

            if (_metricsTick % AnalyzeEveryTicks == 0)
                changed = AnalyzeLocked();
            if (_metricsTick % SummaryEveryTicks == 0 || _summary is null)
                summary = _summary = BuildSummaryLocked();
        }
        if (changed is not null)
            InsightsUpdated?.Invoke(changed);
        if (summary is not null)
            SummaryUpdated?.Invoke(summary);
    }

    private void OnProcesses(IReadOnlyList<ProcessSample> samples)
    {
        if (_disposed)
            return;
        lock (_state)
        {
            _processTick++;
            foreach (var s in samples)
            {
                if (!_pids.TryGetValue(s.Pid, out var stats))
                    _pids[s.Pid] = stats = new PidStats();
                stats.Name = s.Name;
                stats.LastTick = _processTick;
                stats.MemoryBytes = s.MemoryBytes;
                Push(stats.Cpu, s.CpuPercent ?? 0, PidCpuWindow);

                // "Today's Story" attribution (exclude InsideOS itself: the
                // summary describes what *you* have been running).
                if (s.Pid != _ownPid && s.CpuPercent is double c && c >= 1)
                {
                    _cpuSecondsByName.TryGetValue(s.Name, out double total);
                    _cpuSecondsByName[s.Name] = total + c / 100.0;
                }
            }
            if (_processTick % 30 == 0)
            {
                foreach (var pid in _pids.Where(kv => kv.Value.LastTick < _processTick - 5)
                             .Select(kv => kv.Key).ToList())
                    _pids.Remove(pid);
            }
        }
    }

    private void OnStoryChanged(TimelineStorySnapshot snapshot)
    {
        if (_disposed)
            return;
        lock (_state)
        {
            _eventsSeenPerStory.TryGetValue(snapshot.StoryId, out int seen);
            for (int i = seen; i < snapshot.Events.Count; i++)
            {
                var e = snapshot.Events[i];
                _events.Add(new EvidenceEvent(e.Time, snapshot.Pid, snapshot.ProcessName, e.Kind, e.Category));
            }
            _eventsSeenPerStory[snapshot.StoryId] = snapshot.Events.Count;

            var cutoff = DateTime.Now.AddMinutes(-EventRetentionMinutes);
            _events.RemoveAll(e => e.Time < cutoff);
            if (_eventsSeenPerStory.Count > 400)
                _eventsSeenPerStory.Clear(); // counts self-heal from snapshots
        }
    }

    // ---- analysis ----

    private IReadOnlyList<NarratedActivity>? AnalyzeLocked()
    {
        var context = new NarrationContext(
            DateTime.Now,
            Average(_systemCpu, 30),
            Average(_systemCpu, SystemCpuWindow),
            Average(_netIn, NetWindow) ?? 0,
            Average(_netOut, NetWindow) ?? 0,
            _onBattery,
            _events.ToArray(),
            BuildProcessEvidenceLocked());

        var candidates = NarrationEngine.NarrateSystem(context).Take(MaxLiveInsights).ToList();

        // Keep the original card (and its timestamp) when nothing about it
        // changed — the UI then leaves that card untouched: no flicker.
        var previous = _current.ToDictionary(i => i.Id);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (previous.TryGetValue(candidates[i].Id, out var old))
            {
                candidates[i] = old.Title == candidates[i].Title
                                && old.Detail == candidates[i].Detail
                                && old.Confidence == candidates[i].Confidence
                    ? old
                    : candidates[i] with { Timestamp = old.Timestamp };
            }
        }

        bool changed = candidates.Count != _current.Count
                       || candidates.Where((c, i) => !ReferenceEquals(c, _current[i]) && c != _current[i]).Any();
        if (!changed)
            return null;
        _current = candidates;
        return candidates;
    }

    private IReadOnlyList<ProcessEvidence> BuildProcessEvidenceLocked()
    {
        var list = new List<ProcessEvidence>();
        foreach (var (pid, stats) in _pids)
        {
            if (stats.Cpu.Count < 12)
                continue;
            double recent = stats.Cpu.TakeLast(10).Average();
            var priorSlice = stats.Cpu.Take(stats.Cpu.Count - 10).ToList();
            double prior = priorSlice.Count > 0 ? priorSlice.Average() : 0;
            double sustained = stats.Cpu.Average();
            if (recent < 5 && prior < 5 && (stats.MemoryBytes ?? 0) < 3UL * 1024 * 1024 * 1024)
                continue; // not interesting — keeps the evidence bundle small
            list.Add(new ProcessEvidence(pid, stats.Name, recent, prior, sustained, stats.MemoryBytes));
        }
        return list;
    }

    private DailySummary BuildSummaryLocked()
    {
        var lines = new List<string>();
        string since = _since.ToString("HH:mm");
        var top = _cpuSecondsByName
            .OrderByDescending(kv => kv.Value)
            .Where(kv => kv.Value >= 20) // at least ~20 CPU-seconds of real work
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        if (top.Count >= 2)
            lines.Add($"Since {since} you've mainly been using {top[0]} and {top[1]}.");
        else if (top.Count == 1)
            lines.Add($"Since {since}, {top[0]} has been doing most of the work.");
        else
            lines.Add($"Since {since} your applications have been mostly idle.");

        double avgCpu = _cpuCount > 0 ? _cpuSum / _cpuCount : 0;
        lines.Add(avgCpu < 20
            ? "Your computer has been relatively quiet."
            : avgCpu < 45
                ? "Your computer has been moderately busy."
                : "Your computer has been working quite hard.");

        if (top.Count >= 2)
            lines.Add($"The most active application has been {top[0]}.");

        lines.Add(_netBytesTotal < 100 * 1024 * 1024
            ? "Network usage has remained low."
            : $"About {Format.Bytes(_netBytesTotal)} has moved over the network.");

        return new DailySummary(_since, lines);
    }

    private static void Push(List<double> ring, double value, int capacity)
    {
        ring.Add(value);
        if (ring.Count > capacity)
            ring.RemoveAt(0);
    }

    private static double? Average(List<double> ring, int lastN)
    {
        if (ring.Count == 0)
            return null;
        return ring.TakeLast(Math.Min(lastN, ring.Count)).Average();
    }
}
