using InsideOS.Services.ActionFlow;
using InsideOS.Services.Narration;

namespace InsideOS.Services.Explanations;

/// <summary>
/// Presentation smoothing over the shared <see cref="NarrationEngine"/> —
/// this class contains no reasoning of its own. It keeps the per-process
/// reading window the engine needs for trend detection, and applies a small
/// hysteresis (a new rule must win a few ticks in a row) so the displayed
/// explanation stays calm instead of flickering every second. The
/// interpretation itself is the same one Timeline, Insights and the
/// dashboard derive from the same evidence.
/// </summary>
public sealed class RuleBasedExplanationEngine : IExplanationEngine
{
    // Ticks a new rule must persist before switching. Must exceed the 2-tick
    // recent-average window, otherwise a single-second blip (which lingers in
    // the average for two evaluations) could flip the explanation.
    private const int SwitchStability = 3;

    private readonly MomentWindow _window = new();
    private int _pid = -1;
    private string _currentId = "";
    private Explanation _current = new("", ActivityTone.Observing);
    private string? _pendingId;
    private int _pendingTicks;

    public Explanation Explain(ProcessFlowSnapshot snapshot)
    {
        if (snapshot.Pid != _pid)
        {
            _pid = snapshot.Pid;
            _window.Clear();
            _currentId = "";
            _pendingId = null;
            _pendingTicks = 0;
        }

        if (!snapshot.ProcessExited)
            _window.Push(snapshot);

        var moment = NarrationEngine.NarrateMoment(_window, snapshot);
        var explanation = new Explanation(moment.Text, moment.Tone);

        // Hysteresis: keep the current explanation until a different rule has
        // matched for a couple of consecutive ticks. Terminated switches
        // immediately — there is nothing calmer than being gone.
        if (moment.Tone == ActivityTone.Terminated)
            return Commit(moment.RuleId, explanation);
        if (moment.RuleId == _currentId)
        {
            _pendingId = null;
            _pendingTicks = 0;
            return _current;
        }
        if (_currentId.Length == 0)
            return Commit(moment.RuleId, explanation);
        if (_pendingId == moment.RuleId)
        {
            if (++_pendingTicks >= SwitchStability)
                return Commit(moment.RuleId, explanation);
        }
        else
        {
            _pendingId = moment.RuleId;
            _pendingTicks = 1;
        }
        return _current;
    }

    private Explanation Commit(string id, Explanation explanation)
    {
        _currentId = id;
        _pendingId = null;
        _pendingTicks = 0;
        _current = explanation;
        return explanation;
    }
}
