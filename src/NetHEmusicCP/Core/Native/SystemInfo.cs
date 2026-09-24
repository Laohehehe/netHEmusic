using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace netHEmusic.Core.Native;

/// <summary>
/// 启动时采集一次运行环境（系统 / CPU / 内存 / 显卡 / 显示器 / 缩放），写进日志头部。
/// 用户报"某台机器上图标是方框""歌词位置不对"这类问题时，先看这几行就能定位环境差异。
/// 全部走注册表 + Win32，不依赖 WMI。
/// </summary>
internal static class SystemInfo
{
    public static List<string> Lines()
    {
        var list = new List<string>();
        Try(list, "系统  ", OsName);
        Try(list, "CPU   ", CpuName);
        Try(list, "内存  ", RamText);
        Try(list, "显卡  ", GpuName);
        Try(list, "显示器", Displays);
        return list;
    }

    private static void Try(List<string> list, string label, Func<string> f)
    {
        try { var v = f(); if (!string.IsNullOrWhiteSpace(v)) list.Add(label + ": " + v); }
        catch { }
    }

    // ---------- 注册表 ----------
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr root, string sub, uint opt, uint access, out IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved, out uint type, byte[] data, ref uint size);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);

    private static readonly IntPtr HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    private static string? Reg(string sub, string name)
    {
        IntPtr key;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub, 0, 0x20019 /*KEY_READ*/, out key) != 0) return null;
        try
        {
            uint type, size = 0;
            if (RegQueryValueExW(key, name, IntPtr.Zero, out type, null!, ref size) != 0 || size == 0) return null;
            var buf = new byte[size];
            if (RegQueryValueExW(key, name, IntPtr.Zero, out type, buf, ref size) != 0) return null;
            if (type == 1 /*REG_SZ*/ || type == 2 /*REG_EXPAND_SZ*/)
                return Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0', ' ');
            if (type == 4 /*REG_DWORD*/ && size >= 4) return BitConverter.ToUInt32(buf, 0).ToString();
            return null;
        }
        finally { RegCloseKey(key); }
    }

    // ---------- 各项 ----------
    private static string OsName()
    {
        var product = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        var disp = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var build = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber");
        var ubr = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        // Win11 的注册表 ProductName 仍然写着 "Windows 10"，靠 build 号纠正（>=22000 即 Win11）
        if (int.TryParse(build, out var bn) && bn >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        var s = product + (string.IsNullOrEmpty(disp) ? "" : " " + disp);
        if (!string.IsNullOrEmpty(build)) s += " (build " + build + (string.IsNullOrEmpty(ubr) ? "" : "." + ubr) + ")";
        return s + " " + arch;
    }

    private static string CpuName()
    {
        var name = Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        if (string.IsNullOrWhiteSpace(name)) name = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "未知";
        return name.Trim() + "  [" + Environment.ProcessorCount + " 逻辑核]";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kb);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static string RamText()
    {
        double totalGb = 0, availGb = 0;
        if (GetPhysicallyInstalledSystemMemory(out var kb) && kb > 0) totalGb = kb / 1024.0 / 1024.0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            if (totalGb <= 0) totalGb = ms.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
            availGb = ms.ullAvailPhys / 1024.0 / 1024.0 / 1024.0;
        }
        return totalGb > 0 ? totalGb.ToString("F1") + " GB（可用 " + availGb.ToString("F1") + " GB）" : "未知";
    }

    private static string GpuName()
    {
        var names = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var sub = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\" + i.ToString("D4");
            var d = Reg(sub, "DriverDesc");
            if (!string.IsNullOrWhiteSpace(d) && !names.Contains(d!.Trim())) names.Add(d.Trim());
        }
        return names.Count > 0 ? string.Join(" + ", names) : "未知";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    private static string Displays()
    {
        var parts = new List<string>();
        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (GetMonitorInfoW(hMon, ref mi))
            {
                var w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                var h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                parts.Add(w + "x" + h + (mi.dwFlags == 1 ? "(主)" : "") + "@" + mi.rcMonitor.Left + "," + mi.rcMonitor.Top);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        var dpi = GetDpiForSystem();
        return (parts.Count > 0 ? string.Join(" | ", parts) : "未知") + "  系统缩放 " + (dpi * 100 / 96) + "%";
    }
}
