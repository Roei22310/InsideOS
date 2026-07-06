using System;
using System.Collections.Generic;

namespace InsideOS.Services.Insights;

// The insight card model itself is Narration.NarratedActivity — Insights is
// an evidence collector and feed; interpretation lives in the Narration
// Engine, the app's single reasoning layer.

/// <summary>Rolling narrative of the observation window ("Today's Story").</summary>
public sealed record DailySummary(DateTime Since, IReadOnlyList<string> Lines);
