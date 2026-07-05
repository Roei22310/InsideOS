using System;

namespace InsideOS.Services.Processes;

/// <summary>
/// App-wide holder for the process the user is currently inspecting.
/// Future milestones (Action Flow, Timeline) subscribe to <see cref="Changed"/>
/// to focus their views on this process.
/// </summary>
public sealed class ProcessSelection
{
    public ProcessSample? Current { get; private set; }

    public event Action<ProcessSample?>? Changed;

    public void Select(ProcessSample? process)
    {
        if (Current?.Pid == process?.Pid && Current is null == process is null)
        {
            Current = process; // refresh data without re-notifying
            return;
        }
        Current = process;
        Changed?.Invoke(process);
    }
}
