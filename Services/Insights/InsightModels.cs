using System;
using System.Collections.Generic;

namespace InsideOS.Services.Insights;

public enum InsightCategory
{
    System,
    Cpu,
    Memory,
    Disk,
    Network,
    Application,
    Battery,
}

public enum InsightConfidence
{
    Low,
    Medium,
    High,
}

/// <summary>
/// One high-level observation derived from real monitored data. The Id is
/// stable for a given rule + subject so the UI can update cards in place
/// instead of flickering.
/// </summary>
public sealed record Insight(
    string Id,
    InsightCategory Category,
    string IconKey, // StreamGeometry resource name
    string Title,
    string Explanation,
    InsightConfidence Confidence,
    DateTime Timestamp);

/// <summary>Rolling narrative of the observation window ("Today's Story").</summary>
public sealed record DailySummary(DateTime Since, IReadOnlyList<string> Lines);
