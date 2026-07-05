using InsideOS.Services.ActionFlow;

namespace InsideOS.Services.Explanations;

/// <summary>
/// Turns a stream of per-second flow snapshots into human-readable
/// explanations. The UI only ever sees this interface (via
/// <see cref="ExplanationFeed"/>), so a future AI-backed engine can replace
/// <see cref="RuleBasedExplanationEngine"/> without any UI changes.
/// </summary>
public interface IExplanationEngine
{
    /// <summary>
    /// Consumes the next snapshot and returns the current best explanation.
    /// Called once per second from a background thread, always sequentially.
    /// Implementations may keep history to detect trends.
    /// </summary>
    Explanation Explain(ProcessFlowSnapshot snapshot);
}
