using System.Collections.Generic;

namespace InsideOS.Services.Learning;

/// <summary>
/// Supplies the lesson catalog. Today lessons ship built in; a future provider
/// could load them from disk or download new ones without touching the UI.
/// </summary>
public interface ILessonProvider
{
    /// <summary>The lessons that exist today, ordered by number.</summary>
    IReadOnlyList<Lesson> GetLessons();

    /// <summary>
    /// Total planned length of the journey. Slots without an authored lesson
    /// render as locked ("available in a future update").
    /// </summary>
    int PlannedLessonCount { get; }
}
