using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.History;
using InsideOS.Services.Insights;
using InsideOS.Services.Laboratory;
using InsideOS.Services.Narration;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Replay;

/// <summary>Where a replay currently stands.</summary>
public enum ReplayPlayback
{
    Inactive,
    Playing,
    Paused,
    Ended, // frozen on the session's exact final state until Restart or Exit
}

/// <summary>
/// Drives a recorded session back through the application. One dispatcher
/// timer (which exists only while a replay is active — zero cost otherwise)
/// advances one frame per second and injects it into the same events the
/// live pipelines raise, so every page renders the past without knowing it.
/// Stateful services pause live ingestion via <see cref="ReplayState"/> and
/// serve as-of-the-moment views; insights and story explanations are
/// recomputed each tick by the Narration Engine from recorded evidence —
/// nothing interpreted is ever played back from tape.
/// </summary>
public sealed class ReplayService
{
    private readonly ReplayState _state;
    private readonly ProcessMonitorService _processes;
    private readonly LiveMetricsService _metrics;
    private readonly ProcessFlowMonitor _flow;
    private readonly SystemStoryService _story;
    private readonly InsightService _insights;
    private readonly MetricHistoryService _history;
    private readonly SessionRecorder _recorder;
    private readonly LaboratoryService _lab;

    private DispatcherTimer? _timer;
    private int _frameIndex;
    private int _lastAppliedIndex = -1;
    private DateTime _previousNow;

    public ReplaySession? Session { get; private set; }

    public ReplayPlayback Playback { get; private set; } = ReplayPlayback.Inactive;

    /// <summary>Offset into the session of the frame currently on screen.</summary>
    public TimeSpan Position { get; private set; }

    /// <summary>Raised on the UI thread whenever playback state or position changes.</summary>
    public event Action? Changed;

    /// <summary>Raised after a seek jump — consumers with rolling evidence
    /// windows (the moment narrator) reset so interpretations are always
    /// regenerated from the sought moment, never smoothed across a jump.</summary>
    public event Action? Sought;

    public ReplayService(
        ReplayState state,
        ProcessMonitorService processes,
        LiveMetricsService metrics,
        ProcessFlowMonitor flow,
        SystemStoryService story,
        InsightService insights,
        MetricHistoryService history,
        SessionRecorder recorder,
        LaboratoryService lab)
    {
        _state = state;
        _processes = processes;
        _metrics = metrics;
        _flow = flow;
        _story = story;
        _insights = insights;
        _history = history;
        _recorder = recorder;
        _lab = lab;
        // A freshly recorded session should appear on the Replay page without
        // a re-visit; Changed is the page's single refresh signal.
        _recorder.SessionCompleted += _ =>
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>The session that would play — the last recorded experiment.</summary>
    public ReplaySession? AvailableSession => Session ?? _recorder.LastSession;

    /// <summary>
    /// Enters replay of the last recorded session and starts playing from the
    /// beginning. Refused while an experiment is running — one reality at a time.
    /// </summary>
    public bool Start()
    {
        if (_state.IsReplaying)
            return true; // already in the past
        var session = _recorder.LastSession;
        if (session is null || session.Frames.Count == 0 || _lab.RunningDefinition is not null)
            return false;

        Session = session;
        _state.ReplayNow = session.StartedAt;
        _previousNow = session.StartedAt;
        _state.IsReplaying = true;

        _processes.EnterReplay();
        _metrics.EnterReplay();
        _flow.EnterReplay();
        _insights.EnterReplay();
        _story.NotifyReplayTransition(); // pages reload into the as-of view

        Position = TimeSpan.Zero;
        Playback = ReplayPlayback.Playing;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        ApplyFrame(0, seek: false); // frame 0 appears immediately
        _frameIndex = 1;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Jumps straight to the recorded moment nearest <paramref name="position"/>
    /// — no intermediate frames are replayed. The same frame application the
    /// player uses; every page lands on that instant. Playback stays paused
    /// afterwards, ready to resume from there.
    /// </summary>
    public void SeekTo(TimeSpan position)
    {
        var session = Session;
        if (session is null || !_state.IsReplaying || session.Frames.Count == 0)
            return;

        int index = FrameIndexFor(session, position);
        _timer?.Stop();
        if (Playback is ReplayPlayback.Playing or ReplayPlayback.Ended)
            Playback = ReplayPlayback.Paused;

        if (index != _lastAppliedIndex)
        {
            ApplyFrame(index, seek: true);
            Sought?.Invoke();
        }
        _frameIndex = index + 1;
        Changed?.Invoke();
    }

    /// <summary>Called when the user grabs the scrubber: playback pauses and
    /// stays paused until they press Play again.</summary>
    public void PauseForScrub()
    {
        if (Playback is ReplayPlayback.Playing or ReplayPlayback.Ended)
        {
            _timer?.Stop();
            Playback = ReplayPlayback.Paused;
            Changed?.Invoke();
        }
    }

    private static int FrameIndexFor(ReplaySession session, TimeSpan position)
    {
        // Frames are one second apart and chronologically ordered: last frame
        // at or before the requested position (clamped to the tape).
        for (int i = session.Frames.Count - 1; i >= 0; i--)
        {
            if (session.Frames[i].Offset <= position + TimeSpan.FromMilliseconds(250))
                return i;
        }
        return 0;
    }

    public void TogglePlayPause()
    {
        switch (Playback)
        {
            case ReplayPlayback.Playing:
                _timer?.Stop();
                Playback = ReplayPlayback.Paused;
                break;
            case ReplayPlayback.Paused:
                _timer?.Start();
                Playback = ReplayPlayback.Playing;
                break;
            case ReplayPlayback.Ended:
                Restart();
                return;
            default:
                return;
        }
        Changed?.Invoke();
    }

    public void Restart()
    {
        if (Session is null || !_state.IsReplaying)
            return;
        Position = TimeSpan.Zero;
        _state.ReplayNow = Session.StartedAt;
        _previousNow = Session.StartedAt;
        _story.NotifyReplayTransition(); // timeline collapses back to the session's start
        Playback = ReplayPlayback.Playing;
        ApplyFrame(0, seek: false);
        Sought?.Invoke(); // a restart is a jump to 0:00 — evidence windows reset
        _frameIndex = 1;
        _timer?.Start();
        Changed?.Invoke();
    }

    /// <summary>Leaves the past. Every feed returns to live within one tick.</summary>
    public void Exit()
    {
        if (!_state.IsReplaying)
            return;
        _timer?.Stop();
        _timer = null;
        _state.IsReplaying = false;

        _processes.ExitReplay();
        _metrics.ExitReplay();
        _flow.ExitReplay();       // triggers an immediate live flow sample
        _insights.ExitReplay();   // restores the saved live insight set
        _story.NotifyReplayTransition(); // pages reload the full live story set

        Playback = ReplayPlayback.Inactive;
        Position = TimeSpan.Zero;
        Session = null;
        Changed?.Invoke();
    }

    // ---- playback ----

    private void Tick()
    {
        if (Session is null || Playback != ReplayPlayback.Playing)
            return;
        if (_frameIndex >= Session.Frames.Count)
        {
            // The tape ends exactly on the final recorded state and stays there.
            _timer?.Stop();
            Playback = ReplayPlayback.Ended;
            Changed?.Invoke();
            return;
        }
        ApplyFrame(_frameIndex, seek: false);
        _frameIndex++;
        Changed?.Invoke();
    }

    private void ApplyFrame(int index, bool seek)
    {
        var session = Session!;
        var frame = session.Frames[index];
        _lastAppliedIndex = index;
        Position = frame.Offset;
        var now = session.StartedAt + frame.Offset;
        _state.ReplayNow = now;

        if (seek)
        {
            // A jump: the story set changes wholesale (backwards shrinks it),
            // so pages reload the as-of view instead of patching forward.
            _story.NotifyReplayTransition();
        }
        else
        {
            // Natural playback: stories that gained events inside this window
            // grow on screen, re-narrated from truncated evidence — like live.
            _story.EmitReplayWindow(_previousNow, now);
        }
        _previousNow = now;

        _processes.InjectReplay(frame.Samples);
        if (frame.Metrics is { } metrics)
            _metrics.InjectReplay(metrics);
        _flow.InjectReplay(frame.Flow);
        _history.RaiseReplayTick(); // charts re-read their as-of-the-moment view

        // System-level narration, always recomputed from recorded evidence —
        // on every jump, and every third frame during playback.
        if (seek || index % 3 == 0)
            _insights.PublishReplay(NarrationEngine.NarrateSystem(BuildContextAt(index, now)));
    }

    /// <summary>
    /// Rebuilds the system-level evidence bundle as it stood at the replayed
    /// moment, purely from the recorded frames and events.
    /// </summary>
    private NarrationContext BuildContextAt(int frameIndex, DateTime now)
    {
        var session = Session!;
        var upTo = frameIndex + 1;

        var cpuSeries = new List<double>();
        var netIn = new List<double>();
        var netOut = new List<double>();
        bool? onBattery = null;
        for (int i = 0; i < upTo && i < session.Frames.Count; i++)
        {
            if (session.Frames[i].Metrics is not { } m)
                continue;
            if (m.CpuUsagePercent is { } cpu)
                cpuSeries.Add(cpu);
            if (m.DownloadBytesPerSecond is { } dl)
                netIn.Add(dl);
            if (m.UploadBytesPerSecond is { } ul)
                netOut.Add(ul);
            if (m.Battery is { } battery)
                onBattery = battery.StateDescription.Contains("discharg", StringComparison.OrdinalIgnoreCase);
        }

        var events = session.Events
            .Where(e => e.Event.Time <= now)
            .Select(e => new EvidenceEvent(e.Event.Time, e.Pid, e.ProcessName, e.Event.Kind, e.Event.Category))
            .ToList();

        // Per-process CPU windows from the recorded sample lists.
        var byPid = new Dictionary<int, (string Name, List<double> Cpu, ulong? Mem)>();
        for (int i = 0; i < upTo && i < session.Frames.Count; i++)
        {
            foreach (var sample in session.Frames[i].Samples)
            {
                if ((sample.CpuPercent ?? 0) < 1 && sample.Pid != session.FocusPid)
                    continue; // keep the reconstruction small — quiet pids carry no evidence
                if (!byPid.TryGetValue(sample.Pid, out var entry))
                    byPid[sample.Pid] = entry = (sample.Name, new List<double>(), null);
                entry.Cpu.Add(sample.CpuPercent ?? 0);
                byPid[sample.Pid] = (sample.Name, entry.Cpu, sample.MemoryBytes ?? entry.Mem);
            }
        }
        var processes = new List<ProcessEvidence>();
        foreach (var (pid, entry) in byPid)
        {
            if (entry.Cpu.Count < 12)
                continue;
            double recent = entry.Cpu.TakeLast(10).Average();
            var prior = entry.Cpu.Take(entry.Cpu.Count - 10).ToList();
            processes.Add(new ProcessEvidence(
                pid, entry.Name, recent,
                prior.Count > 0 ? prior.Average() : 0,
                entry.Cpu.Average(), entry.Mem));
        }

        return new NarrationContext(
            now,
            Tail(cpuSeries, 30),
            cpuSeries.Count > 0 ? cpuSeries.Average() : null,
            Tail(netIn, 15) ?? 0,
            Tail(netOut, 15) ?? 0,
            onBattery,
            events,
            processes);
    }

    private static double? Tail(List<double> series, int lastN) =>
        series.Count == 0 ? null : series.TakeLast(Math.Min(lastN, series.Count)).Average();
}
