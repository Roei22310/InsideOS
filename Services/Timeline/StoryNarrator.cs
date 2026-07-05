using System.Collections.Generic;
using System.Linq;

namespace InsideOS.Services.Timeline;

/// <summary>
/// Rule-based narration for grouped stories (no AI, fully offline). Combines
/// the categories seen in a story into one beginner-friendly "likely
/// explanation", always with uncertainty words — assumptions are never
/// presented as facts.
/// </summary>
internal static class StoryNarrator
{
    public static string? Narrate(string name, IReadOnlyList<TimelineEvent> events)
    {
        if (events.Count < 2)
            return null;

        bool started = events.Any(e => e.Kind == TimelineEventKind.ProcessStarted);
        bool ended = events.Any(e => e.Kind == TimelineEventKind.ProcessEnded);
        bool cpu = events.Any(e => e.Category == TimelineCategory.Cpu);
        bool mem = events.Any(e => e.Category == TimelineCategory.Memory);
        bool disk = events.Any(e => e.Category == TimelineCategory.Disk);
        bool net = events.Any(e => e.Category == TimelineCategory.Network);

        if (started && ended && !cpu && !mem && !disk && !net)
            return $"{name} ran briefly and finished — probably a short background task the system needed.";
        if (started && (cpu || mem))
            return $"{name} probably just launched and is loading its interface and data into memory.";
        if (cpu && mem && net)
            return $"{name} is probably loading new content — perhaps a page, file or media — and processing it.";
        if (cpu && net)
            return $"{name} is likely downloading data and processing it.";
        if (cpu && disk)
            return $"{name} is likely reading or writing files and processing their contents.";
        if (mem && net)
            return $"{name} is probably receiving data and keeping it in memory.";
        if (disk && net)
            return $"{name} is probably downloading or syncing files.";
        if (cpu && mem)
            return $"{name} is probably working on a new task and reserving memory for it.";
        if (cpu)
            return $"{name} is probably doing sustained computational work right now.";
        if (net)
            return $"{name} is likely communicating with external services in the background.";
        if (mem)
            return $"{name} is probably allocating additional memory for new content.";
        if (disk)
            return $"{name} is probably reading or writing files.";
        return null;
    }
}
