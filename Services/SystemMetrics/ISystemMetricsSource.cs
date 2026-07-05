using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.SystemMetrics;

/// <summary>
/// Platform-specific readings that have no cross-platform .NET API.
/// Implement this per OS (macOS today, Windows later); everything that .NET
/// covers cross-platform (disk, network, uptime) lives in <see cref="LiveMetricsService"/>.
/// </summary>
public interface ISystemMetricsSource
{
    SystemStaticInfo GetStaticInfo();

    /// <summary>Cumulative CPU ticks, or null if unavailable. Usage % is derived from two samples.</summary>
    CpuTicks? ReadCpuTicks();

    MemoryUsage? ReadMemoryUsage();

    Task<BatteryStatus?> ReadBatteryAsync(CancellationToken cancellationToken);
}
