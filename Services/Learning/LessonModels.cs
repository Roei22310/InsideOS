using System.Collections.Generic;

namespace InsideOS.Services.Learning;

public enum LessonDifficulty
{
    Beginner,
    Intermediate,
    Advanced,
}

/// <summary>
/// Immutable description of one lesson in the Learning Journey. Lessons are
/// pure data — the UI renders whatever the provider supplies, so adding a
/// future lesson never requires modifying existing lessons or pages.
/// </summary>
public sealed record Lesson(
    int Number,
    string Id,
    string Title,
    string Description,
    string Duration,
    LessonDifficulty Difficulty,
    IReadOnlyList<string> Objectives);

/// <summary>Runtime progress state for one lesson (persisted via settings).</summary>
public sealed record LessonProgress(string LessonId, bool IsCompleted, double Progress);
