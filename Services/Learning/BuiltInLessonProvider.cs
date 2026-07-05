using System.Collections.Generic;

namespace InsideOS.Services.Learning;

/// <summary>
/// The built-in lesson catalog. Adding a future lesson = adding one entry
/// here (and bumping <see cref="PlannedLessonCount"/> if the journey grows).
/// </summary>
public sealed class BuiltInLessonProvider : ILessonProvider
{
    public int PlannedLessonCount => 8;

    public IReadOnlyList<Lesson> GetLessons() =>
    [
        new Lesson(
            Number: 1,
            Id: "first-process", // keeps completion persisted before this rename
            Title: "Understanding Processes",
            Description:
                "Every application on your Mac runs as a process — a live program the " +
                "operating system supervises. In this lesson you select a real application " +
                "and follow it through the operating system, watching the processor time, " +
                "memory and network resources it receives in real time.",
            Duration: "2–3 minutes",
            Difficulty: LessonDifficulty.Beginner,
            Objectives:
            [
                "Understand what a process is.",
                "Observe a real process.",
                "Understand why the operating system gives it CPU, memory and network resources.",
            ]),
        // Future lessons register here — no UI changes required.
    ];
}
