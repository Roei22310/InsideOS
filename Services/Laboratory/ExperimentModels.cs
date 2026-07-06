using System;
using System.Collections.Generic;
using System.Linq;

namespace InsideOS.Services.Laboratory;

/// <summary>How an experiment run ended.</summary>
public enum ExperimentOutcome
{
    Completed, // the workload ran its full course and exited on its own
    Stopped,   // the user ended it early; the child was terminated immediately
    Failed,    // the workload could not run (or exited abnormally) — explained, never retried silently
}

/// <summary>
/// One phase of a workload schedule: how long the helper process behaves a
/// certain way and how much of one core it uses while doing so (0 = truly
/// idle). Experiments are just sequences of phases, so a new experiment is
/// new data, not new code.
/// </summary>
public sealed record ExperimentPhase(string Title, TimeSpan Duration, int DutyPercent);

/// <summary>
/// A caption shown while an experiment is running, keyed by elapsed time.
/// Lets a definition narrate its own phases ("still quiet…", "now working…")
/// without the page knowing anything about the workload.
/// </summary>
public sealed record ExperimentPhaseCaption(TimeSpan At, string Caption);

/// <summary>
/// A fully data-driven description of one Laboratory experiment: all the
/// educational copy, plus the phase schedule the child process executes.
/// The workload arguments, expected duration and observation windows all
/// derive from <see cref="Phases"/> — no experiment-specific logic exists
/// anywhere else.
/// </summary>
public sealed record ExperimentDefinition(
    string Id,
    string Category,               // short kicker, e.g. "CPU"
    string Title,                  // the "What happens if…" question
    string AboutToObserve,         // shown before running: what we are about to watch
    string WhyInteresting,         // shown before running: why it is worth watching
    IReadOnlyList<string> WhatToWatch,   // where in the app the response will be visible
    IReadOnlyList<string> WhatYouLearned, // shown after a completed run: the concepts demonstrated
    string DurationText,           // human copy, e.g. "About 30 seconds"
    string IntensityText,          // honest resource statement, e.g. "Uses part of one CPU core"
    string WorkloadKind,           // label carried to the child process (see LabWorker)
    IReadOnlyList<ExperimentPhase> Phases,
    IReadOnlyList<ExperimentPhaseCaption> PhaseCaptions)
{
    /// <summary>Duty at or above which a phase counts as "working" for the
    /// averaged-CPU observation (light initialization phases are excluded).</summary>
    public const int ActiveDutyFloor = 25;

    /// <summary>Total scheduled lifetime; the child self-terminates here.</summary>
    public TimeSpan ExpectedDuration =>
        TimeSpan.FromSeconds(Phases.Sum(p => p.Duration.TotalSeconds));

    /// <summary>Workload arguments the child process parses ("duty:seconds" per phase).</summary>
    public IReadOnlyList<string> WorkloadArgs =>
        Phases.Select(p => $"{p.DutyPercent}:{(int)p.Duration.TotalSeconds}").ToArray();

    /// <summary>The scheduled duty at a point in the run (0 beyond the end).</summary>
    public int DutyAt(TimeSpan elapsed)
    {
        var t = TimeSpan.Zero;
        foreach (var phase in Phases)
        {
            t += phase.Duration;
            if (elapsed < t)
                return phase.DutyPercent;
        }
        return 0;
    }
}

/// <summary>
/// What actually happened during a run — only measured values. A null means
/// the value was never observed, and the UI says so instead of inventing it.
/// </summary>
public sealed record ExperimentResult(
    string ExperimentId,
    ExperimentOutcome Outcome,
    DateTime StartedAt,
    TimeSpan Elapsed,
    int? WorkerPid,
    double? WorkerPeakCpu,             // % of one core, highest observed sample
    double? WorkerAvgCpuWhileWorking,  // % of one core, averaged over the working phases
    int? WorkerThreads,
    bool CpuWasPrecise,                // true = libproc per-second delta (own process)
    double? SystemCpuBefore,           // whole-system CPU just before the run
    double? SystemCpuPeak,             // whole-system CPU peak during the run
    int SamplesObserved,
    string? FailureReason);
