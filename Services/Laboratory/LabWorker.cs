using System;
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
/// Safety is enforced *in the worker itself*, not just by the parent:
/// every parameter is clamped to a hard ceiling, the total lifetime can
/// never exceed <see cref="MaxTotalSeconds"/>, and the worker exits the
/// moment it notices its parent is gone (re-parented by the kernel), so it
/// can never keep running in the background. It touches no files, opens no
/// sockets, allocates nothing beyond its own stack, and needs no privileges.
/// </summary>
public static class LabWorker
{
    private const int MaxQuietSeconds = 15;
    private const int MaxWorkSeconds = 45;
    private const int MaxTotalSeconds = 60;
    private const int MaxDutyPercent = 80; // never a full core — this is a classroom, not a stress test
    private const int SliceMs = 100;       // duty cycle resolution

    [DllImport("libc")]
    private static extern int getppid();

    /// <summary>Entry point for the child. Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        // args: --lab-worker <kind> <parentPid> <kind-specific args…>
        // The parent's pid is passed explicitly rather than snapshotted with
        // getppid() at startup: if the parent dies during our ~200 ms launch,
        // we are already re-parented by then and a snapshot would happily
        // record the *new* parent — the argument makes the check race-free.
        if (args.Length < 3 || !int.TryParse(args[2], out int parentPid) || parentPid <= 1)
            return 64; // EX_USAGE — refuse rather than guess

        if (args[1] != "cpu" || args.Length < 6
            || !int.TryParse(args[3], out int quietSeconds)
            || !int.TryParse(args[4], out int workSeconds)
            || !int.TryParse(args[5], out int dutyPercent))
        {
            return 64; // unknown workload kind or malformed recipe
        }

        quietSeconds = Math.Clamp(quietSeconds, 0, MaxQuietSeconds);
        workSeconds = Math.Clamp(workSeconds, 1, MaxWorkSeconds);
        dutyPercent = Math.Clamp(dutyPercent, 5, MaxDutyPercent);
        double totalSeconds = Math.Min(quietSeconds + workSeconds, MaxTotalSeconds);

        var clock = Stopwatch.StartNew();
        var slice = new Stopwatch();
        double sink = 1.000001; // real arithmetic the optimizer cannot remove

        while (clock.Elapsed.TotalSeconds < totalSeconds)
        {
            // Orphan check: if InsideOS died, the kernel re-parents us and
            // getppid() changes — stop immediately, never linger.
            if (getppid() != parentPid)
                return 0;

            bool working = clock.Elapsed.TotalSeconds >= quietSeconds;
            int busyMs = working ? SliceMs * dutyPercent / 100 : 0;

            slice.Restart();
            while (slice.ElapsedMilliseconds < busyMs)
                sink = sink * 1.0000001 + 0.0000001;

            int restMs = SliceMs - (int)slice.ElapsedMilliseconds;
            if (restMs > 0)
                Thread.Sleep(restMs);
        }

        return sink > 0 ? 0 : 1; // consume the sink; always 0 in practice
    }
}
