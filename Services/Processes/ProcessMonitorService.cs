using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.Processes;

/// <summary>
/// Samples the process list once per second on a background thread and raises
/// <see cref="ProcessesUpdated"/> (on that background thread — UI consumers
/// must marshal to the dispatcher). Same architecture as LiveMetricsService.
/// Sampling starts lazily via <see cref="EnsureStarted"/> so the app pays
/// nothing until the Processes page is first opened.
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private readonly IProcessInfoSource _source;
    private readonly CancellationTokenSource _cts = new();
    private int _started;
    private volatile bool _replaying;

    public event Action<IReadOnlyList<ProcessSample>>? ProcessesUpdated;

    /// <summary>
    /// Replay seam: while replaying, live sampling continues in the
    /// background (so state stays warm) but its emission is muted, and the
    /// replay controller injects recorded frames through the very same
    /// event — consumers cannot tell the difference, by design.
    /// </summary>
    public void EnterReplay() => _replaying = true;

    public void ExitReplay() => _replaying = false;

    public void InjectReplay(IReadOnlyList<ProcessSample> samples)
    {
        if (_replaying && samples.Count > 0)
            ProcessesUpdated?.Invoke(samples);
    }

    public ProcessMonitorService(IProcessInfoSource source)
    {
        _source = source;
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            await SampleAsync(ct); // immediate first sample so the page fills right away
            while (await timer.WaitForNextTickAsync(ct))
                await SampleAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            var samples = await _source.SampleAsync(ct);
            if (samples.Count > 0 && !_replaying)
                ProcessesUpdated?.Invoke(samples);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A single failed sample (e.g. ps briefly unavailable) shouldn't kill the loop.
        }
    }
}
