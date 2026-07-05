using InsideOS.Services.Processes;

namespace InsideOS.Services.History;

/// <summary>Metrics the history engine records once per second.</summary>
public enum HistoryMetric
{
    CpuUsage,           // % of all cores busy
    MemoryUsed,         // bytes
    DiskUsed,           // bytes used on the system volume
    NetworkDown,        // bytes/sec
    NetworkUp,          // bytes/sec
    Battery,            // percent (absent on machines without a battery)
    ProcessCount,       // total processes
    ActiveProcessCount, // processes using ≥ 5% of a core
}

/// <summary>
/// Aggregates for one process over a time window, for the "most active
/// applications" table. TrendDelta is second-half average minus first-half
/// average CPU — positive means rising.
/// </summary>
public sealed record ProcessHistoryStat(
    int Pid,
    string Name,
    double AvgCpu,
    double PeakCpu,
    double? AvgMemoryBytes,
    double TrendDelta,
    ProcessSample LastSample);
