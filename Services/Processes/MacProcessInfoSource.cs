using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.Processes;

/// <summary>
/// macOS process source. A single `ps` call per second provides the universal
/// list (unprivileged macOS lets ps report CPU, memory and state for every
/// process, while libproc is limited to the current user's processes). For
/// processes we own, the data is then enriched via libproc with precise
/// delta-based CPU usage, thread counts, exact start times and full names.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacProcessInfoSource : IProcessInfoSource
{
    private const int ProcPidTaskInfo = 4;  // PROC_PIDTASKINFO
    private const int ProcPidTBsdInfo = 3;  // PROC_PIDTBSDINFO
    private const int TaskInfoSize = 96;    // sizeof(proc_taskinfo)
    private const int BsdInfoSize = 136;    // sizeof(proc_bsdinfo)

    private readonly double _machTicksToNanos;
    private Dictionary<int, (double CpuNanos, long Timestamp)> _previousCpu = new();

    public MacProcessInfoSource()
    {
        mach_timebase_info(out var timebase);
        _machTicksToNanos = timebase.Denom == 0 ? 1 : (double)timebase.Numer / timebase.Denom;
    }

    public async Task<IReadOnlyList<ProcessSample>> SampleAsync(CancellationToken cancellationToken)
    {
        string output = await RunPsAsync(cancellationToken);

        var samples = new List<ProcessSample>(512);
        var nextCpu = new Dictionary<int, (double CpuNanos, long Timestamp)>(512);
        long now = Stopwatch.GetTimestamp();
        var wallClock = DateTime.Now;

        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6 || !int.TryParse(parts[0], out int pid))
                continue;

            double? cpu = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double psCpu)
                ? psCpu : null;
            ulong? memory = ulong.TryParse(parts[2], out ulong rssKb) ? rssKb * 1024 : null;
            var status = ParseStatus(parts[3]);
            DateTime? startTime = TryParseEtime(parts[4]) is { } elapsed ? wallClock - elapsed : null;
            string name = parts[5].Trim();
            int? threads = null;
            bool cpuIsPrecise = false;

            // Enrich processes we own via libproc.
            var task = new ProcTaskInfo();
            if (proc_pidinfo(pid, ProcPidTaskInfo, 0, ref task, TaskInfoSize) == TaskInfoSize)
            {
                threads = task.ThreadNum;
                memory = task.ResidentSize;

                double cpuNanos = (task.TotalUser + task.TotalSystem) * _machTicksToNanos;
                nextCpu[pid] = (cpuNanos, now);
                if (_previousCpu.TryGetValue(pid, out var prev))
                {
                    double wallNanos = (now - prev.Timestamp) * 1_000_000_000.0 / Stopwatch.Frequency;
                    if (wallNanos > 0)
                    {
                        cpu = Math.Max(0, (cpuNanos - prev.CpuNanos) / wallNanos * 100);
                        cpuIsPrecise = true;
                    }
                }

                var bsd = new ProcBsdInfo();
                if (proc_pidinfo(pid, ProcPidTBsdInfo, 0, ref bsd, BsdInfoSize) == BsdInfoSize)
                    startTime = DateTimeOffset.FromUnixTimeSeconds((long)bsd.StartTvSec).LocalDateTime;

                var buffer = new byte[64];
                int nameLength = proc_name(pid, buffer, 64);
                if (nameLength > 0)
                    name = Encoding.UTF8.GetString(buffer, 0, nameLength);
            }

            samples.Add(new ProcessSample(pid, name, cpu, memory, status, startTime, threads, cpuIsPrecise));
        }

        _previousCpu = nextCpu; // Old dictionary (with exited pids) is dropped wholesale.
        return samples;
    }

    private static async Task<string> RunPsAsync(CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/bin/ps", "-axo pid=,pcpu=,rss=,state=,etime=,ucomm=")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ps");
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }

    private static ProcessStatus ParseStatus(string state) => state.Length == 0 ? ProcessStatus.Unknown : state[0] switch
    {
        'R' => ProcessStatus.Running,
        'S' => ProcessStatus.Sleeping,
        'I' => ProcessStatus.Idle,
        'T' => ProcessStatus.Stopped,
        'U' => ProcessStatus.Waiting,
        'Z' => ProcessStatus.Zombie,
        _ => ProcessStatus.Unknown,
    };

    /// <summary>Parses ps etime: [[dd-]hh:]mm:ss.</summary>
    private static TimeSpan? TryParseEtime(string value)
    {
        int days = 0;
        int dash = value.IndexOf('-');
        if (dash > 0)
        {
            if (!int.TryParse(value[..dash], out days))
                return null;
            value = value[(dash + 1)..];
        }

        var parts = value.Split(':');
        return parts.Length switch
        {
            2 when int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s) =>
                new TimeSpan(days, 0, m, s),
            3 when int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s) =>
                new TimeSpan(days, h, m, s),
            _ => null,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcTaskInfo
    {
        public ulong VirtualSize, ResidentSize, TotalUser, TotalSystem, ThreadsUser, ThreadsSystem;
        public int Policy, Faults, Pageins, CowFaults, MessagesSent, MessagesReceived,
                   SyscallsMach, SyscallsUnix, Csw, ThreadNum, NumRunning, Priority;
    }

    /// <summary>Mirrors struct proc_bsdinfo from sys/proc_info.h (char arrays flattened to ulongs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcBsdInfo
    {
        public uint Flags, Status, XStatus, Pid, PPid, Uid, Gid, RUid, RGid, SvUid, SvGid, Rfu1;
        public ulong Comm0, Comm1;
        public ulong Name0, Name1, Name2, Name3;
        public uint NFiles, PGid, PJobC, ETDev, ETPGid;
        public int Nice;
        public ulong StartTvSec, StartTvUsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebaseInfo
    {
        public uint Numer, Denom;
    }

    [DllImport("libproc")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, ref ProcTaskInfo info, int size);

    [DllImport("libproc")]
    private static extern int proc_pidinfo(int pid, int flavor, ulong arg, ref ProcBsdInfo info, int size);

    [DllImport("libproc")]
    private static extern int proc_name(int pid, byte[] buffer, uint size);

    [DllImport("libSystem")]
    private static extern int mach_timebase_info(out MachTimebaseInfo info);
}
