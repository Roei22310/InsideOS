using System;
using System.Collections.Generic;
using InsideOS.Services.Processes;

namespace InsideOS.Services.Timeline;

public enum TimelineCategory
{
    Cpu,
    Memory,
    Disk,
    Network,
    Process,
    Learning,
}

public enum TimelineSeverity
{
    Info,
    Notice,
    High,
}

public enum TimelineEventKind
{
    ProcessStarted,
    ProcessEnded,
    CpuRise,
    MemoryRise,
    DiskRead,
    DiskWrite,
    NetworkActivity,
    Learning,
}

/// <summary>One observed system event. Always derived from real measurements.</summary>
public sealed record TimelineEvent(
    DateTime Time,
    TimelineEventKind Kind,
    TimelineCategory Category,
    string Title,
    string Detail,
    TimelineSeverity Severity);

/// <summary>
/// Immutable snapshot of a story (a group of related events for one process,
/// close together in time). Snapshots are safe to consume from the UI thread
/// while detection continues in the background; Replay/Analytics can reuse
/// the same shape later.
/// </summary>
public sealed record TimelineStorySnapshot(
    int StoryId,
    int Pid, // -1 => learning story, not tied to a process
    string ProcessName,
    DateTime StartTime,
    DateTime LastTime,
    IReadOnlyList<TimelineEvent> Events,
    string? Explanation,
    TimelineSeverity Severity,
    IReadOnlyList<TimelineCategory> Categories,
    ProcessSample? LastSample);
