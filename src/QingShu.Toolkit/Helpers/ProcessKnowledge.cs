using System;
using System.Collections.Generic;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// 常见进程作用知识库：把进程名翻译成用户能看懂的“它是干什么的”，
/// 并标记系统关键组件（此类进程不建议整理/节能，即便出现在列表中也能一眼识别）。
/// 命中规则：小写精确匹配 → 前缀匹配（键以 * 结尾）→ 兜底“第三方程序”。
/// </summary>
public static class ProcessKnowledge
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
