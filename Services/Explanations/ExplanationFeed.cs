using System;
using InsideOS.Services.ActionFlow;

namespace InsideOS.Services.Explanations;

/// <summary>
/// Bridges the flow monitor to whichever explanation engine is plugged in.
/// The UI subscribes here and never sees the engine type, so swapping the
/// rule engine for a future AI engine is a one-line change in MainWindow.
/// Events are raised on the monitor's background thread.
/// </summary>
public sealed class ExplanationFeed
{
    public event Action<Explanation>? ExplanationUpdated;

    public ExplanationFeed(ProcessFlowMonitor flow, IExplanationEngine engine)
    {
        flow.FlowUpdated += snapshot => ExplanationUpdated?.Invoke(engine.Explain(snapshot));
    }
}
