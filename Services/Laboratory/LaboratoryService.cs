using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Laboratory;

/// <summary>
/// The Laboratory's single service: holds the experiment catalog, runs at
/// most one experiment at a time as a real child process, and observes it
/// exclusively through the monitoring pipeline that already exists —
/// <see cref="ProcessMonitorService"/> for the worker's own numbers and
/// <see cref="LiveMetricsService"/> for the whole-system response. No new
/// timers, no new sampling; even the per-second UI tick piggybacks on the
/// process monitor's existing beat. Timeline, Insights, Metrics, Action
/// Flow and the dashboard all react to the worker naturally, because to
/// them it is just one more real process doing real work.
///
/// Safety: the child self-terminates (hard ceilings inside LabWorker) and
/// exits if this process dies; on top of that this service kills it on
/// Stop(), on Dispose(), and via a watchdog if it somehow overruns.
/// </summary>
public sealed class LaboratoryService : IDisposable
{
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(10);

    private readonly ProcessMonitorService _processes;
    private readonly ProcessSelection _selection;
    private readonly LiveMetricsService _metrics;
    private readonly SystemStoryService _story;
    private readonly object _lock = new();

    private Process? _child;
    private ExperimentDefinition? _running;
    private Stopwatch? _clock;
    private DateTime _startedAt;
    private bool _stopRequested;
    private bool _finished;

    // Observation accumulators (guarded by _lock).
    private bool _workerSelected;
    private double _peakCpu = -1;
    private double _workCpuSum;
    private int _workCpuCount;
    private int _samples;
    private int? _threads;
    private bool _precise;
    private double? _systemCpuBefore;
    private double _systemCpuPeak = -1;

    /// <summary>The experiment catalog. One entry today; future milestones append.</summary>
    public IReadOnlyList<ExperimentDefinition> Experiments { get; }

    /// <summary>The definition currently running, or null when idle.</summary>
    public ExperimentDefinition? RunningDefinition { get { lock (_lock) return _running; } }

    /// <summary>Elapsed time of the current run (zero when idle).</summary>
    public TimeSpan Elapsed { get { lock (_lock) return _clock?.Elapsed ?? TimeSpan.Zero; } }

    /// <summary>The worker's most recent CPU reading, if it has been observed yet.</summary>
    public double? LiveWorkerCpu { get; private set; }

    /// <summary>The worker's pid once spawned (−1 when idle).</summary>
    public int WorkerPid { get; private set; } = -1;

    /// <summary>Result of the most recent run, if any.</summary>
    public ExperimentResult? LastResult { get; private set; }

    /// <summary>State or observation changed. Raised on background threads — UI must marshal.</summary>
    public event Action? Changed;

    public LaboratoryService(
        ProcessMonitorService processes,
        ProcessSelection selection,
        LiveMetricsService metrics,
        SystemStoryService story)
    {
        _processes = processes;
        _selection = selection;
        _metrics = metrics;
        _story = story;
        Experiments = new[] { BuildCpuExperiment(), BuildLifecycleExperiment() };
    }

    // ---- catalog ----

    private static ExperimentDefinition BuildCpuExperiment() => new(
        Id: "cpu-activity",
        Category: "CPU",
        Title: "What happens when a program suddenly needs the processor?",
        AboutToObserve:
            "InsideOS will launch a small helper process that it fully owns. For the first few "
            + "seconds the helper does nothing at all — then it starts doing real arithmetic in a "
            + "controlled rhythm, using part of one CPU core, and after about half a minute it "
            + "exits on its own.",
        WhyInteresting:
            "You will watch a process's whole life: it appears, waits, works, and disappears — and "
            + "the operating system responds at every step. Because InsideOS owns the helper, macOS "
            + "reveals its exact numbers, so everything you see is measured, not estimated.",
        WhatToWatch: new[]
        {
            "Processes — a second process named “InsideOS” appears. The operating system tracks processes by ID, not by name.",
            "Its status — “Sleeping” while it waits, flipping to “Running” the moment real work begins.",
            "Action Flow — InsideOS follows the helper automatically; watch the CPU particles speed up.",
            "Timeline — the CPU rise should appear as a story within a few seconds of the work starting.",
        },
        WhatYouLearned: new[]
        {
            "It appeared in Processes as a second “InsideOS” — the operating system identifies "
            + "processes by ID, not by name.",
            "Its status read “Sleeping” while it waited, then “Running” once real work began — "
            + "that word describes the last second of behavior, not the process's purpose.",
            "The Timeline likely recorded its CPU rise as a story, and Action Flow's particles "
            + "sped up while it worked.",
            "When it exited, it vanished from the process list — the operating system reclaimed "
            + "its memory and CPU time immediately.",
        },
        DurationText: "About 30 seconds",
        IntensityText: "Uses part of one CPU core",
        WorkloadKind: "cpu",
        Phases: new[]
        {
            new ExperimentPhase("Waiting", TimeSpan.FromSeconds(8), 0),
            new ExperimentPhase("Working", TimeSpan.FromSeconds(20), 60),
        },
        PhaseCaptions: new[]
        {
            new ExperimentPhaseCaption(TimeSpan.Zero,
                "The helper process has been created — and it is doing nothing yet. Notice it reads "
                + "“Sleeping”: alive, loaded, waiting."),
            new ExperimentPhaseCaption(TimeSpan.FromSeconds(8),
                "Now the helper is doing real arithmetic. The scheduler is granting it processor "
                + "time — watch its CPU share and its status."),
            new ExperimentPhaseCaption(TimeSpan.FromSeconds(24),
                "Almost done — the helper will exit on its own, and the operating system will "
                + "reclaim everything it used."),
        });

    private static ExperimentDefinition BuildLifecycleExperiment() => new(
        Id: "process-lifecycle",
        Category: "PROCESS",
        Title: "What happens when an application starts, works, waits, and exits?",
        AboutToObserve:
            "InsideOS will launch a helper process that lives a complete, deliberately visible "
            + "life: it starts, initializes with light activity, works hard for a while, then goes "
            + "idle — still alive, just waiting — and finally exits on its own. One whole process "
            + "lifecycle in about half a minute.",
        WhyInteresting:
            "Every application on your Mac lives this same story — creation, initialization, work, "
            + "waiting, exit. Watching one complete lifecycle end-to-end shows what the words in "
            + "the process list actually mean: “Running” describes the last second, “Sleeping” "
            + "means ready-and-waiting, and exiting hands everything back to the operating system.",
        WhatToWatch: new[]
        {
            "Processes — watch the helper appear, and later vanish, from the list.",
            "Its status — light activity while initializing, “Running” during real work, then back "
            + "to “Sleeping” while it waits, still alive.",
            "Action Flow — InsideOS follows the helper automatically through every phase.",
            "Timeline — expect a story: process started, CPU rose, and finally the process ended.",
        },
        WhatYouLearned: new[]
        {
            "A process's life has distinct phases — created, initializing, working, waiting, "
            + "exited — and you watched the operating system track every one of them.",
            "Idle is not gone: during the waiting phase the helper used no measurable CPU, yet it "
            + "stayed fully alive in the process list, ready to resume instantly.",
            "“Running” and “Sleeping” describe the last second of behavior — the same process "
            + "showed both, without ever changing what it was.",
            "A graceful exit needs no cleanup from you: the moment the helper finished, the "
            + "operating system reclaimed its memory and CPU time and removed it from the list.",
        },
        DurationText: "About 30 seconds",
        IntensityText: "Uses part of one CPU core briefly",
        WorkloadKind: "lifecycle",
        Phases: new[]
        {
            new ExperimentPhase("Initializing", TimeSpan.FromSeconds(5), 12),
            new ExperimentPhase("Working", TimeSpan.FromSeconds(12), 55),
            new ExperimentPhase("Waiting", TimeSpan.FromSeconds(12), 0),
        },
        PhaseCaptions: new[]
        {
            new ExperimentPhaseCaption(TimeSpan.Zero,
                "Phase 1 — Initializing: the process was just created and is setting itself up "
                + "with light activity, like an app loading its pieces."),
            new ExperimentPhaseCaption(TimeSpan.FromSeconds(5),
                "Phase 2 — Working: now it is doing its real work. The scheduler grants it "
                + "processor time and its status reads “Running”."),
            new ExperimentPhaseCaption(TimeSpan.FromSeconds(17),
                "Phase 3 — Waiting: work finished, but the process is still alive. Its CPU falls "
                + "to zero and its status returns to “Sleeping” — idle is not the same as gone."),
            new ExperimentPhaseCaption(TimeSpan.FromSeconds(26),
                "Any moment now it will exit gracefully — watch it disappear from the process "
                + "list as the operating system reclaims everything."),
        });

    // ---- run control ----

    /// <summary>Starts an experiment. Returns false if one is already running.</summary>
    public bool Start(ExperimentDefinition definition)
    {
        lock (_lock)
        {
            if (_running is not null)
                return false;

            _running = definition;
            _stopRequested = false;
            _finished = false;
            _workerSelected = false;
            _peakCpu = -1;
            _workCpuSum = 0;
            _workCpuCount = 0;
            _samples = 0;
            _threads = null;
            _precise = false;
            _systemCpuBefore = null;
            _systemCpuPeak = -1;
            LiveWorkerCpu = null;
            WorkerPid = -1;
            _startedAt = DateTime.Now;
            _clock = Stopwatch.StartNew();
        }

        _processes.EnsureStarted();
        _processes.ProcessesUpdated += OnProcessesUpdated;
        _metrics.SnapshotUpdated += OnMetricsSnapshot;

        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("own executable path unavailable");

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--lab-worker");
            psi.ArgumentList.Add(definition.WorkloadKind);
            // Our pid, so the worker can verify its parent race-free and exit
            // the instant InsideOS is gone (see LabWorker for why an argument
            // beats a getppid() snapshot).
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (var arg in definition.WorkloadArgs)
                psi.ArgumentList.Add(arg);

            var child = Process.Start(psi)
                ?? throw new InvalidOperationException("the operating system did not return a process");
            child.EnableRaisingEvents = true;
            child.Exited += OnChildExited;

            lock (_lock)
            {
                _child = child;
                WorkerPid = child.Id;
            }
        }
        catch (Exception ex)
        {
            Finish(ExperimentOutcome.Failed,
                "The helper process could not be started, so nothing ran and nothing needs cleaning "
                + $"up. The operating system reported: {ex.Message}");
            return true; // the run happened; it just failed — LastResult explains it
        }

        _story.ReportLearningEvent("Experiment started",
            $"You started “{definition.Title}” in the Laboratory — watch the operating system respond.");
        Changed?.Invoke();
        return true;
    }

    /// <summary>User-initiated early stop. The child is terminated immediately.</summary>
    public void Stop()
    {
        Process? child;
        lock (_lock)
        {
            if (_running is null || _finished)
                return;
            _stopRequested = true;
            child = _child;
        }
        KillQuietly(child); // Exited fires next and finishes as Stopped
    }

    public void Dispose()
    {
        Process? child;
        lock (_lock)
        {
            _stopRequested = true;
            child = _child;
        }
        KillQuietly(child);
    }

    // ---- observation (piggybacks on the existing per-second pipelines) ----

    private void OnProcessesUpdated(IReadOnlyList<ProcessSample> samples)
    {
        ExperimentDefinition? def;
        int pid;
        TimeSpan elapsed;
        lock (_lock)
        {
            if (_running is null || _finished)
                return;
            def = _running;
            pid = WorkerPid;
            elapsed = _clock?.Elapsed ?? TimeSpan.Zero;
        }

        var worker = pid > 0 ? samples.FirstOrDefault(s => s.Pid == pid) : null;
        if (worker is not null)
        {
            bool select = false;
            lock (_lock)
            {
                _samples++;
                if (worker.CpuPercent is { } cpu)
                {
                    LiveWorkerCpu = cpu;
                    if (cpu > _peakCpu)
                        _peakCpu = cpu;
                    // "While working" = phases scheduled at real duty; light
                    // initialization and idle waiting are excluded.
                    if (def.DutyAt(elapsed) >= ExperimentDefinition.ActiveDutyFloor)
                    {
                        _workCpuSum += cpu;
                        _workCpuCount++;
                    }
                }
                if (worker.ThreadCount is { } threads)
                    _threads = threads;
                _precise |= worker.CpuIsPrecise;
                if (!_workerSelected)
                    _workerSelected = select = true;
            }
            // Auto-follow: the same shared selection Action Flow and the
            // Processes page already use. Done once, with a real pipeline sample.
            if (select)
                _selection.Select(worker);
        }

        // Watchdog: the worker self-terminates, but if it somehow overran,
        // end it here. Belt and suspenders.
        if (elapsed > def.ExpectedDuration + WatchdogGrace)
        {
            Process? child;
            lock (_lock)
                child = _child;
            KillQuietly(child);
        }

        Changed?.Invoke(); // doubles as the per-second UI tick — no new timers
    }

    private void OnMetricsSnapshot(MetricsSnapshot snapshot)
    {
        lock (_lock)
        {
            if (_running is null || _finished || snapshot.CpuUsagePercent is not { } cpu)
                return;
            _systemCpuBefore ??= cpu; // first snapshot lands in the quiet phase
            if (cpu > _systemCpuPeak)
                _systemCpuPeak = cpu;
        }
    }

    private void OnChildExited(object? sender, EventArgs e)
    {
        int exitCode = 0;
        try
        {
            exitCode = (sender as Process)?.ExitCode ?? 0;
        }
        catch
        {
            // Exit code unavailable — treat as clean; the outcome logic below still holds.
        }

        bool stopped;
        lock (_lock)
            stopped = _stopRequested;

        if (stopped)
            Finish(ExperimentOutcome.Stopped, null);
        else if (exitCode == 0)
            Finish(ExperimentOutcome.Completed, null);
        else
            Finish(ExperimentOutcome.Failed,
                $"The helper exited with code {exitCode} instead of finishing its workload. "
                + "Nothing is left running — the process is already gone.");
    }

    private void Finish(ExperimentOutcome outcome, string? failureReason)
    {
        ExperimentResult result;
        ExperimentDefinition? def;
        lock (_lock)
        {
            if (_finished || _running is null)
                return;
            _finished = true;
            def = _running;

            result = new ExperimentResult(
                def.Id,
                outcome,
                _startedAt,
                _clock?.Elapsed ?? TimeSpan.Zero,
                WorkerPid > 0 ? WorkerPid : null,
                _peakCpu >= 0 ? _peakCpu : null,
                _workCpuCount > 0 ? _workCpuSum / _workCpuCount : null,
                _threads,
                _precise,
                _systemCpuBefore,
                _systemCpuPeak >= 0 ? _systemCpuPeak : null,
                _samples,
                failureReason);

            LastResult = result;
            _running = null;
            _clock = null;
            _child = null;
            LiveWorkerCpu = null;
            WorkerPid = -1;
        }

        _processes.ProcessesUpdated -= OnProcessesUpdated;
        _metrics.SnapshotUpdated -= OnMetricsSnapshot;

        if (outcome == ExperimentOutcome.Completed)
            _story.ReportLearningEvent("Experiment completed",
                $"The “{def.Title}” experiment finished — the helper process exited on its own and "
                + "the operating system reclaimed its memory and CPU time.");

        Changed?.Invoke();
    }

    private static void KillQuietly(Process? child)
    {
        try
        {
            if (child is { HasExited: false })
                child.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or exiting — either way it is gone.
        }
    }
}
