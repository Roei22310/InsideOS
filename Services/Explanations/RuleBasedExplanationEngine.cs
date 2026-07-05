using System;
using System.Collections.Generic;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Processes;

namespace InsideOS.Services.Explanations;

/// <summary>
/// Local, deterministic rule engine — no AI, no network. Keeps a short
/// history of readings for the selected process and matches trend patterns
/// (rising CPU, growing memory, network vs. disk activity…) against an
/// ordered rule list. A small hysteresis (a new rule must win twice in a row)
/// keeps the displayed explanation calm instead of flickering every second.
/// </summary>
public sealed class RuleBasedExplanationEngine : IExplanationEngine
{
    // Thresholds (tuned for per-second samples).
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

    private const int HistoryCapacity = 8;
    // Ticks a new rule must persist before switching. Must exceed the 2-tick
    // recent-average window, otherwise a single-second blip (which lingers in
    // the average for two evaluations) could flip the explanation.
    private const int SwitchStability = 3;

    private readonly record struct Reading(
        double? Cpu, double? Memory,
        double? DiskRead, double? DiskWrite,
        double? NetIn, double? NetOut);

    private readonly List<Reading> _history = new(HistoryCapacity + 1);
    private int _pid = -1;
    private string _currentId = "";
    private Explanation _current = new("", ExplanationKind.Observing);
    private string? _pendingId;
    private int _pendingTicks;

    public Explanation Explain(ProcessFlowSnapshot snapshot)
    {
        if (snapshot.Pid != _pid)
        {
            _pid = snapshot.Pid;
            _history.Clear();
            _currentId = "";
            _pendingId = null;
            _pendingTicks = 0;
        }

        if (snapshot.ProcessExited)
            return Commit("exited", new Explanation("The selected process has terminated.", ExplanationKind.Terminated));

        _history.Add(new Reading(
            snapshot.Cpu.Value, snapshot.Memory.Value,
            snapshot.DiskReadBps, snapshot.DiskWriteBps,
            snapshot.NetworkInBps, snapshot.NetworkOutBps));
        if (_history.Count > HistoryCapacity)
            _history.RemoveAt(0);

        var (id, explanation) = Evaluate(snapshot);

        // Hysteresis: keep the current explanation until a different rule has
        // matched for a couple of consecutive ticks.
        if (id == _currentId)
        {
            _pendingId = null;
            _pendingTicks = 0;
            return _current;
        }
        if (_currentId.Length == 0)
            return Commit(id, explanation);
        if (_pendingId == id)
        {
            if (++_pendingTicks >= SwitchStability)
                return Commit(id, explanation);
        }
        else
        {
            _pendingId = id;
            _pendingTicks = 1;
        }
        return _current;
    }

    private Explanation Commit(string id, Explanation explanation)
    {
        _currentId = id;
        _pendingId = null;
        _pendingTicks = 0;
        _current = explanation;
        return explanation;
    }

    private (string Id, Explanation Explanation) Evaluate(ProcessFlowSnapshot snapshot)
    {
        // Status-based rules come first — they describe the process itself.
        if (snapshot.Status == ProcessStatus.Zombie)
            return ("zombie", new Explanation(
                "This process has already finished its work and is waiting for the system to clean it up — a so-called 'zombie' process.",
                ExplanationKind.Idle));
        if (snapshot.Status == ProcessStatus.Stopped)
            return ("stopped", new Explanation(
                "The process is currently paused by the operating system or a debugger. It is not doing any work right now.",
                ExplanationKind.Idle));

        if (_history.Count < 3)
            return ("warmup", new Explanation(
                $"Watching {snapshot.Name} — collecting a few seconds of activity before explaining what it is doing.",
                ExplanationKind.Observing));

        double? cpu = RecentAverage(r => r.Cpu);
        double? cpuBefore = BaselineAverage(r => r.Cpu);
        bool cpuRising = cpu is { } c && cpuBefore is { } cb && c - cb >= CpuRiseDelta;

        double? netIn = RecentAverage(r => r.NetIn);
        double? netOut = RecentAverage(r => r.NetOut);
        bool netKnown = netIn.HasValue || netOut.HasValue;
        double net = (netIn ?? 0) + (netOut ?? 0);

        double? diskRead = RecentAverage(r => r.DiskRead);
        double? diskWrite = RecentAverage(r => r.DiskWrite);
        bool diskKnown = diskRead.HasValue || diskWrite.HasValue;

        double? memoryDelta = MemoryDelta();

        bool diskHidden = snapshot.Disk.Quality == MetricQuality.Unavailable;
        if (diskHidden && (cpu ?? 0) < CpuLow && net < NetActive)
            return ("limited-idle", new Explanation(
                "This is a system process, so macOS hides some of its activity. From what is visible, it appears mostly idle right now.",
                ExplanationKind.Idle));

        // Network-centred patterns.
        if (netKnown && net >= NetActive && (cpuRising || (cpu ?? 0) >= CpuHigh))
        {
            if ((netOut ?? 0) > 2 * (netIn ?? 0))
                return ("upload-process", new Explanation(
                    "Likely explanation: the application is preparing data and sending it out over the network.",
                    ExplanationKind.Activity));
            return ("download-process", new Explanation(
                "Likely explanation: the application is downloading data and processing it as it arrives.",
                ExplanationKind.Activity));
        }
        if (netKnown && net >= NetActive && diskKnown && (diskWrite ?? 0) >= DiskActive)
            return ("download-save", new Explanation(
                "Possibly downloading data and saving it to your disk.",
                ExplanationKind.Activity));
        if (netKnown && net >= NetActive && (cpu ?? 0) < CpuLow)
            return ("background-network", new Explanation(
                "Likely explanation: the application is communicating with external services while staying mostly idle itself.",
                ExplanationKind.Activity));
        if (netKnown && net >= NetBusy)
            return ("network-heavy", new Explanation(
                "The application is probably transferring a large amount of data over the network right now.",
                ExplanationKind.Activity));

        // Disk-centred patterns.
        if (diskKnown && (diskRead ?? 0) >= DiskActive && (cpu ?? 0) >= CpuHigh)
            return ("read-process", new Explanation(
                "Probably reading files from disk and processing their contents.",
                ExplanationKind.Activity));
        if (diskKnown && (diskWrite ?? 0) >= DiskActive && (diskWrite ?? 0) >= (diskRead ?? 0))
            return ("writing-files", new Explanation(
                "Likely explanation: the application is writing files — probably saving data to your disk.",
                ExplanationKind.Activity));
        if (diskKnown && (diskRead ?? 0) >= DiskActive)
            return ("reading-files", new Explanation(
                "Likely explanation: the application is reading files from your disk.",
                ExplanationKind.Activity));

        // Memory-centred patterns.
        if (memoryDelta is { } growth && growth >= MemoryRise)
        {
            if ((cpu ?? 0) < CpuModerate)
                return ("allocating", new Explanation(
                    "Likely explanation: the application is allocating additional memory — possibly loading new content.",
                    ExplanationKind.Activity));
            return ("compute-memory", new Explanation(
                "Probably working on a task that needs more and more memory as it runs.",
                ExplanationKind.Activity));
        }
        if (memoryDelta is { } drop && drop <= MemoryFall)
            return ("released-memory", new Explanation(
                "The application possibly just finished a task and released memory back to the system.",
                ExplanationKind.Idle));

        // CPU-only patterns.
        if ((cpu ?? 0) >= CpuVeryHigh)
        {
            if (cpu > 105)
                return ("multicore", new Explanation(
                    "The application is working very hard — probably using several processor cores at once.",
                    ExplanationKind.Activity));
            return ("intense", new Explanation(
                "The application is probably running an intensive task on the processor.",
                ExplanationKind.Activity));
        }
        if ((cpu ?? 0) >= CpuHigh)
            return ("computing", new Explanation(
                "Likely explanation: the application is busy computing — heavy processor work without much file or network activity.",
                ExplanationKind.Activity));
        if ((cpu ?? 0) >= CpuLow)
            return ("light-work", new Explanation(
                "The application is doing some light work — possibly handling small background tasks.",
                ExplanationKind.Activity));

        return ("idle", new Explanation(
            "The application appears to be idle — probably waiting for something to do. This is normal for most apps most of the time.",
            ExplanationKind.Idle));
    }

    /// <summary>Average of the last two readings (ignoring unknowns).</summary>
    private double? RecentAverage(Func<Reading, double?> selector)
    {
        int take = Math.Min(2, _history.Count);
        double sum = 0;
        int count = 0;
        for (int i = _history.Count - take; i < _history.Count; i++)
        {
            if (selector(_history[i]) is { } value)
            {
                sum += value;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>Average of everything before the last two readings.</summary>
    private double? BaselineAverage(Func<Reading, double?> selector)
    {
        if (_history.Count < 4)
            return null;
        double sum = 0;
        int count = 0;
        for (int i = 0; i < _history.Count - 2; i++)
        {
            if (selector(_history[i]) is { } value)
            {
                sum += value;
                count++;
            }
        }
        return count > 0 ? sum / count : null;
    }

    /// <summary>Memory change across the whole history window (~8 s).</summary>
    private double? MemoryDelta()
    {
        if (_history.Count < 4)
            return null;
        double? first = null, last = null;
        foreach (var reading in _history)
        {
            if (reading.Memory is { } memory)
            {
                first ??= memory;
                last = memory;
            }
        }
        return first.HasValue && last.HasValue ? last - first : null;
    }
}
