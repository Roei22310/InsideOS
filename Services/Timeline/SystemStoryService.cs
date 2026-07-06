using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;

namespace InsideOS.Services.Timeline;

/// <summary>
/// The Timeline's single event source: watches the existing per-second
/// process samples (plus the per-process I/O counters already used by Action
/// Flow) and turns *real* sustained changes into meaningful events, grouped
/// into per-process stories. No fake events, no duplicated monitoring —
/// detection is pure derivation from services that already exist. Replay,
/// Analytics and future lessons can subscribe to the same stream.
/// </summary>
public sealed class SystemStoryService : IDisposable
{
    // Detection thresholds — deliberately high so the timeline reads like a
    // story, not a firehose. All deltas are measured over real samples.
    private const double CpuEventFloor = 25;   // recent average must reach this (% of one core)
    private const double CpuRiseDelta = 20;    // ...and exceed the pid's baseline by this much
    private const double CpuHighLevel = 85;
    private const ulong MemoryRiseBytes = 150UL * 1024 * 1024; // +150 MB within the window
    private const ulong NetworkEventBytes = 1536UL * 1024;     // ≥ 1.5 MB per ~2 s sample
    private const ulong NetworkBusyBytes = 20UL * 1024 * 1024;
    private const ulong DiskEventBytes = 25UL * 1024 * 1024;   // ≥ 25 MB per ~2 s sample
    private const int GroupWindowSeconds = 45; // events this close join one story
    private const int HistoryLength = 6;
    private const int MaxStories = 150;
    private const int MaxEventsPerTick = 4;    // global cap keeps the story readable
    private const int MaxDiskProbesPerTick = 50;
    private static readonly TimeSpan CpuCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MemoryCooldown = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NetworkCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DiskCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NameCooldown = TimeSpan.FromSeconds(30); // start/exit spam per name

    private sealed class PidState
    {
        public string Name = "";
        public ProcessSample? LastSample;
        public readonly List<double> CpuHistory = new();
        public readonly List<ulong> MemHistory = new();
        public int TicksSeen;
        public int TicksMissing;
        public DateTime CpuEventAt, MemEventAt, NetEventAt, DiskEventAt;
        public ulong? LastNetTotal;
        public ulong? LastDiskRead;
        public ulong? LastDiskWrite;
    }

    private sealed class Story
    {
        public int Id;
        public int Pid;
        public string ProcessName = "";
        public DateTime StartTime, LastTime;
        public readonly List<TimelineEvent> Events = new();
        public string? Explanation;
        public ProcessSample? LastSample;
    }

    private readonly ProcessMonitorService _processes;
    private readonly ProcessSelection _selection;
    private readonly IProcessIoSource _io;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<int, PidState> _pids = new();
    private readonly Dictionary<string, DateTime> _nameEventAt = new();
    private readonly List<Story> _stories = new();
    private readonly Dictionary<int, Story> _openStoryByPid = new();
    private Story? _learningStory;
    private int _tick;
    private int _nextStoryId;
    private int _started;
    private volatile bool _disposed;

    /// <summary>New or updated story. Raised on a background thread — UI must marshal.</summary>
    public event Action<TimelineStorySnapshot>? StoryChanged;

    public SystemStoryService(ProcessMonitorService processes, ProcessSelection selection, IProcessIoSource io)
    {
        _processes = processes;
        _selection = selection;
        _io = io;
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _processes.ProcessesUpdated += OnProcessesUpdated;
        _selection.Changed += OnSelectionChanged;
        _processes.EnsureStarted();
    }

    public void Dispose()
    {
        _disposed = true;
        _processes.ProcessesUpdated -= OnProcessesUpdated;
        _selection.Changed -= OnSelectionChanged;
    }

    public IReadOnlyList<TimelineStorySnapshot> GetStories()
    {
        lock (_lock)
            return _stories.Select(Snapshot).ToArray();
    }

    /// <summary>Records a learning milestone (e.g. lesson completed) on the timeline.</summary>
    public void ReportLearningEvent(string title, string detail)
    {
        var evt = new TimelineEvent(DateTime.Now, TimelineEventKind.Learning,
            TimelineCategory.Learning, title, detail, TimelineSeverity.Info);
        TimelineStorySnapshot snapshot;
        lock (_lock)
        {
            var story = _learningStory;
            if (story is null || (evt.Time - story.LastTime).TotalSeconds > GroupWindowSeconds)
            {
                story = CreateStoryLocked(-1, "Learning Journey", evt.Time);
                _learningStory = story;
            }
            AppendLocked(story, evt, sample: null);
            snapshot = Snapshot(story);
        }
        StoryChanged?.Invoke(snapshot);
    }

    private void OnSelectionChanged(ProcessSample? sample)
    {
        if (sample is null)
            return;
        ReportLearningEvent($"Started exploring {sample.Name}",
            "You selected this process — InsideOS is now following it through the operating system.");
    }

    private async void OnProcessesUpdated(IReadOnlyList<ProcessSample> samples)
    {
        if (_disposed || !await _gate.WaitAsync(0))
            return; // still busy with the previous tick — skip, never queue up
        try
        {
            await ProcessTickAsync(samples);
        }
        catch
        {
            // One bad tick must not kill the story loop.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessTickAsync(IReadOnlyList<ProcessSample> samples)
    {
        _tick++;
        var now = DateTime.Now;
        bool baselineTick = _tick == 1; // first sight of the world — record, don't report
        int budget = MaxEventsPerTick;
        int diskProbes = 0;
        var seen = new HashSet<int>();
        var detected = new List<(int Pid, string Name, ProcessSample? Sample, TimelineEvent Evt)>();

        // Per-process network counters come from one nettop snapshot for all
        // pids, every other tick (~10 ms one-shot, same source Action Flow uses).
        IReadOnlyDictionary<int, NetworkCounters>? net = null;
        if (_tick % 2 == 0 && !_disposed)
            net = await _io.ReadNetworkCountersAsync(CancellationToken.None);

        foreach (var s in samples)
        {
            seen.Add(s.Pid);
            if (!_pids.TryGetValue(s.Pid, out var st))
            {
                st = new PidState { Name = s.Name };
                _pids[s.Pid] = st;
                if (!baselineTick && budget > 0 && AllowNameEvent(s.Name, now))
                {
                    budget--;
                    detected.Add((s.Pid, s.Name, s, new TimelineEvent(now,
                        TimelineEventKind.ProcessStarted, TimelineCategory.Process,
                        "Process started",
                        "The operating system created this process and gave it memory and a share of CPU time.",
                        TimelineSeverity.Info)));
                }
            }
            st.TicksSeen++;
            st.TicksMissing = 0;
            st.Name = s.Name;
            st.LastSample = s;

            // --- CPU: sustained rise above this process's own baseline ---
            if (s.CpuPercent is double cpu)
            {
                st.CpuHistory.Add(cpu);
                if (st.CpuHistory.Count > HistoryLength)
                    st.CpuHistory.RemoveAt(0);
                if (st.CpuHistory.Count >= 4 && budget > 0 && now - st.CpuEventAt >= CpuCooldown)
                {
                    double recent = (st.CpuHistory[^1] + st.CpuHistory[^2]) / 2;
                    double baseline = st.CpuHistory.Take(st.CpuHistory.Count - 2).Average();
                    if (recent >= CpuEventFloor && recent >= baseline + CpuRiseDelta)
                    {
                        st.CpuEventAt = now;
                        budget--;
                        detected.Add((s.Pid, s.Name, s, new TimelineEvent(now,
                            TimelineEventKind.CpuRise, TimelineCategory.Cpu,
                            "CPU usage increased",
                            $"Now using about {Math.Round(recent)}% of a CPU core.",
                            recent >= CpuHighLevel ? TimelineSeverity.High : TimelineSeverity.Notice)));
                    }
                }
            }

            // --- Memory: real growth across the sampling window ---
            if (s.MemoryBytes is ulong mem)
            {
                st.MemHistory.Add(mem);
                if (st.MemHistory.Count > HistoryLength)
                    st.MemHistory.RemoveAt(0);
                if (st.MemHistory.Count >= HistoryLength && budget > 0 && now - st.MemEventAt >= MemoryCooldown)
                {
                    ulong oldest = st.MemHistory[0];
                    if (mem > oldest && mem - oldest >= MemoryRiseBytes)
                    {
                        st.MemEventAt = now;
                        budget--;
                        detected.Add((s.Pid, s.Name, s, new TimelineEvent(now,
                            TimelineEventKind.MemoryRise, TimelineCategory.Memory,
                            "Memory usage increased",
                            $"Grew by about {Format.Bytes(mem - oldest)} in the last few seconds.",
                            TimelineSeverity.Notice)));
                    }
                }
            }

            // --- Network: delta between consecutive nettop readings ---
            if (net is not null && net.TryGetValue(s.Pid, out var counters))
            {
                ulong total = counters.BytesIn + counters.BytesOut;
                if (st.LastNetTotal is ulong prevNet && total > prevNet)
                {
                    ulong delta = total - prevNet;
                    if (delta >= NetworkEventBytes && budget > 0 && now - st.NetEventAt >= NetworkCooldown)
                    {
                        st.NetEventAt = now;
                        budget--;
                        detected.Add((s.Pid, s.Name, s, new TimelineEvent(now,
                            TimelineEventKind.NetworkActivity, TimelineCategory.Network,
                            "Network activity detected",
                            $"About {Format.Bytes(delta)} exchanged with the network just now.",
                            delta >= NetworkBusyBytes ? TimelineSeverity.Notice : TimelineSeverity.Info)));
                    }
                }
                st.LastNetTotal = total;
            }

            // --- Disk: cheap per-pid syscall, probed only for active pids
            //     (macOS only permits it for this user's processes) ---
            if (_tick % 2 == 0 && diskProbes < MaxDiskProbesPerTick
                && ((s.CpuPercent ?? 0) >= 3 || _openStoryByPid.ContainsKey(s.Pid)))
            {
                diskProbes++;
                if (_io.ReadDiskIo(s.Pid) is { } dio)
                {
                    ulong readDelta = st.LastDiskRead is ulong prevRead && dio.BytesRead > prevRead
                        ? dio.BytesRead - prevRead : 0;
                    ulong writeDelta = st.LastDiskWrite is ulong prevWrite && dio.BytesWritten > prevWrite
                        ? dio.BytesWritten - prevWrite : 0;
                    ulong delta = readDelta + writeDelta;
                    if (st.LastDiskRead is not null && delta >= DiskEventBytes
                        && budget > 0 && now - st.DiskEventAt >= DiskCooldown)
                    {
                        st.DiskEventAt = now;
                        budget--;
                        bool mostlyWrite = writeDelta >= readDelta;
                        detected.Add((s.Pid, s.Name, s, new TimelineEvent(now,
                            mostlyWrite ? TimelineEventKind.DiskWrite : TimelineEventKind.DiskRead,
                            TimelineCategory.Disk,
                            "Disk activity detected",
                            $"About {Format.Bytes(delta)} {(mostlyWrite ? "written to" : "read from")} disk just now.",
                            TimelineSeverity.Notice)));
                    }
                    st.LastDiskRead = dio.BytesRead;
                    st.LastDiskWrite = dio.BytesWritten;
                }
            }
        }

        // --- Exits: pid gone for two consecutive ticks ---
        List<int>? gone = null;
        foreach (var (pid, st) in _pids)
        {
            if (seen.Contains(pid))
                continue;
            st.TicksMissing++;
            if (st.TicksMissing < 2)
                continue;
            (gone ??= new List<int>()).Add(pid);
            // An exit that completes a story we already began telling is never
            // spam — a lifecycle deserves its ending. The per-name cooldown
            // only guards against start/exit churn across *different* pids
            // (respawning helper daemons). Without this, any process living
            // less than the cooldown lost its exit event to its own start.
            bool completesStory;
            lock (_lock)
            {
                completesStory = _openStoryByPid.TryGetValue(pid, out var open)
                    && (now - open.LastTime).TotalSeconds <= GroupWindowSeconds;
            }
            if (st.TicksSeen >= 3 && budget > 0 && (completesStory || AllowNameEvent(st.Name, now)))
            {
                budget--;
                detected.Add((pid, st.Name, st.LastSample, new TimelineEvent(now,
                    TimelineEventKind.ProcessEnded, TimelineCategory.Process,
                    "Process ended",
                    "The process exited and the operating system reclaimed its memory.",
                    TimelineSeverity.Info)));
            }
        }
        if (gone is not null)
            foreach (var pid in gone)
                _pids.Remove(pid);

        if (detected.Count == 0 || _disposed)
            return;

        // --- Group into stories (same pid within the window = one story) ---
        var changedIds = new List<int>();
        var snapshots = new List<TimelineStorySnapshot>();
        lock (_lock)
        {
            foreach (var (pid, name, sample, evt) in detected)
            {
                if (!_openStoryByPid.TryGetValue(pid, out var story)
                    || (evt.Time - story.LastTime).TotalSeconds > GroupWindowSeconds)
                {
                    story = CreateStoryLocked(pid, name, evt.Time);
                    _openStoryByPid[pid] = story;
                }
                AppendLocked(story, evt, sample);
                if (!changedIds.Contains(story.Id))
                    changedIds.Add(story.Id);
            }
            foreach (var id in changedIds)
            {
                var story = _stories.FirstOrDefault(x => x.Id == id);
                if (story is not null)
                    snapshots.Add(Snapshot(story));
            }
        }
        foreach (var snapshot in snapshots)
            StoryChanged?.Invoke(snapshot);
    }

    private bool AllowNameEvent(string name, DateTime now)
    {
        // Helper daemons respawn constantly; one start/exit event per name
        // per cooldown keeps the story honest without the spam.
        if (_nameEventAt.TryGetValue(name, out var last) && now - last < NameCooldown)
            return false;
        _nameEventAt[name] = now;
        if (_nameEventAt.Count > 800)
            _nameEventAt.Clear();
        return true;
    }

    private Story CreateStoryLocked(int pid, string name, DateTime time)
    {
        var story = new Story { Id = _nextStoryId++, Pid = pid, ProcessName = name, StartTime = time, LastTime = time };
        _stories.Add(story);
        if (_stories.Count > MaxStories)
        {
            var oldest = _stories[0];
            _stories.RemoveAt(0);
            if (_openStoryByPid.TryGetValue(oldest.Pid, out var open) && ReferenceEquals(open, oldest))
                _openStoryByPid.Remove(oldest.Pid);
            if (ReferenceEquals(_learningStory, oldest))
                _learningStory = null;
        }
        return story;
    }

    private static void AppendLocked(Story story, TimelineEvent evt, ProcessSample? sample)
    {
        story.Events.Add(evt);
        story.LastTime = evt.Time;
        story.LastSample = sample ?? story.LastSample;
        // The Narration Engine is the single interpreter for all activity —
        // this story text is the same interpretation Insights and Action Flow
        // derive from the same evidence.
        story.Explanation = Narration.NarrationEngine
            .NarrateStory(story.ProcessName, story.Events)?.Summary;
    }

    private static TimelineStorySnapshot Snapshot(Story story) => new(
        story.Id,
        story.Pid,
        story.ProcessName,
        story.StartTime,
        story.LastTime,
        story.Events.ToArray(),
        story.Explanation,
        story.Events.Max(e => e.Severity),
        story.Events.Select(e => e.Category).Distinct().ToArray(),
        story.LastSample);
}
