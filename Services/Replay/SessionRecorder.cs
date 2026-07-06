using System;
using System.Collections.Generic;
using System.Linq;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Replay;

/// <summary>
/// Records a session by listening to the pipelines that already run — the
/// per-second process list, the metrics snapshot, the flow snapshot of the
/// focused process, and the story events the timeline already detects. No
/// new monitoring, no polling: subscriptions attach when recording begins
/// and detach when it ends, so the recorder costs nothing while idle.
/// The Laboratory starts a recording with each experiment and completes it a
/// few seconds after the workload ends (so exit events land in the tape).
/// </summary>
public sealed class SessionRecorder
{
    private const int MaxFrames = 150; // hard cap ≈ 2.5 min — sessions are short by design

    private readonly ProcessMonitorService _processes;
    private readonly LiveMetricsService _metrics;
    private readonly ProcessFlowMonitor _flow;
    private readonly SystemStoryService _story;
    private readonly object _lock = new();

    private bool _recording;
    private string _experimentId = "";
    private string _title = "";
    private int _focusPid = -1;
    private DateTime _startedAt;
    private List<ReplayFrame> _frames = new();
    private List<ReplayEvent> _events = new();
    private readonly Dictionary<int, int> _eventsSeenPerStory = new();
    private MetricsSnapshot? _lastMetrics;
    private ProcessFlowSnapshot? _lastFlow;
    private int _tailFramesRemaining = -1; // -1 = still recording the main run

    /// <summary>The most recently completed session, replayable until replaced.</summary>
    public ReplaySession? LastSession { get; private set; }

    /// <summary>Raised (background thread) when a session finishes recording.</summary>
    public event Action<ReplaySession>? SessionCompleted;

    public SessionRecorder(
        ProcessMonitorService processes,
        LiveMetricsService metrics,
        ProcessFlowMonitor flow,
        SystemStoryService story)
    {
        _processes = processes;
        _metrics = metrics;
        _flow = flow;
        _story = story;
    }

    /// <summary>Begins recording. Called by the Laboratory when an experiment spawns.</summary>
    public void Begin(string experimentId, string title, int focusPid)
    {
        lock (_lock)
        {
            if (_recording)
                DetachLocked();
            _recording = true;
            _experimentId = experimentId;
            _title = title;
            _focusPid = focusPid;
            _startedAt = DateTime.Now;
            _frames = new List<ReplayFrame>(64);
            _events = new List<ReplayEvent>(16);
            _eventsSeenPerStory.Clear();
            _lastMetrics = null;
            _lastFlow = null;
            _tailFramesRemaining = -1;

            _processes.ProcessesUpdated += OnProcesses;
            _metrics.SnapshotUpdated += OnMetrics;
            _flow.FlowUpdated += OnFlow;
            _story.StoryChanged += OnStoryChanged;
        }
        // Flow snapshots require the monitor; starting it here also means
        // Action Flow is live during the experiment even if never opened.
        _flow.EnsureStarted();
    }

    /// <summary>
    /// Ends the recording after a short tail, so the process-exit event and
    /// the pages' settling seconds are part of the tape.
    /// </summary>
    public void CompleteAfterTail(int tailFrames = 6)
    {
        lock (_lock)
        {
            if (_recording && _tailFramesRemaining < 0)
                _tailFramesRemaining = Math.Max(0, tailFrames);
        }
    }

    // ---- capture (all handlers detach automatically at finalize) ----

    private void OnMetrics(MetricsSnapshot snapshot)
    {
        lock (_lock)
            _lastMetrics = snapshot;
    }

    private void OnFlow(ProcessFlowSnapshot snapshot)
    {
        lock (_lock)
        {
            if (snapshot.Pid == _focusPid)
                _lastFlow = snapshot;
        }
    }

    private void OnStoryChanged(TimelineStorySnapshot snapshot)
    {
        lock (_lock)
        {
            if (!_recording)
                return;
            _eventsSeenPerStory.TryGetValue(snapshot.StoryId, out int seen);
            for (int i = seen; i < snapshot.Events.Count; i++)
            {
                var evt = snapshot.Events[i];
                if (evt.Time >= _startedAt)
                    _events.Add(new ReplayEvent(snapshot.Pid, snapshot.ProcessName, evt));
            }
            _eventsSeenPerStory[snapshot.StoryId] = snapshot.Events.Count;
        }
    }

    private void OnProcesses(IReadOnlyList<ProcessSample> samples)
    {
        ReplaySession? completed = null;
        lock (_lock)
        {
            if (!_recording)
                return;

            _frames.Add(new ReplayFrame(DateTime.Now - _startedAt, samples, _lastMetrics, _lastFlow));

            if (_tailFramesRemaining > 0)
                _tailFramesRemaining--;
            if (_tailFramesRemaining == 0 || _frames.Count >= MaxFrames)
                completed = FinalizeLocked();
        }
        if (completed is not null)
            SessionCompleted?.Invoke(completed);
    }

    private ReplaySession FinalizeLocked()
    {
        DetachLocked();
        var session = new ReplaySession(
            _experimentId, _title, _startedAt, _focusPid,
            _frames, _events.OrderBy(e => e.Event.Time).ToList());
        LastSession = session;
        return session;
    }

    private void DetachLocked()
    {
        _recording = false;
        _processes.ProcessesUpdated -= OnProcesses;
        _metrics.SnapshotUpdated -= OnMetrics;
        _flow.FlowUpdated -= OnFlow;
        _story.StoryChanged -= OnStoryChanged;
    }
}
