using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.Processes;

/// <summary>
/// Platform-specific process enumeration. Implement per OS (macOS today,
/// Windows later); <see cref="ProcessMonitorService"/> drives the sampling
/// loop and is platform-agnostic.
/// </summary>
public interface IProcessInfoSource
{
    /// <summary>Snapshot of all running processes. Called once per second on a background thread.</summary>
    Task<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken);
}
