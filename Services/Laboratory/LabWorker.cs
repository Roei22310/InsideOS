using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace InsideOS.Services.Laboratory;

/// <summary>
/// The code that runs *inside* a Laboratory child process. Program.Main
/// dispatches here (before Avalonia ever starts) when launched as
/// <c>InsideOS --lab-worker &lt;kind&gt; …</c>, so the workload is a real,
/// separate OS process that the app owns — visible in every existing view
/// with fully measured (libproc-precise) numbers.
///
/// The workload is a generic *phase schedule*: a sequence of
/// "dutyPercent:seconds" segments (0 = truly idle). CPU bursts, quiet
/// waiting, initialization ramps and whole lifecycles are all just
/// different schedules — the worker has no experiment-specific code.
///
/// Safety is enforced *in the worker itself*, not just by the parent:
/// every parameter is clamped to a hard ceiling, the total lifetime can
/// never exceed <see cref="MaxTotalSeconds"/>, and the worker exits the
/// moment it notices its parent is gone (re-parented by the kernel), so it
/// can never keep running in the background. It touches no files, opens no
/// sockets, allocates nothing beyond its own stack, and needs no privileges.
/// </summary>
public static class LabWorker
{
    private const int MaxPhases = 8;
    private const int MaxPhaseSeconds = 45;
    private const int MaxTotalSeconds = 60;
    private const int MaxDutyPercent = 80; // never a full core — this is a classroom, not a stress test
    private const int SliceMs = 100;       // duty cycle resolution

    [DllImport("libc")]
    private static extern int getppid();

    /// <summary>Entry point for the child. Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        // args: --lab-worker <kind> <parentPid> <duty:seconds> [<duty:seconds> …]
        // The parent's pid is passed explicitly rather than snapshotted with
        // getppid() at startup: if the parent dies during our ~200 ms launch,
        // we are already re-parented by then and a snapshot would happily
        // record the *new* parent — the argument makes the check race-free.
        if (args.Length < 4 || args[1].Length == 0
            || !int.TryParse(args[2], out int parentPid) || parentPid <= 1)
        {
            return 64; // EX_USAGE — refuse rather than guess
        }

        var phases = new List<(int DutyPercent, int Seconds)>();
        double total = 0;
        for (int i = 3; i < args.Length && phases.Count < MaxPhases; i++)
        {
            var parts = args[i].Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], out int duty)
                || !int.TryParse(parts[1], out int seconds))
            {
                return 64; // malformed schedule
            }
            duty = Math.Clamp(duty, 0, MaxDutyPercent);
            seconds = Math.Clamp(seconds, 1, MaxPhaseSeconds);
            if (total + seconds > MaxTotalSeconds)
                seconds = (int)Math.Max(0, MaxTotalSeconds - total);
            if (seconds == 0)
                break;
            total += seconds;
            phases.Add((duty, seconds));
        }
        if (phases.Count == 0)
            return 64;

        var clock = Stopwatch.StartNew();
        var slice = new Stopwatch();
        double sink = 1.000001; // real arithmetic the optimizer cannot remove
        double phaseStart = 0;

        foreach (var (duty, seconds) in phases)
        {
            double phaseEnd = phaseStart + seconds;
            int busyMs = SliceMs * duty / 100;

            while (clock.Elapsed.TotalSeconds < phaseEnd)
            {
                // Orphan check: if InsideOS died, the kernel re-parents us and
                // getppid() changes — stop immediately, never linger.
                if (getppid() != parentPid)
                    return 0;

                slice.Restart();
                while (slice.ElapsedMilliseconds < busyMs)
                    sink = sink * 1.0000001 + 0.0000001;

                int restMs = SliceMs - (int)slice.ElapsedMilliseconds;
                if (restMs > 0)
                    Thread.Sleep(restMs);
            }
            phaseStart = phaseEnd;
        }

        return sink > 0 ? 0 : 1; // consume the sink; always 0 in practice
    }
}
