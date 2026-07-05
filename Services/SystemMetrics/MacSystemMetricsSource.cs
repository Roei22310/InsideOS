using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InsideOS.Services.SystemMetrics;

/// <summary>
/// macOS implementation backed by Mach host statistics (CPU, memory),
/// sysctl (hardware facts) and pmset (battery).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacSystemMetricsSource : ISystemMetricsSource
{
    private const int HostCpuLoadInfoFlavor = 3;  // HOST_CPU_LOAD_INFO
    private const int HostVmInfo64Flavor = 4;     // HOST_VM_INFO64
    private const uint VmStatistics64Count = 38;  // sizeof(vm_statistics64) / sizeof(integer_t)

    private readonly uint _host;
    private readonly nuint _pageSize;
    private readonly ulong _totalMemoryBytes;

    public MacSystemMetricsSource()
    {
        _host = mach_host_self();
        host_page_size(_host, out _pageSize);
        _totalMemoryBytes = ReadSysctlUInt64("hw.memsize");
    }

    public SystemStaticInfo GetStaticInfo() => new(
        OsVersion: $"macOS {Environment.OSVersion.Version}",
        CpuModel: ReadSysctlString("machdep.cpu.brand_string") ?? "Unknown processor",
        TotalMemoryBytes: _totalMemoryBytes,
        LogicalCores: Environment.ProcessorCount);

    public CpuTicks? ReadCpuTicks()
    {
        var info = new HostCpuLoadInfo();
        uint count = 4;
        return host_statistics(_host, HostCpuLoadInfoFlavor, ref info, ref count) == 0
            ? new CpuTicks(info.User, info.System, info.Idle, info.Nice)
            : null;
    }

    public MemoryUsage? ReadMemoryUsage()
    {
        var vm = new VmStatistics64();
        uint count = VmStatistics64Count;
        if (host_statistics64(_host, HostVmInfo64Flavor, ref vm, ref count) != 0)
            return null;

        // Activity Monitor's "Memory Used": app memory (internal minus purgeable)
        // + wired + compressed pages.
        ulong usedPages = (ulong)vm.InternalPageCount - vm.PurgeableCount
                        + vm.WireCount + vm.CompressorPageCount;
        return new MemoryUsage(usedPages * _pageSize, _totalMemoryBytes);
    }

    public async Task<BatteryStatus?> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pmset", "-g batt")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var match = BatteryRegex().Match(output);
            if (!match.Success)
                return null; // No battery present (e.g. desktop Mac).

            int percent = Math.Clamp(int.Parse(match.Groups[1].Value), 0, 100);
            return new BatteryStatus(percent, DescribeState(match.Groups[2].Value.Trim()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeState(string raw) => raw switch
    {
        "charging" => "Charging",
        "discharging" => "Running on battery",
        "charged" => "Fully charged",
        "finishing charge" => "Finishing charge",
        "AC attached" => "Plugged in, not charging",
        { Length: > 0 } => char.ToUpperInvariant(raw[0]) + raw[1..],
        _ => "Unknown state",
    };

    [GeneratedRegex(@"(\d{1,3})%;\s*([^;]+);")]
    private static partial Regex BatteryRegex();

    private static string? ReadSysctlString(string name)
    {
        nint len = 0;
        if (sysctlbyname(name, null, ref len, IntPtr.Zero, 0) != 0 || len <= 0)
            return null;
        var buffer = new byte[len];
        if (sysctlbyname(name, buffer, ref len, IntPtr.Zero, 0) != 0)
            return null;
        return Encoding.UTF8.GetString(buffer, 0, (int)len).TrimEnd('\0').Trim();
    }

    private static ulong ReadSysctlUInt64(string name)
    {
        ulong value = 0;
        nint len = sizeof(ulong);
        return sysctlbyname(name, ref value, ref len, IntPtr.Zero, 0) == 0 ? value : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostCpuLoadInfo
    {
        public uint User, System, Idle, Nice;
    }

    /// <summary>Mirrors struct vm_statistics64 from mach/vm_statistics.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint FreeCount, ActiveCount, InactiveCount, WireCount;
        public ulong ZeroFillCount, Reactivations, Pageins, Pageouts, Faults, CowFaults, Lookups, Hits, Purges;
        public uint PurgeableCount, SpeculativeCount;
        public ulong Decompressions, Compressions, Swapins, Swapouts;
        public uint CompressorPageCount, ThrottledCount, ExternalPageCount, InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    [DllImport("libSystem")]
    private static extern uint mach_host_self();

    [DllImport("libSystem")]
    private static extern int host_statistics(uint host, int flavor, ref HostCpuLoadInfo info, ref uint count);

    [DllImport("libSystem")]
    private static extern int host_statistics64(uint host, int flavor, ref VmStatistics64 info, ref uint count);

    [DllImport("libSystem")]
    private static extern int host_page_size(uint host, out nuint pageSize);

    [DllImport("libSystem")]
    private static extern int sysctlbyname(string name, byte[]? oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    [DllImport("libSystem")]
    private static extern int sysctlbyname(string name, ref ulong oldp, ref nint oldlenp, IntPtr newp, nint newlen);
}
