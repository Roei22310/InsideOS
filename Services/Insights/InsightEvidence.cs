using System;
using System.Collections.Generic;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Insights;

/// <summary>A recent timeline event, reduced to what insight rules need.</summary>
public sealed record EvidenceEvent(
    DateTime Time,
    int Pid,
    string Name,
    TimelineEventKind Kind,
    TimelineCategory Category);

/// <summary>Rolling CPU/memory picture of one currently-running process.</summary>
public sealed record ProcessEvidence(
    int Pid,
    string Name,
    double CpuRecent,    // avg over ~last 10 s
    double CpuPrior,     // avg over the ~2 min before that
    double CpuSustained, // avg over the whole ~2 min window
    ulong? MemoryBytes);

/// <summary>
/// Immutable evidence bundle handed to the engine. Everything in here comes
/// from real measurements collected by the existing services — the engine
/// itself performs no monitoring, which keeps analysis deterministic and
/// trivially testable.
/// </summary>
public sealed record InsightEvidence(
    DateTime Now,
    double? SystemCpuShort,  // avg system CPU %, ~30 s
    double? SystemCpuLong,   // avg system CPU %, ~3 min
    double NetworkInBps,     // ~15 s average
    double NetworkOutBps,
    bool? OnBattery,
    IReadOnlyList<EvidenceEvent> RecentEvents,  // last ~10 min, oldest → newest
    IReadOnlyList<ProcessEvidence> Processes);  // active processes only
