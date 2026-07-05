using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.ActionFlow;

/// <summary>
/// macOS per-process I/O. Disk counters come from proc_pid_rusage (only
/// permitted for processes the current user owns — root-owned processes
/// return EPERM and are reported as unavailable). Network counters come from
/// one cheap `nettop -n` snapshot per call (~10 ms), which unprivileged macOS
/// allows for every process.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacProcessIoSource : IProcessIoSource
{
    private const int RusageInfoV2 = 2;

    private volatile bool _nettopBroken;

    public DiskIoCounters? ReadDiskIo(int pid)
    {
        var info = new RUsageInfoV2();
        return proc_pid_rusage(pid, RusageInfoV2, ref info) == 0
            ? new DiskIoCounters(info.DiskIoBytesRead, info.DiskIoBytesWritten)
            : null;
    }

    public async Task<IReadOnlyDictionary<int, NetworkCounters>?> ReadNetworkCountersAsync(CancellationToken cancellationToken)
    {
        if (_nettopBroken)
            return null;

        try
        {
            var psi = new ProcessStartInfo("/usr/bin/nettop", "-P -x -n -L 1")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                _nettopBroken = true;
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var map = new Dictionary<int, NetworkCounters>(128);
            foreach (var line in output.Split('\n'))
            {
                // Row format: time,name.pid,interface,state,bytes_in,bytes_out,...
                var parts = line.Split(',');
                if (parts.Length < 6 || parts[1].Length == 0)
                    continue; // header or blank
                int dot = parts[1].LastIndexOf('.');
                if (dot < 0
                    || !int.TryParse(parts[1][(dot + 1)..], out int pid)
                    || !ulong.TryParse(parts[4], out ulong bytesIn)
                    || !ulong.TryParse(parts[5], out ulong bytesOut))
                {
                    continue;
                }
                map[pid] = new NetworkCounters(bytesIn, bytesOut);
            }

            if (map.Count == 0)
            {
                _nettopBroken = true; // unexpected output — stop retrying every second
                return null;
            }
            return map;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            _nettopBroken = true;
            return null;
        }
    }

    /// <summary>Mirrors struct rusage_info_v2 from sys/resource.h (160 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RUsageInfoV2
    {
        public ulong Uuid0, Uuid1;
        public ulong UserTime, SystemTime, PkgIdleWkups, InterruptWkups, Pageins, WiredSize,
                     ResidentSize, PhysFootprint, ProcStartAbstime, ProcExitAbstime,
                     ChildUserTime, ChildSystemTime, ChildPkgIdleWkups, ChildInterruptWkups,
                     ChildPageins, ChildElapsedAbstime, DiskIoBytesRead, DiskIoBytesWritten;
    }

    [DllImport("libproc")]
    private static extern int proc_pid_rusage(int pid, int flavor, ref RUsageInfoV2 info);
}
