using System;
using System.Collections.Generic;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Narration;

/// <summary>A recent timeline event, reduced to what narration rules need.</summary>
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
/// Immutable evidence bundle handed to the narration engine for system-level
/// interpretation. Everything in here comes from real measurements collected
/// by the existing services — the engine itself performs no monitoring,
/// which keeps interpretation deterministic and trivially testable.
/// </summary>
public sealed record NarrationContext(
    DateTime Now,
    double? SystemCpuShort,  // avg system CPU %, ~30 s
    double? SystemCpuLong,   // avg system CPU %, ~3 min
    double NetworkInBps,     // ~15 s average
    double NetworkOutBps,
    bool? OnBattery,
    IReadOnlyList<EvidenceEvent> RecentEvents,  // last ~10 min, oldest → newest
    IReadOnlyList<ProcessEvidence> Processes);  // active processes only

/// <summary>
/// A short rolling window of one process's readings, used by
/// <see cref="NarrationEngine.NarrateMoment"/> to detect trends (rising CPU,
/// growing memory…). The caller owns the instance and its lifetime — the
/// engine itself stays stateless.
/// </summary>
public sealed class MomentWindow
{
    public const int Capacity = 8;

    internal readonly record struct Reading(
        double? Cpu, double? Memory,
        double? DiskRead, double? DiskWrite,
        double? NetIn, double? NetOut);

    internal readonly List<Reading> History = new(Capacity + 1);

    public int Count => History.Count;

    public void Clear() => History.Clear();

    public void Push(ActionFlow.ProcessFlowSnapshot snapshot)
    {
        History.Add(new Reading(
            snapshot.Cpu.Value, snapshot.Memory.Value,
            snapshot.DiskReadBps, snapshot.DiskWriteBps,
            snapshot.NetworkInBps, snapshot.NetworkOutBps));
        if (History.Count > Capacity)
            History.RemoveAt(0);
    }

    /// <summary>Average of the last two readings (ignoring unknowns).</summary>
    internal double? RecentAverage(Func<Reading, double?> selector)
    {
        int take = Math.Min(2, History.Count);
        double sum = 0;
        int count = 0;
        for (int i = History.Count - take; i < History.Count; i++)
        {
            if (selector(History[i]) is { } value)
            {
                sum += value;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>Average of everything before the last two readings.</summary>
    internal double? BaselineAverage(Func<Reading, double?> selector)
    {
        if (History.Count < 4)
            return null;
        double sum = 0;
        int count = 0;
        for (int i = 0; i < History.Count - 2; i++)
        {
            if (selector(History[i]) is { } value)
            {
                sum += value;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>Memory change across the whole history window (~8 s).</summary>
    internal double? MemoryDelta()
    {
        if (History.Count < 4)
            return null;
        double? first = null, last = null;
        foreach (var reading in History)
        {
            if (reading.Memory is { } memory)
            {
                first ??= memory;
                last = memory;
            }
        }
        return first.HasValue && last.HasValue ? last - first : null;
    }
}
