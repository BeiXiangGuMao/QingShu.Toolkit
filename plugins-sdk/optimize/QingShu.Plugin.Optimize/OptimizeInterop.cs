using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace QingShu.Plugin.Optimize;

// ================= 底层互操作与知识库（宿主 Helpers/Interop 同源自包含移植） =================
// 仅保留优化域实际用到的成员：字节格式化 / EcoQoS 功耗遏制 / 工作集整理 / UAC 身份判定 / 进程知识库。

/// <summary>展示格式化工具（宿主 Helpers/FormatHelper 同源移植，仅保留本域用到的 Bytes）。</summary>
internal static class FormatHelper
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}

/// <summary>进程功耗遏制（EcoQoS）与工作集整理 Win32 互操作（宿主 Interop/NativeMethods 两个区段同源移植）。</summary>
internal static class NativeOptimize
{
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
}

/// <summary>提权身份判定（宿主 Interop/ElevationHelper 同源移植，仅保留 IsCurrentUserAdmin）。</summary>
internal static class ElevationHelper
{
    /// <summary>当前进程是否已以管理员运行（HKLM 启动项禁用/启用前的权限预检）。</summary>
    public static bool IsCurrentUserAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

/// <summary>
/// 常见进程作用知识库（宿主 Helpers/ProcessKnowledge 同源移植）：
/// 把进程名翻译成用户能看懂的“它是干什么的”，并标记系统关键组件。
/// 命中规则：小写精确匹配 → 前缀匹配（键以 * 结尾）→ 兜底“第三方程序”。
/// </summary>
internal static class ProcessKnowledge
{
    /// <summary>进程名（小写）→ 作用说明。</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 即时通讯
        ["wechat"] = "微信", ["weixin"] = "微信", ["wechatapp"] = "微信",
        ["wechatappex"] = "微信小程序", ["wxwebview"] = "微信内嵌网页",
        ["qq"] = "QQ 聊天", ["qqnt"] = "QQ（新版）", ["txplatform"] = "QQ 多开/组件",
        ["qqexternal"] = "QQ 辅助进程", ["dingtalk"] = "钉钉", ["dingding"] = "钉钉",
        ["feishu"] = "飞书", ["lark"] = "飞书",
        // 办公与教学
        ["wps"] = "WPS 文字", ["et"] = "WPS 表格", ["wpp"] = "WPS 演示",
        ["wpsoffice"] = "WPS 办公套件", ["wpscloudsvr"] = "WPS 云服务",
        ["winword"] = "Word（Office）", ["excel"] = "Excel（Office）",
        ["powerpnt"] = "PowerPoint（Office）", ["onenote"] = "OneNote（Office）",
        ["outlook"] = "Outlook 邮件", ["msaccess"] = "Access（Office）",
        ["easinote"] = "希沃白板", ["easicam"] = "希沃摄像", ["easipaper"] = "希沃答题纸",
        ["seewo"] = "希沃组件", ["seewoservice"] = "希沃服务",
        ["cvtouch"] = "希沃触控", ["studentmain"] = "极域电子教室（学生端）",
        ["goprojector"] = "投影/教学控制", ["networkcontrolclient"] = "教学网络管控",
        ["wltransport"] = "教学传输组件", ["gatesvc"] = "联奕/教务服务组件",
        // 浏览器
        ["msedge"] = "Edge 浏览器", ["chrome"] = "Chrome 浏览器",
        ["firefox"] = "Firefox 浏览器", ["360se"] = "360 安全浏览器",
        ["360chrome"] = "360 极速浏览器", ["qqbrowser"] = "QQ 浏览器",
        ["sogouexplorer"] = "搜狗浏览器", ["brave"] = "Brave 浏览器",
        // 远控与安全
        ["sunloginclient"] = "向日葵远程控制", ["todesk"] = "ToDesk 远程控制",
        ["anydesk"] = "AnyDesk 远程控制", ["rustdesk"] = "RustDesk 远程控制",
        ["teamviewer"] = "TeamViewer 远程控制", ["msmpeng"] = "Defender 反恶意软件",
        ["nissrv"] = "Defender 网络检查", ["360tray"] = "360 安全卫士", ["360safe"] = "360 安全卫士",
        ["360sd"] = "360 杀毒", ["hipsdaemon"] = "火绒安全服务", ["hipstray"] = "火绒安全托盘",
        ["wsctrl"] = "安全管控组件", ["smartscreen"] = "系统 SmartScreen",
        ["securityhealthsystray"] = "Windows 安全中心", ["securityhealthservice"] = "Windows 安全中心服务",
        // 网盘 / 下载 / 多媒体
        ["baidunetdisk"] = "百度网盘", ["baidunetdiskhost"] = "百度网盘组件",
        ["aliyundrive"] = "阿里云盘", ["thunder"] = "迅雷", ["xunlei"] = "迅雷",
        ["cloudmusic"] = "网易云音乐", ["qqmusic"] = "QQ音乐", ["kugou"] = "酷狗音乐",
        ["bilibili"] = "哔哩哔哩", ["potplayer"] = "PotPlayer 播放器", ["vlc"] = "VLC 播放器",
        ["steam"] = "Steam 平台", ["wegame"] = "WeGame 平台",
        // 系统组件（一般不建议整理/降频）
        ["ctfmon"] = "输入法框架（系统）", ["dllhost"] = "COM 代理（系统）",
        ["searchhost"] = "系统搜索（系统）", ["searchapplication"] = "系统搜索（系统）",
        ["searchindexer"] = "搜索索引（系统）", ["startmenuexperiencehost"] = "开始菜单（系统）",
        ["shellexperiencehost"] = "系统 Shell（系统）", ["textinputhost"] = "文本输入（系统）",
        ["fontdrvhost"] = "字体驱动（系统）", ["fontcache"] = "字体缓存（系统）",
        ["wudfhost"] = "驱动框架（系统）", ["spoolsv"] = "打印后台（系统）",
        ["dashost"] = "设备关联（系统）", ["sihost"] = "Shell 基础设施（系统）",
        ["taskhostw"] = "计划任务宿主（系统）", ["audiodg"] = "音频引擎（系统）",
        ["applicationframehost"] = "UWP 宿主（系统）", ["runtimebroker"] = "运行时代理（系统）",
        ["explorer"] = "文件资源管理器（系统）", ["taskmgr"] = "任务管理器（系统）",
        ["cmd"] = "命令提示符", ["conhost"] = "控制台宿主", ["powershell"] = "PowerShell",
        ["windowsterminal"] = "Windows 终端", ["notepad"] = "记事本", ["mspaint"] = "画图",
        ["snippingtool"] = "截图工具", ["calculator"] = "计算器",
        // 开发与工具
        ["code"] = "VS Code", ["devenv"] = "Visual Studio", ["python"] = "Python 运行环境",
        ["pythonw"] = "Python 运行环境", ["everything"] = "Everything 文件搜索",
        ["notepad++"] = "Notepad++ 编辑器",
    };

    /// <summary>系统关键组件（标注用途，不建议整理内存 / 应用 EcoQoS）。</summary>
    private static readonly HashSet<string> SystemSet = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctfmon", "dllhost", "searchhost", "searchapplication", "searchindexer",
        "startmenuexperiencehost", "shellexperiencehost", "textinputhost",
        "fontdrvhost", "fontcache", "wudfhost", "spoolsv", "dashost", "sihost",
        "taskhostw", "audiodg", "applicationframehost", "runtimebroker", "explorer",
        "msmpeng", "nissrv", "smartscreen", "securityhealthsystray", "securityhealthservice",
        "taskmgr", "conhost"
    };

    /// <summary>作用说明（未收录时给出兜底文案）。</summary>
    public static string Describe(string processName)
    {
        var key = (processName ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return "未知进程";
        if (Map.TryGetValue(key, out var desc)) return desc;
        foreach (var (k, v) in Map)
            if (k.EndsWith("*", StringComparison.Ordinal) && key.StartsWith(k[..^1], StringComparison.Ordinal))
                return v;
        return "第三方程序或服务";
    }

    /// <summary>是否系统关键组件（UI 显示“系统”标记并提示谨慎）。</summary>
    public static bool IsSystemComponent(string processName)
        => SystemSet.Contains((processName ?? "").Trim());
}
