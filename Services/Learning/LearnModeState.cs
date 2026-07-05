using System;

namespace InsideOS.Services.Learning;

/// <summary>App-wide switch between Monitor Mode and Learn Mode.</summary>
public sealed class LearnModeState
{
    public bool IsLearnMode { get; private set; }

    public event Action<bool>? Changed;

    public void Set(bool learnMode)
    {
        if (IsLearnMode == learnMode)
            return;
        IsLearnMode = learnMode;
        Changed?.Invoke(learnMode);
    }
}
