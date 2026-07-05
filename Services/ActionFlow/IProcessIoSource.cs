using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.ActionFlow;

public readonly record struct DiskIoCounters(ulong BytesRead, ulong BytesWritten);

public readonly record struct NetworkCounters(ulong BytesIn, ulong BytesOut);

/// <summary>
/// Platform-specific per-process I/O counters (cumulative since process
/// start). Implement per OS; rates are derived from consecutive readings by
/// <see cref="ProcessFlowMonitor"/>.
/// </summary>
public interface IProcessIoSource
{
    /// <summary>Cumulative disk bytes for one pid, or null when the OS denies access.</summary>
    DiskIoCounters? ReadDiskIo(int pid);

    /// <summary>
    /// Cumulative network bytes for all processes with sockets, or null when
    /// per-process network accounting is unavailable on this system.
    /// A pid absent from the map has no network activity on record.
    /// </summary>
    Task<IReadOnlyDictionary<int, NetworkCounters>?> ReadNetworkCountersAsync(CancellationToken cancellationToken);
}
