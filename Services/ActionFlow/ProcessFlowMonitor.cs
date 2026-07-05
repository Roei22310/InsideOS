using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InsideOS.Services.Processes;

namespace InsideOS.Services.ActionFlow;

/// <summary>
/// Produces one <see cref="ProcessFlowSnapshot"/> per second for the process
/// held by <see cref="ProcessSelection"/>. CPU/memory/status are reused from
/// the existing <see cref="ProcessMonitorService"/> tick; disk and network
/// rates are derived here from cumulative counters. Runs entirely on
/// background threads; UI consumers marshal to the dispatcher.
/// Selecting a different process triggers an immediate extra sample so the
/// visualization reacts right away.
/// </summary>
public sealed class ProcessFlowMonitor : IDisposable
{
    private readonly ProcessMonitorService _processes;
    private readonly IProcessIoSource _io;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile IReadOnlyList<ProcessSample>? _latest;
    private int _started;
    private int _trackedPid = -1;
    private (ulong Read, ulong Written, long Timestamp)? _previousDisk;
    private (ulong In, ulong Out, long Timestamp)? _previousNetwork;

    public ProcessSelection Selection { get; }

    public event Action<ProcessFlowSnapshot>? FlowUpdated;

    public ProcessFlowMonitor(ProcessMonitorService processes, ProcessSelection selection, IProcessIoSource io)
    {
        _processes = processes;
        _io = io;
        Selection = selection;
        _processes.ProcessesUpdated += samples => _latest = samples;
        Selection.Changed += _ => RequestImmediateSample();
    }

    public void EnsureStarted()
    {
        _processes.EnsureStarted();
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private void RequestImmediateSample()
    {
        if (_started == 1 && !_cts.IsCancellationRequested)
            _ = Task.Run(() => SampleAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            await SampleAsync(ct);
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
        if (!await _gate.WaitAsync(0, ct))
            return; // an overlapping sample is already in flight

        try
        {
            var selected = Selection.Current;
            if (selected is null)
            {
                _trackedPid = -1;
                return;
            }

            int pid = selected.Pid;
            if (pid != _trackedPid)
            {
                _trackedPid = pid;
                _previousDisk = null;
                _previousNetwork = null;
            }

            var sample = _latest?.FirstOrDefault(s => s.Pid == pid);
            if (sample is null)
            {
                if (_latest is not null)
                    FlowUpdated?.Invoke(ExitedSnapshot(selected));
                return; // no process list yet, or the process is gone
            }

            long now = Stopwatch.GetTimestamp();

            var cpu = sample.CpuPercent is { } cpuValue
                ? new FlowMetric(cpuValue, sample.CpuIsPrecise ? MetricQuality.Measured : MetricQuality.Calculated)
                : new FlowMetric(null, MetricQuality.Unavailable);

            var memory = sample.MemoryBytes is { } memoryValue
                ? new FlowMetric(memoryValue, MetricQuality.Measured)
                : new FlowMetric(null, MetricQuality.Unavailable);

            var (disk, diskRead, diskWrite) = SampleDisk(pid, now);
            var (network, netIn, netOut) = await SampleNetworkAsync(pid, now, ct);

            FlowUpdated?.Invoke(new ProcessFlowSnapshot(
                pid, sample.Name, sample.Status,
                cpu, memory, disk, network,
                diskRead, diskWrite, netIn, netOut,
                ProcessExited: false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // One bad sample must not kill the loop.
        }
        finally
        {
            _gate.Release();
        }
    }

    private (FlowMetric Metric, double? ReadBps, double? WriteBps) SampleDisk(int pid, long now)
    {
        if (_io.ReadDiskIo(pid) is not { } counters)
            return (new FlowMetric(null, MetricQuality.Unavailable), null, null);

        double? readBps = null, writeBps = null;
        if (_previousDisk is { } prev)
        {
            double seconds = (now - prev.Timestamp) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                readBps = Math.Max(0, (double)counters.BytesRead - prev.Read) / seconds;
                writeBps = Math.Max(0, (double)counters.BytesWritten - prev.Written) / seconds;
            }
        }
        _previousDisk = (counters.BytesRead, counters.BytesWritten, now);

        double? combined = readBps is { } r && writeBps is { } w ? r + w : null;
        return (new FlowMetric(combined, MetricQuality.Measured), readBps, writeBps);
    }

    private async Task<(FlowMetric Metric, double? InBps, double? OutBps)> SampleNetworkAsync(int pid, long now, CancellationToken ct)
    {
        var map = await _io.ReadNetworkCountersAsync(ct);
        if (map is null)
            return (new FlowMetric(null, MetricQuality.Unavailable), null, null);

        // Absent from the map = the kernel has no socket activity on record: cumulative zero.
        var counters = map.TryGetValue(pid, out var found) ? found : new NetworkCounters(0, 0);

        double? inBps = null, outBps = null;
        if (_previousNetwork is { } prev)
        {
            double seconds = (now - prev.Timestamp) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                inBps = Math.Max(0, (double)counters.BytesIn - prev.In) / seconds;
                outBps = Math.Max(0, (double)counters.BytesOut - prev.Out) / seconds;
            }
        }
        _previousNetwork = (counters.BytesIn, counters.BytesOut, now);

        double? combined = inBps is { } i && outBps is { } o ? i + o : null;
        return (new FlowMetric(combined, MetricQuality.Measured), inBps, outBps);
    }

    private static ProcessFlowSnapshot ExitedSnapshot(ProcessSample lastKnown) => new(
        lastKnown.Pid, lastKnown.Name, lastKnown.Status,
        new FlowMetric(null, MetricQuality.Unavailable),
        new FlowMetric(null, MetricQuality.Unavailable),
        new FlowMetric(null, MetricQuality.Unavailable),
        new FlowMetric(null, MetricQuality.Unavailable),
        null, null, null, null,
        ProcessExited: true);
}
