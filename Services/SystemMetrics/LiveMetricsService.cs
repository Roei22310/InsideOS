using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.SystemMetrics;

/// <summary>
/// Samples all system metrics once per second on a background thread and
/// raises <see cref="SnapshotUpdated"/> (on that background thread — UI
/// consumers must marshal to the dispatcher). Cross-platform readings
/// (disk, network, uptime) are done here; platform-specific ones are
/// delegated to <see cref="ISystemMetricsSource"/>.
/// </summary>
public sealed class LiveMetricsService : IDisposable
{
    private const int BatteryPollIntervalTicks = 15; // battery changes slowly; avoid spawning pmset every second

    private readonly ISystemMetricsSource _source;
    private readonly CancellationTokenSource _cts = new();

    private CpuTicks? _previousCpu;
    private (long Rx, long Tx, long Timestamp)? _previousNet;
    private BatteryStatus? _battery;
    private int _ticksSinceBatteryPoll = BatteryPollIntervalTicks;

    public SystemStaticInfo StaticInfo { get; }

    public event Action<MetricsSnapshot>? SnapshotUpdated;

    public LiveMetricsService(ISystemMetricsSource source)
    {
        _source = source;
        StaticInfo = source.GetStaticInfo();
    }

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

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
            await SampleAsync(ct); // immediate first sample so the UI fills right away
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
        double? cpu = SampleCpu();
        var memory = _source.ReadMemoryUsage();
        var disk = ReadDisk();
        var (download, upload) = SampleNetwork();

        if (++_ticksSinceBatteryPoll >= BatteryPollIntervalTicks)
        {
            _ticksSinceBatteryPoll = 0;
            _battery = await _source.ReadBatteryAsync(ct);
        }

        var snapshot = new MetricsSnapshot(
            cpu, memory, disk, download, upload, _battery,
            TimeSpan.FromMilliseconds(Environment.TickCount64));

        SnapshotUpdated?.Invoke(snapshot);
    }

    private double? SampleCpu()
    {
        var current = _source.ReadCpuTicks();
        if (current is not { } cur)
            return null;

        var previous = _previousCpu;
        _previousCpu = cur;
        if (previous is not { } prev)
            return null; // Need two samples for a delta.

        // Unsigned arithmetic handles counter wrap-around correctly.
        uint busy = (cur.User - prev.User) + (cur.System - prev.System) + (cur.Nice - prev.Nice);
        uint idle = cur.Idle - prev.Idle;
        uint total = busy + idle;
        return total == 0 ? null : Math.Clamp(100.0 * busy / total, 0, 100);
    }

    private static DiskUsage? ReadDisk()
    {
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory);
            if (string.IsNullOrEmpty(root))
                return null;

            var drive = new DriveInfo(root);
            ulong total = (ulong)drive.TotalSize;
            ulong used = total - (ulong)drive.AvailableFreeSpace;
            return new DiskUsage(used, total);
        }
        catch
        {
            return null;
        }
    }

    private (double? Download, double? Upload) SampleNetwork()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsRelevantInterface(nic))
                    continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch
                {
                    // Interface disappeared or stats unsupported — skip it.
                }
            }
        }
        catch
        {
            return (null, null);
        }

        long now = Stopwatch.GetTimestamp();
        var previous = _previousNet;
        _previousNet = (rx, tx, now);
        if (previous is not { } prev)
            return (null, null); // Need two samples for a rate.

        double seconds = (now - prev.Timestamp) / (double)Stopwatch.Frequency;
        if (seconds <= 0)
            return (null, null);

        // Counters can reset when interfaces cycle; clamp to zero instead of showing negatives.
        double download = Math.Max(0, rx - prev.Rx) / seconds;
        double upload = Math.Max(0, tx - prev.Tx) / seconds;
        return (download, upload);
    }

    private static bool IsRelevantInterface(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
            || nic.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        // Skip macOS virtual interfaces (VPN tunnels, AirDrop, low-latency WLAN)
        // so traffic isn't counted twice.
        string name = nic.Name;
        return !name.StartsWith("utun", StringComparison.Ordinal)
            && !name.StartsWith("awdl", StringComparison.Ordinal)
            && !name.StartsWith("llw", StringComparison.Ordinal);
    }
}
