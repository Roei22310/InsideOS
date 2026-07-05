using System;

namespace InsideOS.Services.SystemMetrics;

/// <summary>Cumulative CPU tick counters since boot (Mach scheduler ticks on macOS).</summary>
public readonly record struct CpuTicks(uint User, uint System, uint Idle, uint Nice);

public readonly record struct MemoryUsage(ulong UsedBytes, ulong TotalBytes);

public readonly record struct DiskUsage(ulong UsedBytes, ulong TotalBytes);

public sealed record BatteryStatus(int Percent, string StateDescription);

/// <summary>Hardware/OS facts that do not change while the app is running.</summary>
public sealed record SystemStaticInfo(
    string OsVersion,
    string CpuModel,
    ulong TotalMemoryBytes,
    int LogicalCores);

/// <summary>
/// One per-second reading of all live metrics. Nullable members mean the value
/// is not available yet (first sample) or not supported on this platform.
/// </summary>
public sealed record MetricsSnapshot(
    double? CpuUsagePercent,
    MemoryUsage? Memory,
    DiskUsage? Disk,
    double? DownloadBytesPerSecond,
    double? UploadBytesPerSecond,
    BatteryStatus? Battery,
    TimeSpan Uptime);
