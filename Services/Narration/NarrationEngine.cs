using System;
using System.Collections.Generic;
using System.Linq;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Laboratory;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Narration;

/// <summary>
/// The single place where InsideOS interprets operating-system activity.
/// Deterministic, rule-based, fully local — no AI, no network. Every page
/// (Timeline stories, Insights, Action Flow, the dashboard, Laboratory
/// summaries) derives its explanation from one of the four entry points
/// here, so the app can never tell two different stories about the same
/// evidence.
///
/// Vocabulary discipline: facts are carried as <see cref="Evidence"/>
/// (Measured or Observed); interpretations always hedge in the text itself
/// ("likely", "probably", "possibly") and carry a
/// <see cref="NarrationConfidence"/> that is High only when directly backed
/// by measured evidence. The engine never invents facts — if a value was
/// not observed, it is absent, not estimated.
///
/// The engine is stateless: callers that want trend detection own a
/// <see cref="MomentWindow"/>; callers that want calm switching apply their
/// own hysteresis over the stable rule ids returned here.
/// </summary>
public static class NarrationEngine
{
    // ---- moment thresholds (tuned for per-second samples) ----
    private const double CpuLow = 8;            // below this the CPU is "mostly idle"
    private const double CpuModerate = 15;      // low/active boundary for memory rules
    private const double CpuHigh = 30;          // clearly busy
    private const double CpuVeryHigh = 85;      // intensive work
    private const double CpuRiseDelta = 15;     // percent-points increase = "rising"
    private const double NetActive = 50 * 1024;         // 50 KB/s
    private const double NetBusy = 1024 * 1024;         // 1 MB/s
    private const double DiskActive = 200 * 1024;       // 200 KB/s
    private const double MemoryRise = 20 * 1048576;     // +20 MB across the window
    private const double MemoryFall = -30 * 1048576;    // -30 MB across the window

    // ---- system-level thresholds ----
    private const int MaxActivities = 8;
    private const double CpuIntenseLevel = 85;
    private const double HeavySustainedLevel = 60;
    private const double ActiveRecentLevel = 20;
    private const double IdlePriorLevel = 40;
    private const double QuietSystemLevel = 15;
    private const double BusySystemLevel = 60;
    private const ulong MemoryIntenseBytes = 3UL * 1024 * 1024 * 1024;
    private const double DownloadActiveBps = 2 * 1024 * 1024;
    private const double UploadActiveBps = 1 * 1024 * 1024;

    private static readonly string[] BrowserNames =
    {
        "safari", "chrome", "firefox", "arc", "edge", "brave", "opera", "vivaldi", "zen",
    };

    private static readonly IReadOnlyList<Evidence> NoEvidence = Array.Empty<Evidence>();

    // =====================================================================
    // 1) Stories — grouped timeline events for one process
    // =====================================================================

    /// <summary>
    /// Interprets a grouped story ("likely explanation"). Returns null when
    /// fewer than two events exist — a single event speaks for itself.
    /// </summary>
    public static Interpretation? NarrateStory(string name, IReadOnlyList<TimelineEvent> events)
    {
        if (events.Count < 2)
            return null;

        bool started = events.Any(e => e.Kind == TimelineEventKind.ProcessStarted);
        bool ended = events.Any(e => e.Kind == TimelineEventKind.ProcessEnded);
        bool cpu = events.Any(e => e.Category == TimelineCategory.Cpu);
        bool mem = events.Any(e => e.Category == TimelineCategory.Memory);
        bool disk = events.Any(e => e.Category == TimelineCategory.Disk);
        bool net = events.Any(e => e.Category == TimelineCategory.Network);

        var evidence = StoryEvidence(events);

        // Pure lifecycle (started + ended, nothing else) is directly measured.
        if (started && ended && !cpu && !mem && !disk && !net)
            return new Interpretation(
                $"{name} ran briefly and finished — probably a short background task the system needed.",
                NarrationConfidence.High, evidence);

        string? summary =
            started && (cpu || mem)
                ? $"{name} probably just launched and is loading its interface and data into memory."
            : cpu && mem && net
                ? $"{name} is probably loading new content — perhaps a page, file or media — and processing it."
            : cpu && net
                ? $"{name} is likely downloading data and processing it."
            : cpu && disk
                ? $"{name} is likely reading or writing files and processing their contents."
            : mem && net
                ? $"{name} is probably receiving data and keeping it in memory."
            : disk && net
                ? $"{name} is probably downloading or syncing files."
            : cpu && mem
                ? $"{name} is probably working on a new task and reserving memory for it."
            : cpu
                ? $"{name} is probably doing sustained computational work right now."
            : net
                ? $"{name} is likely communicating with external services in the background."
            : mem
                ? $"{name} is probably allocating additional memory for new content."
            : disk
                ? $"{name} is probably reading or writing files."
            : null;

        return summary is null ? null : new Interpretation(summary, NarrationConfidence.Medium, evidence);
    }

    private static IReadOnlyList<Evidence> StoryEvidence(IReadOnlyList<TimelineEvent> events)
    {
        var list = new List<Evidence>(events.Count);
        foreach (var e in events)
        {
            // Process lifecycle and disk/network deltas come from exact kernel
            // counters; CPU/memory events derive from sampled averages.
            var quality = e.Category is TimelineCategory.Cpu or TimelineCategory.Memory
                ? EvidenceQuality.Observed
                : EvidenceQuality.Measured;
            list.Add(new Evidence($"{e.Time:HH:mm:ss} — {e.Title}: {e.Detail}", quality));
        }
        return list;
    }

    // =====================================================================
    // 2) Moments — one process's current behavior (Action Flow, dashboard)
    // =====================================================================

    /// <summary>
    /// Interprets the current moment of one process from its snapshot and a
    /// short window of recent readings. Rule ids are stable so callers can
    /// smooth switches with hysteresis.
    /// </summary>
    public static MomentNarration NarrateMoment(MomentWindow window, ProcessFlowSnapshot snapshot)
    {
        if (snapshot.ProcessExited)
            return new MomentNarration("exited",
                "The selected process has terminated.",
                ActivityTone.Terminated, NarrationConfidence.High,
                new[] { new Evidence("The process is no longer present in the kernel's process table.", EvidenceQuality.Measured) });

        // Status-based rules come first — they describe the process itself.
        if (snapshot.Status == ProcessStatus.Zombie)
            return new MomentNarration("zombie",
                "This process has already finished its work and is waiting for the system to clean it up — a so-called 'zombie' process.",
                ActivityTone.Idle, NarrationConfidence.High,
                new[] { new Evidence("Kernel state: zombie.", EvidenceQuality.Measured) });
        if (snapshot.Status == ProcessStatus.Stopped)
            return new MomentNarration("stopped",
                "The process is currently paused by the operating system or a debugger. It is not doing any work right now.",
                ActivityTone.Idle, NarrationConfidence.High,
                new[] { new Evidence("Kernel state: stopped.", EvidenceQuality.Measured) });

        if (window.Count < 3)
            return new MomentNarration("warmup",
                $"Watching {snapshot.Name} — collecting a few seconds of activity before explaining what it is doing.",
                ActivityTone.Observing, NarrationConfidence.High, NoEvidence);

        double? cpu = window.RecentAverage(r => r.Cpu);
        double? cpuBefore = window.BaselineAverage(r => r.Cpu);
        bool cpuRising = cpu is { } c && cpuBefore is { } cb && c - cb >= CpuRiseDelta;

        double? netIn = window.RecentAverage(r => r.NetIn);
        double? netOut = window.RecentAverage(r => r.NetOut);
        bool netKnown = netIn.HasValue || netOut.HasValue;
        double net = (netIn ?? 0) + (netOut ?? 0);

        double? diskRead = window.RecentAverage(r => r.DiskRead);
        double? diskWrite = window.RecentAverage(r => r.DiskWrite);
        bool diskKnown = diskRead.HasValue || diskWrite.HasValue;

        double? memoryDelta = window.MemoryDelta();

        var evidence = MomentEvidence(snapshot, cpu, net, diskRead, diskWrite, memoryDelta);

        bool diskHidden = snapshot.Disk.Quality == MetricQuality.Unavailable;
        if (diskHidden && (cpu ?? 0) < CpuLow && net < NetActive)
            return new MomentNarration("limited-idle",
                "This is a system process, so macOS hides some of its activity. From what is visible, it appears mostly idle right now.",
                ActivityTone.Idle, NarrationConfidence.Medium, evidence);

        // Network-centred patterns.
        if (netKnown && net >= NetActive && (cpuRising || (cpu ?? 0) >= CpuHigh))
        {
            if ((netOut ?? 0) > 2 * (netIn ?? 0))
                return new MomentNarration("upload-process",
                    "Likely explanation: the application is preparing data and sending it out over the network.",
                    ActivityTone.Activity, NarrationConfidence.Medium, evidence);
            return new MomentNarration("download-process",
                "Likely explanation: the application is downloading data and processing it as it arrives.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        }
        if (netKnown && net >= NetActive && diskKnown && (diskWrite ?? 0) >= DiskActive)
            return new MomentNarration("download-save",
                "Possibly downloading data and saving it to your disk.",
                ActivityTone.Activity, NarrationConfidence.Low, evidence);
        if (netKnown && net >= NetActive && (cpu ?? 0) < CpuLow)
            return new MomentNarration("background-network",
                "Likely explanation: the application is communicating with external services while staying mostly idle itself.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        if (netKnown && net >= NetBusy)
            return new MomentNarration("network-heavy",
                "The application is probably transferring a large amount of data over the network right now.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);

        // Disk-centred patterns.
        if (diskKnown && (diskRead ?? 0) >= DiskActive && (cpu ?? 0) >= CpuHigh)
            return new MomentNarration("read-process",
                "Probably reading files from disk and processing their contents.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        if (diskKnown && (diskWrite ?? 0) >= DiskActive && (diskWrite ?? 0) >= (diskRead ?? 0))
            return new MomentNarration("writing-files",
                "Likely explanation: the application is writing files — probably saving data to your disk.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        if (diskKnown && (diskRead ?? 0) >= DiskActive)
            return new MomentNarration("reading-files",
                "Likely explanation: the application is reading files from your disk.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);

        // Memory-centred patterns.
        if (memoryDelta is { } growth && growth >= MemoryRise)
        {
            if ((cpu ?? 0) < CpuModerate)
                return new MomentNarration("allocating",
                    "Likely explanation: the application is allocating additional memory — possibly loading new content.",
                    ActivityTone.Activity, NarrationConfidence.Medium, evidence);
            return new MomentNarration("compute-memory",
                "Probably working on a task that needs more and more memory as it runs.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        }
        if (memoryDelta is { } drop && drop <= MemoryFall)
            return new MomentNarration("released-memory",
                "The application possibly just finished a task and released memory back to the system.",
                ActivityTone.Idle, NarrationConfidence.Low, evidence);

        // CPU-only patterns.
        if ((cpu ?? 0) >= CpuVeryHigh)
        {
            if (cpu > 105)
                return new MomentNarration("multicore",
                    "The application is working very hard — probably using several processor cores at once.",
                    ActivityTone.Activity, NarrationConfidence.Medium, evidence);
            return new MomentNarration("intense",
                "The application is probably running an intensive task on the processor.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        }
        if ((cpu ?? 0) >= CpuHigh)
            return new MomentNarration("computing",
                "Likely explanation: the application is busy computing — heavy processor work without much file or network activity.",
                ActivityTone.Activity, NarrationConfidence.Medium, evidence);
        if ((cpu ?? 0) >= CpuLow)
            return new MomentNarration("light-work",
                "The application is doing some light work — possibly handling small background tasks.",
                ActivityTone.Activity, NarrationConfidence.Low, evidence);

        // Idle: no measurable CPU. Keep everything on screen and reassure —
        // the process is still fully loaded and ready, not stuck or broken.
        return new MomentNarration("idle",
            "No measurable CPU activity right now — the application appears to be waiting for more work. "
            + "Its memory stays allocated so it can resume instantly, which is completely normal.",
            ActivityTone.Idle, NarrationConfidence.High, evidence);
    }

    private static IReadOnlyList<Evidence> MomentEvidence(
        ProcessFlowSnapshot snapshot, double? cpu, double net,
        double? diskRead, double? diskWrite, double? memoryDelta)
    {
        var list = new List<Evidence>(4);
        if (cpu is { } c)
            list.Add(new Evidence($"CPU: about {c:0.0}% of one core over the last seconds.",
                snapshot.Cpu.Quality == MetricQuality.Measured
                    ? EvidenceQuality.Measured : EvidenceQuality.Observed));
        if (snapshot.Memory.Value is { } m)
            list.Add(new Evidence($"Memory: {Format.Bytes((ulong)m)} currently held.", EvidenceQuality.Measured));
        if (memoryDelta is { } delta && Math.Abs(delta) >= MemoryRise)
            list.Add(new Evidence(
                delta > 0
                    ? $"Memory grew by about {Format.Bytes((ulong)delta)} across the window."
                    : $"Memory shrank by about {Format.Bytes((ulong)(-delta))} across the window.",
                EvidenceQuality.Observed));
        if ((diskRead ?? 0) + (diskWrite ?? 0) >= DiskActive)
            list.Add(new Evidence(
                $"Disk: reading {Format.Speed(diskRead ?? 0)}, writing {Format.Speed(diskWrite ?? 0)}.",
                EvidenceQuality.Measured));
        if (net >= NetActive)
            list.Add(new Evidence($"Network: about {Format.Speed(net)} moving right now.", EvidenceQuality.Measured));
        return list;
    }

    // =====================================================================
    // 3) System — the whole machine's current activity (Insights)
    // =====================================================================

    /// <summary>
    /// Interprets the system-wide picture into a small ranked set of narrated
    /// activities. Rules are evaluated in priority order and only state what
    /// the evidence supports.
    /// </summary>
    public static IReadOnlyList<NarratedActivity> NarrateSystem(NarrationContext context)
    {
        var activities = new List<NarratedActivity>();
        var now = context.Now;

        // Events from the last 3 minutes, grouped per process, newest group first.
        var recentGroups = context.RecentEvents
            .Where(e => (now - e.Time).TotalMinutes <= 3 && e.Pid >= 0)
            .GroupBy(e => e.Pid)
            .OrderByDescending(g => g.Max(e => e.Time))
            .ToList();

        AddApplicationPatterns(activities, recentGroups, context);
        AddNetworkPatterns(activities, context);
        AddProcessLoadPatterns(activities, context);
        AddSwitchingPattern(activities, recentGroups, now);
        AddBatteryPattern(activities, context);
        AddSystemLoadPatterns(activities, context);

        return activities
            .GroupBy(a => a.Id).Select(g => g.First()) // one activity per subject
            .Take(MaxActivities)
            .ToList();
    }

    private static void AddApplicationPatterns(
        List<NarratedActivity> activities,
        List<IGrouping<int, EvidenceEvent>> groups,
        NarrationContext context)
    {
        int added = 0;
        foreach (var group in groups)
        {
            if (added >= 3)
                break;
            string name = group.Last().Name;
            var time = group.Max(e => e.Time);
            var kinds = group.Select(e => e.Kind).ToHashSet();
            bool cpu = group.Any(e => e.Category == TimelineCategory.Cpu);
            bool mem = group.Any(e => e.Category == TimelineCategory.Memory);
            bool net = group.Any(e => e.Category == TimelineCategory.Network);
            bool read = kinds.Contains(TimelineEventKind.DiskRead);
            bool write = kinds.Contains(TimelineEventKind.DiskWrite);

            NarratedActivity? activity = null;
            if (kinds.Contains(TimelineEventKind.ProcessEnded))
            {
                activity = new NarratedActivity($"shutdown:{name}", ActivityCategory.Application, "IconAppWindow",
                    "Application shut down",
                    $"{name} exited and the operating system reclaimed its memory.",
                    NarrationConfidence.High, time); // directly measured
            }
            else if (kinds.Contains(TimelineEventKind.ProcessStarted) && (cpu || mem))
            {
                activity = new NarratedActivity($"warmup:{name}", ActivityCategory.Application, "IconAppWindow",
                    "Application warming up",
                    $"{name} probably just launched and is loading its interface into memory.",
                    NarrationConfidence.Medium, time);
            }
            else if (cpu && mem && net)
            {
                activity = IsBrowser(name)
                    ? new NarratedActivity($"webload:{name}", ActivityCategory.Network, "IconGlobe",
                        "Likely webpage loading",
                        $"{name} is probably rendering a webpage — it's using CPU, memory and network together.",
                        NarrationConfidence.High, time)
                    : new NarratedActivity($"content:{name}", ActivityCategory.Application, "IconAppWindow",
                        "Loading new content",
                        $"{name} appears to be loading new content — perhaps a file, page or media.",
                        NarrationConfidence.Medium, time);
            }
            else if (cpu && read)
            {
                activity = new NarratedActivity($"fileopen:{name}", ActivityCategory.Disk, "IconDisk",
                    "Likely opening a file",
                    $"{name} is likely opening or processing a file — it's reading from disk while using CPU.",
                    mem ? NarrationConfidence.High : NarrationConfidence.Medium, time);
            }
            else if (write)
            {
                activity = new NarratedActivity($"filesave:{name}", ActivityCategory.Disk, "IconDisk",
                    "Likely saving a file",
                    $"{name} appears to be saving data — it recently wrote a noticeable amount to disk.",
                    NarrationConfidence.Medium, time);
            }
            else if (net && !cpu && !mem
                     && context.RecentEvents.Count(e =>
                         e.Pid == group.Key && e.Category == TimelineCategory.Network) >= 3)
            {
                activity = new NarratedActivity($"sync:{name}", ActivityCategory.Network, "IconGlobe",
                    "Background synchronization",
                    $"{name} is possibly syncing in the background — small bursts of network traffic keep recurring.",
                    NarrationConfidence.Low, time);
            }

            if (activity is not null)
            {
                activities.Add(activity);
                added++;
            }
        }
    }

    private static void AddNetworkPatterns(List<NarratedActivity> activities, NarrationContext context)
    {
        if (context.NetworkInBps >= DownloadActiveBps)
        {
            var recentNet = context.RecentEvents.LastOrDefault(e =>
                e.Category == TimelineCategory.Network
                && (context.Now - e.Time).TotalMinutes <= 2 && e.Pid >= 0);
            string who = recentNet is null ? "" : $" — probably for {recentNet.Name}";
            activities.Add(new NarratedActivity("net:download", ActivityCategory.Network, "IconDownload",
                "Downloads are currently active",
                $"Data is arriving at about {Format.Speed(context.NetworkInBps)}{who}.",
                NarrationConfidence.High, context.Now));
        }
        else if (context.NetworkOutBps >= UploadActiveBps)
        {
            activities.Add(new NarratedActivity("net:upload", ActivityCategory.Network, "IconUpload",
                "Likely uploading",
                $"Data is leaving at about {Format.Speed(context.NetworkOutBps)} — something is probably uploading or backing up.",
                NarrationConfidence.Medium, context.Now));
        }
    }

    private static void AddProcessLoadPatterns(List<NarratedActivity> activities, NarrationContext context)
    {
        int cpuAdded = 0;
        foreach (var p in context.Processes.OrderByDescending(p => p.CpuRecent))
        {
            if (p.CpuRecent >= CpuIntenseLevel && cpuAdded < 2)
            {
                cpuAdded++;
                activities.Add(new NarratedActivity($"cpu:{p.Name}", ActivityCategory.Cpu, "IconGauge",
                    "CPU-intensive task",
                    $"{p.Name} appears to be running a CPU-intensive task — about {Math.Round(p.CpuRecent)}% of a core right now.",
                    NarrationConfidence.High, context.Now));
            }
            else if (p.CpuSustained >= HeavySustainedLevel && p.CpuRecent >= IdlePriorLevel)
            {
                activities.Add(new NarratedActivity($"heavy:{p.Name}", ActivityCategory.Cpu, "IconGauge",
                    "Heavy background work",
                    $"{p.Name} has been working hard for several minutes — probably something like indexing, compiling or media processing.",
                    NarrationConfidence.Medium, context.Now));
                break;
            }
        }

        var awakened = context.Processes.FirstOrDefault(p =>
            p.CpuPrior < 5 && p.CpuRecent >= ActiveRecentLevel);
        if (awakened is not null)
            activities.Add(new NarratedActivity($"active:{awakened.Name}", ActivityCategory.Application, "IconPulse",
                "Application became active",
                $"{awakened.Name} just woke up — it probably received something to do.",
                NarrationConfidence.Medium, context.Now));

        var settled = context.Processes.FirstOrDefault(p =>
            p.CpuPrior >= IdlePriorLevel && p.CpuRecent < 5);
        if (settled is not null)
            activities.Add(new NarratedActivity($"idle:{settled.Name}", ActivityCategory.Application, "IconTimer",
                "Application became idle",
                $"{settled.Name} appears to have finished its work and settled down.",
                NarrationConfidence.Medium, context.Now));

        var hungry = context.Processes
            .Where(p => p.MemoryBytes >= MemoryIntenseBytes)
            .OrderByDescending(p => p.MemoryBytes)
            .FirstOrDefault();
        if (hungry is not null)
            activities.Add(new NarratedActivity($"mem:{hungry.Name}", ActivityCategory.Memory, "IconMemChip",
                "Memory-intensive application",
                $"{hungry.Name} is currently holding about {Format.Bytes(hungry.MemoryBytes!.Value)} of memory.",
                NarrationConfidence.High, context.Now)); // directly measured
    }

    private static void AddSwitchingPattern(
        List<NarratedActivity> activities, List<IGrouping<int, EvidenceEvent>> groups, DateTime now)
    {
        int distinct = groups.Select(g => g.Last().Name).Distinct().Count();
        if (distinct >= 4)
            activities.Add(new NarratedActivity("switching", ActivityCategory.System, "IconAppWindow",
                "Frequent application switching",
                $"Activity from {distinct} different applications in the last few minutes — you're probably switching between tasks.",
                NarrationConfidence.Medium, now));
    }

    private static void AddBatteryPattern(List<NarratedActivity> activities, NarrationContext context)
    {
        if (context.OnBattery != true)
            return;
        var drain = context.Processes
            .Where(p => p.CpuSustained >= 50)
            .OrderByDescending(p => p.CpuSustained)
            .FirstOrDefault();
        if (drain is not null)
            activities.Add(new NarratedActivity("battery", ActivityCategory.Battery, "IconBattery",
                "Battery saving opportunity",
                $"{drain.Name} has been using a lot of CPU while on battery — pausing or closing it could possibly extend battery life.",
                NarrationConfidence.Medium, context.Now));
    }

    private static void AddSystemLoadPatterns(List<NarratedActivity> activities, NarrationContext context)
    {
        var now = context.Now;
        if (context.SystemCpuShort is double shortCpu && shortCpu >= BusySystemLevel)
        {
            activities.Add(new NarratedActivity("sys:busy", ActivityCategory.System, "IconPulse",
                "System under heavy activity",
                $"Overall CPU usage is around {Math.Round(shortCpu)}% — several tasks appear to be competing for the processor.",
                NarrationConfidence.High, now));
            return;
        }

        bool noRecentEvents = !context.RecentEvents.Any(e => (now - e.Time).TotalMinutes <= 2);
        bool longIdle = !context.RecentEvents.Any(e => (now - e.Time).TotalMinutes <= 5);

        if (longIdle && context.SystemCpuLong is < 10)
        {
            activities.Add(new NarratedActivity("sys:idle", ActivityCategory.System, "IconTimer",
                "System idle",
                "Very little has happened for a while — your computer seems to be idle.",
                NarrationConfidence.Medium, now));
        }
        else if (noRecentEvents
                 && context.SystemCpuShort is < QuietSystemLevel
                 && context.NetworkInBps < 200 * 1024)
        {
            activities.Add(new NarratedActivity("sys:quiet", ActivityCategory.System, "IconPulse",
                "System currently quiet",
                "CPU and network activity are low — your computer appears to be relaxing.",
                NarrationConfidence.High, now));
        }
    }

    private static bool IsBrowser(string name)
    {
        var lower = name.ToLowerInvariant();
        return BrowserNames.Any(lower.Contains);
    }

    // =====================================================================
    // 4) Experiments — what a completed Laboratory run showed
    // =====================================================================

    /// <summary>
    /// Interprets a completed experiment run: the measured facts become
    /// evidence, and the summary states what they demonstrated. Confidence is
    /// High — the worker is InsideOS's own process, so everything is measured.
    /// </summary>
    public static Interpretation NarrateExperiment(ExperimentResult result)
    {
        var evidence = new List<Evidence>(4);

        if (result.WorkerPid is { } pid)
            evidence.Add(new Evidence(
                $"The helper process (PID {pid}) lived for about {result.Elapsed.TotalSeconds:0} seconds, "
                + "then exited on its own — no cleanup was needed from you.",
                EvidenceQuality.Measured));
        if (result.WorkerAvgCpuWhileWorking is { } avg && result.WorkerPeakCpu is { } peak)
            evidence.Add(new Evidence(
                $"While working it averaged {avg:0}% of one CPU core and peaked at {peak:0}%"
                + (result.CpuWasPrecise ? " — measured exactly, because InsideOS owns the process." : "."),
                result.CpuWasPrecise ? EvidenceQuality.Measured : EvidenceQuality.Observed));
        if (result.WorkerThreads is { } threads)
            evidence.Add(new Evidence(
                $"It reported {threads} thread{(threads == 1 ? "" : "s")} — the arithmetic itself "
                + "ran on just one; the rest belong to the runtime that hosts the program "
                + "(housekeeping like the garbage collector). Even a simple program is a small team.",
                EvidenceQuality.Measured));
        if (result.SystemCpuBefore is { } before && result.SystemCpuPeak is { } sysPeak
            && sysPeak > before)
            evidence.Add(new Evidence(
                $"Whole-system CPU rose from about {before:0}% to a peak of {sysPeak:0}% during the "
                + "run — likely mostly the helper's doing, though other apps kept working too.",
                EvidenceQuality.Observed));

        string summary = result.Outcome switch
        {
            ExperimentOutcome.Completed =>
                "A process's whole life played out: created, waiting, working, gone — with the "
                + "operating system scheduling it, measuring it and reclaiming it at every step.",
            ExperimentOutcome.Stopped =>
                "You stopped the experiment early, so InsideOS terminated the helper immediately — "
                + "notice how quickly it disappeared from the process list. Ending a process is "
                + "itself an operating-system action worth watching: one request, and the process "
                + "and all its resources were gone.",
            _ => result.FailureReason
                 ?? "The experiment could not run. Nothing was started, so nothing is left behind.",
        };

        return new Interpretation(summary,
            result.Outcome == ExperimentOutcome.Completed && result.CpuWasPrecise
                ? NarrationConfidence.High
                : NarrationConfidence.Medium,
            evidence);
    }
}
