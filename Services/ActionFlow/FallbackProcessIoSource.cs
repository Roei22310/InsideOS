using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.ActionFlow;

/// <summary>
/// Used on platforms without a dedicated implementation yet — reports both
/// disk and network as unavailable so the UI stays honest.
/// TODO(Windows): implement a WindowsProcessIoSource using GetProcessIoCounters
/// for disk and ETW / GetPerTcpConnectionEStats for per-process network.
/// </summary>
public sealed class FallbackProcessIoSource : IProcessIoSource
{
    public DiskIoCounters? ReadDiskIo(int pid) => null;

    public Task<IReadOnlyDictionary<int, NetworkCounters>?> ReadNetworkCountersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, NetworkCounters>?>(null);
}
