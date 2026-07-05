using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.SystemMetrics;

/// <summary>
/// Used on platforms without a dedicated implementation yet. Returns null for
/// everything platform-specific so the UI clearly shows "not available"
/// instead of fake data.
/// TODO(Windows): implement a WindowsSystemMetricsSource (e.g. GetSystemTimes,
/// GlobalMemoryStatusEx, GetSystemPowerStatus) and select it in MainWindow.
/// </summary>
public sealed class FallbackSystemMetricsSource : ISystemMetricsSource
{
    public SystemStaticInfo GetStaticInfo() => new(
        OsVersion: RuntimeInformation.OSDescription,
        CpuModel: "Unknown processor",
        TotalMemoryBytes: (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        LogicalCores: Environment.ProcessorCount);

    public CpuTicks? ReadCpuTicks() => null;

    public MemoryUsage? ReadMemoryUsage() => null;

    public Task<BatteryStatus?> ReadBatteryAsync(CancellationToken cancellationToken) =>
        Task.FromResult<BatteryStatus?>(null);
}
