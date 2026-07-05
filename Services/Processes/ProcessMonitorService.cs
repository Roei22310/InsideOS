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

    public event Action<IReadOnlyList<ProcessSample>>? ProcessesUpdated;

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
            if (samples.Count > 0)
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
