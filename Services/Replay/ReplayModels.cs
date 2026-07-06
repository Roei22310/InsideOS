using System;
using System.Collections.Generic;
using System.Threading;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Replay;

/// <summary>
/// One second of a recorded session: exactly the data the live pipelines
/// emitted at that moment. Frames carry raw evidence only — no explanations,
/// no interpretations; the Narration Engine runs again during replay.
/// </summary>
public sealed record ReplayFrame(
    TimeSpan Offset,
    IReadOnlyList<ProcessSample> Samples,
    MetricsSnapshot? Metrics,
    ProcessFlowSnapshot? Flow);

/// <summary>A timeline event as it was detected during the recording.</summary>
public sealed record ReplayEvent(
    int Pid,
    string ProcessName,
    TimelineEvent Event);

/// <summary>
/// A fully recorded session, replayable any number of times. Everything in
/// here was measured live when it happened — replay reproduces it through
/// the same pipelines, and narration is recomputed from this evidence.
/// </summary>
public sealed record ReplaySession(
    string ExperimentId,
    string Title,
    DateTime StartedAt,
    int FocusPid,
    IReadOnlyList<ReplayFrame> Frames,
    IReadOnlyList<ReplayEvent> Events)
{
    public TimeSpan Duration => Frames.Count > 0 ? Frames[^1].Offset : TimeSpan.Zero;
}

/// <summary>
/// The one shared fact the whole app can ask: are we looking at the past,
/// and if so, at which moment? Stateful services (story, insights, history,
/// laboratory) gate their live ingestion on <see cref="IsReplaying"/> so
/// replayed frames can never contaminate live state, and time-windowed views
/// bound themselves to <see cref="ReplayNow"/> so every page shows the same
/// instant. Pages themselves never need this — they just render their feeds.
/// </summary>
public sealed class ReplayState
{
    private volatile bool _isReplaying;
    private long _replayNowTicks;

    public bool IsReplaying
    {
        get => _isReplaying;
        internal set => _isReplaying = value;
    }

    public DateTime ReplayNow
    {
        get => new(Interlocked.Read(ref _replayNowTicks));
        internal set => Interlocked.Exchange(ref _replayNowTicks, value.Ticks);
    }
}
