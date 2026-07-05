using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.Processes;

/// <summary>
/// Cross-platform fallback using System.Diagnostics.Process. Provides names,
/// PIDs and working sets; CPU usage and status are left null/unknown.
/// TODO(Windows): implement a WindowsProcessInfoSource with per-process CPU
/// deltas from Process.TotalProcessorTime (or PDH counters) and real statuses.
/// </summary>
public sealed class FallbackProcessInfoSource : IProcessInfoSource
{
    public Task<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken)
    {
        var samples = new List<ProcessSample>(256);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                samples.Add(new ProcessSample(
                    process.Id,
                    process.ProcessName,
                    CpuPercent: null,
                    MemoryBytes: (ulong)process.WorkingSet64,
                    ProcessStatus.Unknown,
                    StartTime: TryGetStartTime(process),
                    ThreadCount: TryGetThreadCount(process)));
            }
            catch
            {
                // Process exited or access denied — skip it.
            }
            finally
            {
                process.Dispose();
            }
        }
        return Task.FromResult<IReadOnlyList<ProcessSample>>(samples);
    }

    private static DateTime? TryGetStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return null; }
    }

    private static int? TryGetThreadCount(Process process)
    {
        try { return process.Threads.Count; }
        catch { return null; }
    }
}
