using System.Collections.Generic;

namespace InsideOS.Services.Learning;

public enum LearnTopicId
{
    Process,
    Cpu,
    Memory,
    Disk,
    Network,
}

/// <summary>Reader knowledge levels. Only Beginner content ships today;
/// the catalog is keyed by level so more can be added without API changes.</summary>
public enum KnowledgeLevel
{
    Beginner,
}

/// <summary>
/// Static educational content for one topic. Pure data — structured so a
/// future AI layer can generate or enrich entries without touching the UI.
/// </summary>
public sealed record LearnContent(
    LearnTopicId Topic,
    string Title,
    string WhatItDoes,
    IReadOnlyList<string> RelatedConcepts);
