using System;

namespace InsideOS.Services.SystemMetrics;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        double value = Math.Max(bytes, 0);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} B" : $"{value:0.0} {Units[unit]}";
    }

    public static string Speed(double bytesPerSecond) => $"{Bytes(bytesPerSecond)}/s";

    public static string Uptime(TimeSpan uptime) =>
        uptime.Days > 0
            ? $"{uptime.Days}d {uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}"
            : $"{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
}
