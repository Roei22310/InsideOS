using InsideOS.Services.Processes;

namespace InsideOS.Services.ActionFlow;

/// <summary>How trustworthy a displayed value is. Every value shown in the
/// Action Flow carries one of these — nothing is ever invented.</summary>
public enum MetricQuality
{
    Measured,     // read directly from kernel accounting for this exact interval
    Calculated,   // derived/smoothed by the kernel or by us (e.g. decaying average)
    Estimated,    // an approximation (currently unused — reserved for future milestones)
    Unavailable,  // macOS does not expose this to unprivileged apps
}

/// <summary>A single metric plus its honesty label. Value is null while a
/// delta-based reading is still collecting its first interval.</summary>
public readonly record struct FlowMetric(double? Value, MetricQuality Quality);

/// <summary>
/// One per-second reading of everything the Action Flow visualizes for the
/// selected process. Future milestones (Timeline, Replay, Event Recording)
/// can persist or replay these snapshots as-is.
/// </summary>
public sealed record ProcessFlowSnapshot(
    int Pid,
    string Name,
    ProcessStatus Status,
    FlowMetric Cpu,        // percent (100 = one full core)
    FlowMetric Memory,     // resident bytes
    FlowMetric Disk,       // bytes/sec, read + write combined
    FlowMetric Network,    // bytes/sec, in + out combined
    double? DiskReadBps,
    double? DiskWriteBps,
    double? NetworkInBps,
    double? NetworkOutBps,
    bool ProcessExited);
