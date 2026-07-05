using System;

namespace InsideOS.Services.Processes;

public enum ProcessStatus
{
    Unknown,
    Running,
    Sleeping,
    Idle,
    Stopped,
    Waiting,
    Zombie,
}

/// <summary>
/// One per-second reading of a single process. Nullable members mean the value
/// could not be retrieved (e.g. macOS restricts thread counts of other users'
/// processes to root) — the UI shows them as unavailable instead of faking data.
/// </summary>
public sealed record ProcessSample(
    int Pid,
    string Name,
    double? CpuPercent,
    ulong? MemoryBytes,
    ProcessStatus Status,
    DateTime? StartTime,
    int? ThreadCount,
    bool CpuIsPrecise = false) // true = exact per-second delta; false = kernel's smoothed average (ps)
{
    /// <summary>CPU share (percent of one core) at or above which a process
    /// counts as actively running across the sample window.</summary>
    public const double ActiveCpuFloor = 1;

    /// <summary>
    /// The state of the process over this one-second sample — the single
    /// source of truth for everything the UI displays or filters on.
    ///
    /// <see cref="Status"/> is the kernel's *instantaneous* scheduler state
    /// at the moment ps sampled it. On macOS nearly every process — including
    /// very busy ones — reads "sleeping" at any given instant, because even a
    /// process using 20% of a core is off-CPU for most of each second.
    /// <see cref="CpuPercent"/>, by contrast, measures the whole last second.
    /// Rendering the instantaneous letter next to the window measurement made
    /// busy apps show "Sleeping" at 20% CPU. This property reconciles the two
    /// time bases: measurable work over the window means Running; dormant
    /// states pass through; Zombie/Stopped are always preserved. The raw
    /// instantaneous state remains available in <see cref="Status"/>.
    /// </summary>
    public ProcessStatus EffectiveStatus =>
        Status is ProcessStatus.Zombie or ProcessStatus.Stopped
            ? Status
            // Compare at the same one-decimal precision the UI displays, so a
            // process shown as "1.0%" can never be classified as sleeping.
            : Status == ProcessStatus.Running || Math.Round(CpuPercent ?? 0, 1) >= ActiveCpuFloor
                ? ProcessStatus.Running
                : Status;
}
