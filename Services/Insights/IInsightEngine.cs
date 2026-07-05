using System.Collections.Generic;

namespace InsideOS.Services.Insights;

/// <summary>
/// Turns an evidence bundle into high-level observations. Deterministic and
/// fully local — same evidence in, same insights out. A richer engine can
/// replace the rule-based one without touching the UI or the evidence
/// collector.
/// </summary>
public interface IInsightEngine
{
    IReadOnlyList<Insight> Analyze(InsightEvidence evidence);
}
