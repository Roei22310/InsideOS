using InsideOS.Services.Narration;

namespace InsideOS.Services.Explanations;

/// <summary>A human-readable interpretation of what the selected process is
/// likely doing, produced by the shared Narration Engine and smoothed for
/// display. Uncertainty is always expressed in the text itself
/// (likely / probably / possibly) — assumptions are never stated as facts.</summary>
public sealed record Explanation(string Text, ActivityTone Kind);
