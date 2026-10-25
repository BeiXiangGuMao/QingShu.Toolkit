using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QingShu.Toolkit.Interop;

/// <summary>可见顶层窗口快照。</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, int Pid, string ProcessName, bool IsToolWindow);

/// <summary>
/// Win32 互操作集合：电源状态 / 进程功耗遏制(EcoQoS) / 工作集整理 /
/// 枚举窗口与弹窗拦截 / 回收站 / WinINet 代理刷新 / 原生消息框。
/// </summary>
internal static partial class NativeMethods
{
    #region 电源状态

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 0=电池 1=已接通 255=未知
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public static bool TryGetPowerStatus(out bool acOnline, out byte batteryPercent)
    {
        acOnline = true;
        batteryPercent = 100;
        try
        {
            if (!GetSystemPowerStatus(out var s)) return false;
            acOnline = s.ACLineStatus == 1;
            batteryPercent = s.BatteryLifePercent;
            return s.ACLineStatus != 255;
        }
        catch { return false; }
    }

    #endregion

    #region 进程功耗遏制 EcoQoS

    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernelbase.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>对指定进程启用/关闭 EcoQoS（Win10 1709+）。返回是否成功。</summary>
    public static bool SetProcessEcoQos(int pid, bool enable)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            var st = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };
            return SetProcessInformation(h, ProcessPowerThrottling, ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    #endregion

    #region 工作集整理

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>整理指定进程工作集，返回释放字节数（失败返回 -1）。</summary>
    public static long TryEmptyWorkingSet(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return -1;
            bool ok = EmptyWorkingSet(h);
            return ok ? 0 : -1;
        }
        catch { return -1; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public static (uint LoadPercent, ulong TotalPhys, ulong AvailPhys) GetMemoryStatus()
    {
        var m = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(m)) return (m.dwMemoryLoad, m.ullTotalPhys, m.ullAvailPhys);
        return (0, 0, 0);
    }

    #endregion

    #region 窗口枚举 / 弹窗拦截

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    internal const uint WM_CLOSE = 0x0010;

    private static readonly HashSet<string> SystemWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Progman", "WorkerW", "CoreWindow", "XamlExplorerHostIslandWindow",
        "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "SysListView32", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow" // UWP 宿主窗口 ApplicationFrameWindow 走进程白名单，不按类名拦
    };

    public static List<WindowInfo> GetVisibleWindows(int ownPid)
    {
        var list = new List<WindowInfo>();
        var names = new Dictionary<int, string>();
        EnumWindows((h, l) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                int p = (int)pid;
                if (p == ownPid || p <= 4) return true;
                var title = new StringBuilder(512);
                GetWindowText(h, title, 512);
                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                bool tool = (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0;
                string pname;
                if (!names.TryGetValue(p, out var n))
                {
                    try { n = Process.GetProcessById(p).ProcessName; }
                    catch { n = ""; }
                    names[p] = n;
                }
                pname = n!;
                list.Add(new WindowInfo(h, title.ToString(), cls.ToString(), p, pname, tool));
            }
            catch { /* 忽略单个窗口异常 */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>判断窗口是否属于系统外壳（托盘/桌面/输入窗口等），此类窗口永不拦截。</summary>
    public static bool IsSystemShellWindow(WindowInfo w)
        => SystemWindowClasses.Contains(w.ClassName);

    public static IntPtr GetForegroundWindowSafe() => GetForegroundWindow();

    public static bool TryCloseWindow(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hModWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    public static IntPtr HookForegroundEvent(WinEventDelegate proc)
        => SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);

    public static void UnhookForegroundEvent(IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }

    #endregion

    #region 回收站

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    public static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        if (hr != 0) return (0, 0);
        return (info.i64Size, info.i64NumItems);
    }

    /// <summary>清空回收站（不确认、无界面、无声音）。返回 HRESULT。</summary>
    public static int EmptyRecycleBin()
        => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    #endregion

    #region 文件放入回收站

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public short fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    private const uint FO_DELETE = 3;
    private const short FOF_ALLOWUNDO = 0x40;
    private const short FOF_NOCONFIRMATION = 0x10;
    private const short FOF_SILENT = 0x4;
    private const short FOF_NOERRORUI = 0x400;

    /// <summary>将文件/目录移入回收站（安静模式）。0 = 成功。</summary>
    public static int DeleteToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            pTo = "",
            fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = ""
        };
        return SHFileOperation(ref op);
    }

    #endregion

    #region WinINet 代理刷新

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public static void NotifyProxySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    #endregion

    #region 原生消息框

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void ShowMessageBox(string text, string caption, uint flags = 0x40)
        => MessageBox(IntPtr.Zero, text, caption, flags);

    #endregion
}
