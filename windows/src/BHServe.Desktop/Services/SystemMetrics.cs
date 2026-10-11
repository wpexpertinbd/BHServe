using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace BHServe.App.Services;

/// <summary>Live CPU / RAM / disk / network for the dashboard — raw Win32 (no extra packages).</summary>
public static class SystemMetrics
{
    private static ulong _prevIdle, _prevKernel, _prevUser;
    private static long _prevRx, _prevTx, _prevNetTick;

    /// <summary>Per-second network throughput (down, up) in KB/s across non-loopback interfaces.</summary>
    public static (double downKbps, double upKbps) Network()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var st = ni.GetIPv4Statistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
            var now = Environment.TickCount64;
            double down = 0, up = 0;
            if (_prevNetTick != 0)
            {
                var secs = (now - _prevNetTick) / 1000.0;
                if (secs > 0) { down = Math.Max(0, (rx - _prevRx) / 1024.0 / secs); up = Math.Max(0, (tx - _prevTx) / 1024.0 / secs); }
            }
            _prevRx = rx; _prevTx = tx; _prevNetTick = now;
            return (down, up);
        }
        catch { return (0, 0); }
    }

    public static double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var i = ToULong(idle); var k = ToULong(kernel); var u = ToULong(user);
        var idleD = i - _prevIdle; var kernelD = k - _prevKernel; var userD = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        var total = kernelD + userD;             // kernel time already includes idle
        if (total == 0) return 0;
        var used = total - idleD;
        return Math.Clamp(used * 100.0 / total, 0, 100);
    }

    public static (double usedGB, double totalGB, double pct) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return (0, 0, 0);
        var total = m.ullTotalPhys / 1073741824.0;
        var used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return (used, total, used / total * 100.0);
    }

    public static (double usedGB, double totalGB, double pct) Disk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            var total = d.TotalSize / 1073741824.0;
            var used = (d.TotalSize - d.TotalFreeSpace) / 1073741824.0;
            return (used, total, total > 0 ? used / total * 100.0 : 0);
        }
        catch { return (0, 0, 0); }
    }

    private static ulong ToULong(FILETIME f) => ((ulong)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
