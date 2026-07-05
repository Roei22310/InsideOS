namespace InsideOS.Services.Explanations;

public enum ExplanationKind
{
    Observing,   // still collecting data
    Idle,        // nothing notable happening
    Activity,    // the process is doing something
    Terminated,  // the process is gone
}

/// <summary>A human-readable interpretation of what the selected process is
/// likely doing. Uncertainty is always expressed in the text itself
/// (likely / probably / possibly) — assumptions are never stated as facts.</summary>
public sealed record Explanation(string Text, ExplanationKind Kind);
