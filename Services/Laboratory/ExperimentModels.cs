using System;
using System.Collections.Generic;

namespace InsideOS.Services.Laboratory;

/// <summary>How an experiment run ended.</summary>
public enum ExperimentOutcome
{
    Completed, // the workload ran its full course and exited on its own
    Stopped,   // the user ended it early; the child was terminated immediately
    Failed,    // the workload could not run (or exited abnormally) — explained, never retried silently
}

/// <summary>
/// A caption shown while an experiment is running, keyed by elapsed time.
/// Lets a definition narrate its own phases ("still quiet…", "now working…")
/// without the page knowing anything about the workload.
/// </summary>
public sealed record ExperimentPhaseCaption(TimeSpan At, string Caption);

/// <summary>
/// A fully data-driven description of one Laboratory experiment: all the
/// educational copy, plus the workload recipe the child process executes.
/// Future experiments (memory, disk, network, process lifecycle) are new
/// instances of this record with a different <see cref="WorkloadKind"/> —
/// no new infrastructure required.
/// </summary>
public sealed record ExperimentDefinition(
    string Id,
    string Category,               // short kicker, e.g. "CPU"
    string Title,                  // the "What happens if…" question
    string AboutToObserve,         // shown before running: what we are about to watch
    string WhyInteresting,         // shown before running: why it is worth watching
    IReadOnlyList<string> WhatToWatch, // where in the app the response will be visible
    string DurationText,           // human copy, e.g. "About 30 seconds"
    string IntensityText,          // honest resource statement, e.g. "Uses part of one CPU core"
    string WorkloadKind,           // dispatched inside the child process (see LabWorker)
    IReadOnlyList<string> WorkloadArgs,
    TimeSpan QuietPhase,           // leading do-nothing span (observation splits on this)
    TimeSpan ExpectedDuration,     // quiet + work; the child self-terminates at this point
    IReadOnlyList<ExperimentPhaseCaption> PhaseCaptions);

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
    double? WorkerAvgCpuWhileWorking,  // % of one core, averaged after the quiet phase
    int? WorkerThreads,
    bool CpuWasPrecise,                // true = libproc per-second delta (own process)
    double? SystemCpuBefore,           // whole-system CPU just before the run
    double? SystemCpuPeak,             // whole-system CPU peak during the run
    int SamplesObserved,
    string? FailureReason);
