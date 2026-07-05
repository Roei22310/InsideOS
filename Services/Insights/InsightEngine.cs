using System;
using System.Collections.Generic;
using System.Linq;
using InsideOS.Services.SystemMetrics;
using InsideOS.Services.Timeline;

namespace InsideOS.Services.Insights;

/// <summary>
/// The rule-based System Intelligence engine (no AI, fully deterministic).
/// Rules are evaluated in priority order and only state what the evidence
/// supports; confidence drops and wording hedges ("probably", "appears to")
/// whenever multiple explanations fit. Adding a rule = adding one method
/// call in <see cref="Analyze"/>.
/// </summary>
public sealed class InsightEngine : IInsightEngine
{
    private const int MaxInsights = 8;

    // Thresholds (percent of one core for per-process values).
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

    public IReadOnlyList<Insight> Analyze(InsightEvidence evidence)
    {
        var insights = new List<Insight>();
        var now = evidence.Now;

        // Events from the last 3 minutes, grouped per process, newest group first.
        var recentGroups = evidence.RecentEvents
            .Where(e => (now - e.Time).TotalMinutes <= 3 && e.Pid >= 0)
            .GroupBy(e => e.Pid)
            .OrderByDescending(g => g.Max(e => e.Time))
            .ToList();

        AddApplicationPatternInsights(insights, recentGroups, evidence);
        AddNetworkInsights(insights, evidence);
        AddProcessLoadInsights(insights, evidence);
        AddSwitchingInsight(insights, recentGroups, now);
        AddBatteryInsight(insights, evidence);
        AddSystemLoadInsights(insights, evidence);

        return insights
            .GroupBy(i => i.Id).Select(g => g.First()) // one insight per subject
            .Take(MaxInsights)
            .ToList();
    }

    // ---- application patterns from recent timeline events ----

    private static void AddApplicationPatternInsights(
        List<Insight> insights,
        List<IGrouping<int, EvidenceEvent>> groups,
        InsightEvidence evidence)
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

            Insight? insight = null;
            if (kinds.Contains(TimelineEventKind.ProcessEnded))
            {
                insight = new Insight($"shutdown:{name}", InsightCategory.Application, "IconAppWindow",
                    "Application shut down",
                    $"{name} exited and the operating system reclaimed its memory.",
                    InsightConfidence.High, time); // directly measured
            }
            else if (kinds.Contains(TimelineEventKind.ProcessStarted) && (cpu || mem))
            {
                insight = new Insight($"warmup:{name}", InsightCategory.Application, "IconAppWindow",
                    "Application warming up",
                    $"{name} probably just launched and is loading its interface into memory.",
                    InsightConfidence.Medium, time);
            }
            else if (cpu && mem && net)
            {
                insight = IsBrowser(name)
                    ? new Insight($"webload:{name}", InsightCategory.Network, "IconGlobe",
                        "Likely webpage loading",
                        $"{name} is probably rendering a webpage — it's using CPU, memory and network together.",
                        InsightConfidence.High, time)
                    : new Insight($"content:{name}", InsightCategory.Application, "IconAppWindow",
                        "Loading new content",
                        $"{name} appears to be loading new content — perhaps a file, page or media.",
                        InsightConfidence.Medium, time);
            }
            else if (cpu && read)
            {
                insight = new Insight($"fileopen:{name}", InsightCategory.Disk, "IconDisk",
                    "Likely opening a file",
                    $"{name} is likely opening or processing a file — it's reading from disk while using CPU.",
                    mem ? InsightConfidence.High : InsightConfidence.Medium, time);
            }
            else if (write)
            {
                insight = new Insight($"filesave:{name}", InsightCategory.Disk, "IconDisk",
                    "Likely saving a file",
                    $"{name} appears to be saving data — it recently wrote a noticeable amount to disk.",
                    InsightConfidence.Medium, time);
            }
            else if (net && !cpu && !mem
                     && evidence.RecentEvents.Count(e =>
                         e.Pid == group.Key && e.Category == TimelineCategory.Network) >= 3)
            {
                insight = new Insight($"sync:{name}", InsightCategory.Network, "IconGlobe",
                    "Background synchronization",
                    $"{name} is possibly syncing in the background — small bursts of network traffic keep recurring.",
                    InsightConfidence.Low, time);
            }

            if (insight is not null)
            {
                insights.Add(insight);
                added++;
            }
        }
    }

    // ---- system-wide network ----

    private static void AddNetworkInsights(List<Insight> insights, InsightEvidence evidence)
    {
        if (evidence.NetworkInBps >= DownloadActiveBps)
        {
            var recentNet = evidence.RecentEvents.LastOrDefault(e =>
                e.Category == TimelineCategory.Network
                && (evidence.Now - e.Time).TotalMinutes <= 2 && e.Pid >= 0);
            string who = recentNet is null ? "" : $" — probably for {recentNet.Name}";
            insights.Add(new Insight("net:download", InsightCategory.Network, "IconDownload",
                "Downloads are currently active",
                $"Data is arriving at about {Format.Speed(evidence.NetworkInBps)}{who}.",
                InsightConfidence.High, evidence.Now));
        }
        else if (evidence.NetworkOutBps >= UploadActiveBps)
        {
            insights.Add(new Insight("net:upload", InsightCategory.Network, "IconUpload",
                "Likely uploading",
                $"Data is leaving at about {Format.Speed(evidence.NetworkOutBps)} — something is probably uploading or backing up.",
                InsightConfidence.Medium, evidence.Now));
        }
    }

    // ---- per-process load ----

    private static void AddProcessLoadInsights(List<Insight> insights, InsightEvidence evidence)
    {
        int cpuAdded = 0;
        foreach (var p in evidence.Processes.OrderByDescending(p => p.CpuRecent))
        {
            if (p.CpuRecent >= CpuIntenseLevel && cpuAdded < 2)
            {
                cpuAdded++;
                insights.Add(new Insight($"cpu:{p.Name}", InsightCategory.Cpu, "IconGauge",
                    "CPU-intensive task",
                    $"{p.Name} appears to be running a CPU-intensive task — about {Math.Round(p.CpuRecent)}% of a core right now.",
                    InsightConfidence.High, evidence.Now));
            }
            else if (p.CpuSustained >= HeavySustainedLevel && p.CpuRecent >= IdlePriorLevel)
            {
                insights.Add(new Insight($"heavy:{p.Name}", InsightCategory.Cpu, "IconGauge",
                    "Heavy background work",
                    $"{p.Name} has been working hard for several minutes — probably something like indexing, compiling or media processing.",
                    InsightConfidence.Medium, evidence.Now));
                break;
            }
        }

        var awakened = evidence.Processes.FirstOrDefault(p =>
            p.CpuPrior < 5 && p.CpuRecent >= ActiveRecentLevel);
        if (awakened is not null)
            insights.Add(new Insight($"active:{awakened.Name}", InsightCategory.Application, "IconPulse",
                "Application became active",
                $"{awakened.Name} just woke up — it probably received something to do.",
                InsightConfidence.Medium, evidence.Now));

        var settled = evidence.Processes.FirstOrDefault(p =>
            p.CpuPrior >= IdlePriorLevel && p.CpuRecent < 5);
        if (settled is not null)
            insights.Add(new Insight($"idle:{settled.Name}", InsightCategory.Application, "IconTimer",
                "Application became idle",
                $"{settled.Name} appears to have finished its work and settled down.",
                InsightConfidence.Medium, evidence.Now));

        var hungry = evidence.Processes
            .Where(p => p.MemoryBytes >= MemoryIntenseBytes)
            .OrderByDescending(p => p.MemoryBytes)
            .FirstOrDefault();
        if (hungry is not null)
            insights.Add(new Insight($"mem:{hungry.Name}", InsightCategory.Memory, "IconMemChip",
                "Memory-intensive application",
                $"{hungry.Name} is currently holding about {Format.Bytes(hungry.MemoryBytes!.Value)} of memory.",
                InsightConfidence.High, evidence.Now)); // directly measured
    }

    // ---- multi-app activity ----

    private static void AddSwitchingInsight(
        List<Insight> insights, List<IGrouping<int, EvidenceEvent>> groups, DateTime now)
    {
        int distinct = groups.Select(g => g.Last().Name).Distinct().Count();
        if (distinct >= 4)
            insights.Add(new Insight("switching", InsightCategory.System, "IconAppWindow",
                "Frequent application switching",
                $"Activity from {distinct} different applications in the last few minutes — you're probably switching between tasks.",
                InsightConfidence.Medium, now));
    }

    // ---- battery ----

    private static void AddBatteryInsight(List<Insight> insights, InsightEvidence evidence)
    {
        if (evidence.OnBattery != true)
            return;
        var drain = evidence.Processes
            .Where(p => p.CpuSustained >= 50)
            .OrderByDescending(p => p.CpuSustained)
            .FirstOrDefault();
        if (drain is not null)
            insights.Add(new Insight("battery", InsightCategory.Battery, "IconBattery",
                "Battery saving opportunity",
                $"{drain.Name} has been using a lot of CPU while on battery — pausing or closing it could possibly extend battery life.",
                InsightConfidence.Medium, evidence.Now));
    }

    // ---- overall system state ----

    private static void AddSystemLoadInsights(List<Insight> insights, InsightEvidence evidence)
    {
        var now = evidence.Now;
        if (evidence.SystemCpuShort is double shortCpu && shortCpu >= BusySystemLevel)
        {
            insights.Add(new Insight("sys:busy", InsightCategory.System, "IconPulse",
                "System under heavy activity",
                $"Overall CPU usage is around {Math.Round(shortCpu)}% — several tasks appear to be competing for the processor.",
                InsightConfidence.High, now));
            return;
        }

        bool noRecentEvents = !evidence.RecentEvents.Any(e => (now - e.Time).TotalMinutes <= 2);
        bool longIdle = !evidence.RecentEvents.Any(e => (now - e.Time).TotalMinutes <= 5);

        if (longIdle && evidence.SystemCpuLong is < 10)
        {
            insights.Add(new Insight("sys:idle", InsightCategory.System, "IconTimer",
                "System idle",
                "Very little has happened for a while — your computer seems to be idle.",
                InsightConfidence.Medium, now));
        }
        else if (noRecentEvents
                 && evidence.SystemCpuShort is < QuietSystemLevel
                 && evidence.NetworkInBps < 200 * 1024)
        {
            insights.Add(new Insight("sys:quiet", InsightCategory.System, "IconPulse",
                "System currently quiet",
                "CPU and network activity are low — your computer appears to be relaxing.",
                InsightConfidence.High, now));
        }
    }

    private static bool IsBrowser(string name)
    {
        var lower = name.ToLowerInvariant();
        return BrowserNames.Any(lower.Contains);
    }
}
