using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QingShu.Plugin.PopupGuard;

/// <summary>一个可见顶层窗口的快照（枚举产物，拦截判定输入）。</summary>
internal sealed record WinWindow(IntPtr Handle, string Title, string ClassName, int Pid, string ProcessName, bool IsToolWindow);

/// <summary>
/// Win32 互操作（插件自包含，零宿主内部依赖）：
/// 窗口枚举 / 类名识别 / 关闭与隐藏消息 / 前台切换事件钩子。
/// 与宿主内置 NativeMethods 的窗口段同源移植——内置实现卸载插件回退后行为保持一致。
/// </summary>
internal static class Win32Interop
{
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
    private const uint WM_CLOSE = 0x0010;
    private const int SW_HIDE = 0;

    /// <summary>系统外壳窗口类（托盘/桌面/输入等），永不拦截。</summary>
    private static readonly HashSet<string> SystemWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Progman", "WorkerW", "CoreWindow", "XamlExplorerHostIslandWindow",
        "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "SysListView32", "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow" // UWP 宿主窗口 ApplicationFrameWindow 走进程白名单，不按类名拦
    };

    /// <summary>
    /// 枚举可见顶层窗口（排除本进程 / 系统进程 / 被 cloak 的隐形 UWP 宿主）。
    /// 进程名按 pid 缓存（一轮枚举内同进程窗口只查一次 Process.GetProcessById）。
    /// <paramref name="skipHandles"/> 为基线句柄快照：已判定过的窗口跳过标题/类名/扩展样式/
    /// 进程名四次 P/Invoke——桌面常态 90%+ 窗口在基线内，增量枚举是扫描成本的大头
    /// （与宿主内置 NativeMethods 同源同步：事件驱动 + 增量枚举后常态 CPU 占用降至近零）。
    /// </summary>
    public static List<WinWindow> GetVisibleWindows(int ownPid, ICollection<long>? skipHandles = null)
    {
        var list = new List<WinWindow>();
        var names = new Dictionary<int, string>();
        EnumWindows((h, l) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                var handle = h.ToInt64();
                if (skipHandles != null && skipHandles.Contains(handle)) return true; // 基线窗口：跳过取信息
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
                list.Add(new WinWindow(h, title.ToString(), cls.ToString(), p, pname, tool));
            }
            catch { /* 忽略单个窗口异常 */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>窗口是否属于系统外壳（托盘/桌面/输入窗口等），此类窗口永不拦截。</summary>
    public static bool IsSystemShellWindow(WinWindow w) => SystemWindowClasses.Contains(w.ClassName);

    public static IntPtr GetForegroundWindowSafe() => GetForegroundWindow();

    /// <summary>被动关闭窗口（PostMessage WM_CLOSE：不注入、不挂钩）。</summary>
    public static bool TryCloseWindow(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>强制隐藏窗口（WM_CLOSE 被忽略的顽固弹窗兜底手段；进程仍在运行，仅窗口不可见）。</summary>
    public static bool HideWindow(IntPtr hWnd) => ShowWindow(hWnd, SW_HIDE);

    public delegate void WinEventDelegate(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hModWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    private const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    /// <summary>窗口即将显示（含后台弹出、不抢前台者）——后台广告窗不再等轮询，事件驱动即时拦。</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const int OBJID_WINDOW = 0;

    /// <summary>安装前台切换事件钩子（被动监听，不注入；失败返回 IntPtr.Zero，调用方退化为轮询）。</summary>
    public static IntPtr HookForegroundEvent(WinEventDelegate proc)
        => SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);

    /// <summary>安装窗口显示事件钩子（被动监听；EVENT_OBJECT_SHOW 高频触发，回调侧必须先做基线过滤）。</summary>
    public static IntPtr HookWindowShowEvent(WinEventDelegate proc)
        => SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);

    public static void UnhookForegroundEvent(IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }
}
