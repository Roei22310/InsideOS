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
    bool CpuIsPrecise = false); // true = exact per-second delta; false = kernel's smoothed average (ps)
