using System;
using System.Collections.Generic;
using System.Linq;
using InsideOS.Services.Settings;

namespace InsideOS.Services.Learning;

/// <summary>
/// Central authority for the Learning Journey: exposes the lesson catalog
/// from an <see cref="ILessonProvider"/>, tracks completion (persisted via
/// <see cref="AppSettingsService"/>) and computes overall progress.
/// </summary>
public sealed class LessonManager
{
    private readonly ILessonProvider _provider;
    private readonly AppSettingsService _settings;

    public event Action? Changed;

    public LessonManager(ILessonProvider provider, AppSettingsService settings)
    {
        _provider = provider;
        _settings = settings;
        Lessons = provider.GetLessons().OrderBy(l => l.Number).ToArray();
    }

    /// <summary>Lessons that exist today, ordered by number.</summary>
    public IReadOnlyList<Lesson> Lessons { get; }

    public int PlannedLessonCount => _provider.PlannedLessonCount;

    public Lesson? FirstLesson => Lessons.Count > 0 ? Lessons[0] : null;

    public Lesson? FindByNumber(int number) => Lessons.FirstOrDefault(l => l.Number == number);

    /// <summary>A journey slot is unlocked once its lesson has been authored.</summary>
    public bool IsUnlocked(int number) => FindByNumber(number) is not null;

    /// <summary>
    /// Lesson 1 *is* the first-run introduction, so its completion
    /// automatically synchronizes with the onboarding completion state
    /// (covering installs that finished onboarding before lessons existed).
    /// </summary>
    public bool IsCompleted(Lesson lesson) =>
        _settings.CompletedLessons.Contains(lesson.Id)
        || (lesson.Number == 1 && _settings.OnboardingCompleted);

    public LessonProgress GetProgress(Lesson lesson)
    {
        bool done = IsCompleted(lesson);
        return new LessonProgress(lesson.Id, done, done ? 1.0 : 0.0);
    }

    public int CompletedCount => Lessons.Count(IsCompleted);

    public int ProgressPercent => PlannedLessonCount == 0
        ? 0
        : (int)Math.Round(100.0 * CompletedCount / PlannedLessonCount);

    public void MarkCompleted(string lessonId)
    {
        if (_settings.CompletedLessons.Contains(lessonId))
            return;
        _settings.CompletedLessons.Add(lessonId);
        _settings.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Re-raises <see cref="Changed"/> after external settings mutations
    /// (e.g. the onboarding flag) that affect derived lesson state.
    /// </summary>
    public void RaiseChanged() => Changed?.Invoke();
}
