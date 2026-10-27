using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
        => TrySetProcessEcoQos(pid, enable, out _);

    /// <summary>
    /// 带失败原因的 EcoQoS 设置。原因分类：
    ///  · "os"    ：系统早于 Win10 1709，不支持 ProcessPowerThrottling
    ///  · "open"  ：OpenProcess 失败（目标受保护/权限不足，如管理员进程、反作弊保护）
    ///  · "set"   ：SetProcessInformation 失败（系统拒绝，含前台自动豁免策略）
    /// </summary>
    public static bool TrySetProcessEcoQos(int pid, bool enable, out string error)
    {
        error = "";
        try
        {
            // Win10 1709 = build 16299 起才提供 ProcessPowerThrottling 信息类
            if (Environment.OSVersion.Version.Build < 16299) { error = "os"; return false; }
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { error = "open"; return false; }
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = enable ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                };
                if (SetProcessInformation(h, ProcessPowerThrottling, ref st,
                        Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())) return true;
                error = "set";
                return false;
            }
            finally { CloseHandle(h); }
        }
        catch { error = "open"; return false; }
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

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    /// <summary>强制隐藏窗口（WM_CLOSE 被忽略的顽固弹窗兜底手段；进程仍在运行，仅窗口不可见）。</summary>
    public static bool HideWindow(IntPtr hWnd) => ShowWindow(hWnd, SW_HIDE);

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

    /// <summary>
    /// 查询回收站占用（所有固定分区求和）。
    /// 历史 bug： pszRootPath 传 null 不被文档支持，部分系统返回失败 → 被误判“回收站是空的”，
    /// 回收站目标扫描恒为 0、清理时永远跳过（用户实测“勾了回收站也删不掉”）。
    /// 逐分区查询是文档明确支持的行为，结果稳定可靠。
    /// </summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        long bytes = 0, items = 0;
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (SHQueryRecycleBin(d.Name, ref info) == 0)
                {
                    bytes += info.i64Size;
                    items += info.i64NumItems;
                }
            }
            return (bytes, items);
        }
        catch { }
        // 兜底：盘符枚举异常时退回整体查询（旧行为）
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
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

    /// <summary>
    /// SHFileOperation FO_DELETE 公共实现。pFrom 为以 \0 分隔、\0\0 结尾的多路径列表。
    /// 历史 bug（用户实测「移入回收站不可行」）：线程池是 MTA，SHFileOperation 属 shell API，
    /// 回收站（ALLOWUNDO）操作在 MTA 线程下 shell 上下文初始化不完整，可能静默失败或退化为
    /// 永久删除 —— 必须切 STA 线程执行。FOF_SILENT|FOF_NOERRORUI 下无 UI 阻塞点，同步等待安全。
    /// </summary>
    private static int ShDeleteToRecycleBin(string multiPathList)
    {
        int result = 0;
        var t = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCTW
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,
                    pFrom = multiPathList,
                    pTo = "",
                    fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
                    fAnyOperationsAborted = 0,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = ""
                };
                result = SHFileOperation(ref op);
            }
            catch { result = unchecked((int)0x8000FFFF); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>将单个文件/目录移入回收站（安静模式）。0 = 成功。</summary>
    public static int DeleteToRecycleBin(string path)
        => ShDeleteToRecycleBin(path + "\0");

    /// <summary>
    /// 批量将多个文件/目录移入回收站：pFrom 支持多路径，单次调用完成整批删除，
    /// 比逐文件调用快一个数量级（数千临时文件场景的历史卡顿点）。
    /// 历史 bug：SHFileOperation 的 pFrom 列表存在约 32K 字符的隐性上限，
    /// 临时文件路径平均 80-150 字符，按 400 条/批拼串轻易超限 → 整批静默失败。
    /// 此处按累计字符预算（16000 字符/批）切批，远低于上限且仍保持批量优势。
    /// 返回 0 = 所有批次调用成功（不代表每一项都删净）；非 0 = 存在失败，调用方应逐项复核。
    /// </summary>
    public static int DeleteToRecycleBinBatch(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;
        int result = 0;
        var buffer = new List<string>(64);
        int chars = 0;
        foreach (var p in paths)
        {
            if (chars + p.Length + 1 > 16000 && buffer.Count > 0)
            {
                var r = ShDeleteToRecycleBin(string.Join("\0", buffer) + "\0");
                if (r != 0) result = r;
                buffer.Clear();
                chars = 0;
            }
            buffer.Add(p);
            chars += p.Length + 1;
        }
        if (buffer.Count > 0)
        {
            var r = ShDeleteToRecycleBin(string.Join("\0", buffer) + "\0");
            if (r != 0) result = r;
        }
        return result;
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
