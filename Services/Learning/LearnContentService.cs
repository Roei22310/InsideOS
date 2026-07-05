using System.Collections.Generic;
using InsideOS.Services.ActionFlow;
using InsideOS.Services.Processes;
using InsideOS.Services.SystemMetrics;

namespace InsideOS.Services.Learning;

/// <summary>
/// Built-in, fully offline educational content (English, beginner level).
/// The catalog is keyed by (language, level, topic) so additional languages
/// and levels can be registered without changing callers, and entries are
/// plain data a future AI enhancer could rewrite or extend.
/// </summary>
public sealed class LearnContentService : ILearnContentService
{
    private const double CpuLight = 8, CpuBusy = 30, CpuHot = 60, CpuMulti = 110;
    private const double DiskNoticeable = 200 * 1024, DiskHeavy = 5 * 1048576;
    private const double NetQuiet = 1024, NetHeavy = 2 * 1048576;

    private readonly ulong _totalMemoryBytes;
    private readonly Dictionary<(string Language, KnowledgeLevel Level, LearnTopicId Topic), LearnContent> _catalog;

    public LearnContentService(ulong totalMemoryBytes)
    {
        _totalMemoryBytes = totalMemoryBytes;
        _catalog = BuildCatalog();
    }

    public LearnContent GetContent(LearnTopicId topic, KnowledgeLevel level = KnowledgeLevel.Beginner, string language = "en")
    {
        if (_catalog.TryGetValue((language, level, topic), out var content))
            return content;
        return _catalog[("en", KnowledgeLevel.Beginner, topic)]; // fallback
    }

    public string DescribeWhy(LearnTopicId topic, ProcessFlowSnapshot s) => topic switch
    {
        LearnTopicId.Process => s.Status switch
        {
            ProcessStatus.Running =>
                $"{s.Name} is active right now — the operating system is giving it turns on the processor so it can do its work.",
            ProcessStatus.Sleeping or ProcessStatus.Idle or ProcessStatus.Waiting =>
                $"{s.Name} is sleeping — loaded and ready, but waiting for work such as your next click or a message from "
                + "another program. It stays fully in memory so it can wake and respond instantly; that memory is retained "
                + "on purpose, not wasted. This is how most programs spend most of their time, and it is completely normal.",
            ProcessStatus.Stopped =>
                $"{s.Name} has been paused — the system or a debugger told it to stop and wait.",
            ProcessStatus.Zombie =>
                $"{s.Name} has already finished; only a small note about it remains until the system tidies it away.",
            _ => $"{s.Name} is being managed by the operating system like every other program.",
        },

        LearnTopicId.Cpu => s.Cpu.Value switch
        {
            null => "CPU details are not visible for this process, so there is nothing to interpret right now.",
            >= CpuBusy =>
                $"{s.Name} is probably using this much CPU because it is actively working — for example rendering content, processing data, or responding to your input.",
            >= CpuLight =>
                $"{s.Name} is doing light work — small background tasks that need just a little processor time.",
            _ =>
                $"{s.Name} is barely using the processor right now. That means it is mostly waiting, not working — which is how most programs spend most of their time.",
        },

        LearnTopicId.Memory when s.Memory.Value is { } memory =>
            $"{s.Name} is holding {Format.Bytes(memory)} of memory{MemoryShareText(memory)}. " +
            "That's where it keeps everything it needs quick access to — the more tabs, documents or data an app has open, the more memory it holds.",
        LearnTopicId.Memory =>
            "Memory details are not visible for this process right now.",

        LearnTopicId.Disk => s.Disk.Quality == MetricQuality.Unavailable
            ? "macOS only shows disk activity for applications you own, so this system process keeps its file activity private."
            : (s.Disk.Value ?? 0) >= DiskNoticeable
                ? $"{s.Name} is moving data to or from your disk right now — probably opening, saving or updating files{DiskDirectionText(s)}."
                : $"{s.Name} is barely touching the disk right now. Most applications only read or write files occasionally, in short bursts.",

        LearnTopicId.Network => (s.Network.Value ?? 0) >= NetQuiet && s.NetworkInBps is { } inBps && s.NetworkOutBps is { } outBps
            ? $"{s.Name} is exchanging data with other computers right now — receiving {Format.Speed(inBps)} and sending {Format.Speed(outBps)}."
            : $"{s.Name} is not sending or receiving much right now. Many apps only use the network in short bursts — like checking for updates or syncing.",

        _ => "",
    };

    public string DescribeWorry(LearnTopicId topic, ProcessFlowSnapshot s) => topic switch
    {
        LearnTopicId.Process => s.Status switch
        {
            ProcessStatus.Zombie =>
                "No — zombie processes sound scary but are harmless leftovers. The system will clean it up shortly.",
            ProcessStatus.Stopped =>
                "Usually not — something intentionally paused it. If you didn't expect that, it's worth a closer look.",
            _ =>
                "No — this is completely normal. Your system runs hundreds of processes like this all the time.",
        },

        LearnTopicId.Cpu => s.Cpu.Value switch
        {
            null => "There is nothing here suggesting a problem.",
            < CpuHot => "No. This level of CPU activity is completely normal.",
            <= CpuMulti =>
                "Usually not — short bursts of heavy CPU use are normal. It only deserves attention if it stays this high for many minutes and your computer feels slow.",
            _ =>
                "It's using several processor cores at once. That's expected for heavy tasks like exporting video or compiling — but if you didn't ask it to do anything demanding, it's worth checking.",
        },

        LearnTopicId.Memory when s.Memory.Value is { } memory && _totalMemoryBytes > 0 =>
            (100.0 * memory / _totalMemoryBytes) switch
            {
                < 15 => "No. This amount of memory usage is expected.",
                < 40 => "Not really — this is a lot, but normal for large applications like browsers. macOS manages memory well and will balance things automatically.",
                _ => "It is using a large share of your memory. That is not dangerous, but if your Mac feels slow, closing unused windows or tabs in this app may help.",
            },
        LearnTopicId.Memory => "There is nothing here suggesting a problem.",

        LearnTopicId.Disk => s.Disk.Quality == MetricQuality.Unavailable
            ? "Nothing suggests a problem — macOS simply keeps these details private for system processes."
            : (s.Disk.Value ?? 0) < DiskHeavy
                ? "No — occasional disk activity is completely normal. Apps constantly read settings and save small bits of data."
                : "Sustained heavy disk activity is normal during saves, downloads, updates or backups. If it never stops, it's worth checking what the app is doing.",

        LearnTopicId.Network => (s.Network.Value ?? 0) switch
        {
            < NetQuiet => "No — it's quiet right now.",
            < NetHeavy => "No — this is ordinary network usage, like syncing, loading content or streaming.",
            _ => "That's a lot of traffic — perfectly fine for downloads or video calls, but if you're not expecting it, it's worth a look.",
        },

        _ => "",
    };

    private string MemoryShareText(double memory) =>
        _totalMemoryBytes > 0
            ? $" — about {100.0 * memory / _totalMemoryBytes:0.#}% of your RAM"
            : "";

    private static string DiskDirectionText(ProcessFlowSnapshot s)
    {
        if (s.DiskReadBps is not { } read || s.DiskWriteBps is not { } write)
            return "";
        if (read > 2 * write)
            return " (mostly reading)";
        if (write > 2 * read)
            return " (mostly writing)";
        return "";
    }

    private static Dictionary<(string, KnowledgeLevel, LearnTopicId), LearnContent> BuildCatalog() => new()
    {
        [("en", KnowledgeLevel.Beginner, LearnTopicId.Process)] = new LearnContent(
            LearnTopicId.Process,
            "The Process",
            "A process is a running program. The operating system gives each process its own protected space in memory and decides when it gets time on the CPU, so programs can run side by side without interfering with each other.",
            ["Threads", "Scheduling", "Process Isolation", "Parent & Child Processes"]),

        [("en", KnowledgeLevel.Beginner, LearnTopicId.Cpu)] = new LearnContent(
            LearnTopicId.Cpu,
            "CPU",
            "The CPU (processor) performs the calculations required by running programs. It executes billions of tiny instructions per second, switching rapidly between all the programs that need attention — so fast that everything appears to run at once.",
            ["Threads", "Scheduling", "CPU Cores", "Context Switching"]),

        [("en", KnowledgeLevel.Beginner, LearnTopicId.Memory)] = new LearnContent(
            LearnTopicId.Memory,
            "Memory",
            "Memory (RAM) stores information while applications are running — their code, data and everything they are working on. It is very fast but temporary: it empties completely when the power goes off.",
            ["Virtual Memory", "Heap", "Stack", "Paging"]),

        [("en", KnowledgeLevel.Beginner, LearnTopicId.Disk)] = new LearnContent(
            LearnTopicId.Disk,
            "Disk",
            "The disk stores files permanently — your documents, apps and the operating system itself. Unlike memory, it keeps everything even when your computer is off, but reading and writing to it is much slower than using RAM.",
            ["File System", "SSD", "Caching", "Read & Write Speed"]),

        [("en", KnowledgeLevel.Beginner, LearnTopicId.Network)] = new LearnContent(
            LearnTopicId.Network,
            "Network",
            "The network component sends and receives data over the internet or your local network. Applications use it to load webpages, sync files, stream media and talk to servers around the world.",
            ["TCP", "HTTP", "Packets", "DNS"]),
    };
}
