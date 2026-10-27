using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QingShu.Toolkit.Services.Update; // UpdateChannel 枚举（属性名与通道类型同名，显式引入保持可达）

namespace QingShu.Toolkit.Models;

/// <summary>应用配置（%LOCALAPPDATA%\QingShu.Toolkit\config.json）。</summary>
public sealed class AppSettings
{
    // ---------- 外观 ----------
    /// <summary>主题：system / light / dark</summary>
    public string ThemeMode { get; set; } = "system";
    /// <summary>窗口亚克力背景（Win10 用 Acrylic，Win11 用 Mica）</summary>
    public bool UseAcrylicBackdrop { get; set; } = true;

    // ---------- 网络诊断 ----------
    /// <summary>城域网认证门户地址（如 http://10.x.x.x 或认证域名）</summary>
    public string PortalBaseUrl { get; set; } = "";
    /// <summary>门户自动登录（实验性）：配置账号后一键修复会尝试直接提交认证表单</summary>
    public bool PortalAutoLogin { get; set; } = false;
    /// <summary>认证账号（仅保存在本机 config.json）</summary>
    public string PortalUsername { get; set; } = "";
    /// <summary>认证密码（仅保存在本机 config.json）</summary>
    public string PortalPassword { get; set; } = "";
    /// <summary>认证状态查询接口（可选，如门户的 /status 页）</summary>
    public string AuthStatusUrl { get; set; } = "";
    /// <summary>判定“认证在线”的关键字（| 分隔）</summary>
    public string AuthOkKeywords { get; set; } = "success|在线|已认证|认证成功";
    /// <summary>校园内网探测地址（如教体局 OA / 内网资源 IP 或域名）</summary>
    public string IntranetProbeHost { get; set; } = "";
    public string DnsPreferred { get; set; } = "223.5.5.5";
    public string DnsAlternate { get; set; } = "119.29.29.29";
    public int PingCount { get; set; } = 4;
    public int PingTimeoutMs { get; set; } = 1500;
    /// <summary>延迟警告阈值（ms）</summary>
    public int LatencyWarnMs { get; set; } = 80;
    /// <summary>延迟不合格阈值（ms）</summary>
    public int LatencyFailMs { get; set; } = 300;
    /// <summary>测速每端点限时（秒，3-30）</summary>
    public int SpeedTestSeconds { get; set; } = 8;
    /// <summary>测速并发连接数（1-8）</summary>
    public int SpeedTestConnections { get; set; } = 4;
    /// <summary>测速总下载上限（MB，防止超大带宽长时间跑满）</summary>
    public int SpeedTestCapMb { get; set; } = 256;
    /// <summary>自定义测速 URL（优先于内置节点；留空使用内置候选）</summary>
    public string SpeedTestUrl { get; set; } = "";

    // ---------- 磁盘清理 ----------
    /// <summary>微信/QQ 久远文件阈值（天）</summary>
    public int ImOldDays { get; set; } = 180;
    /// <summary>大文件扫描阈值（MB）</summary>
    public int LargeFileMinMb { get; set; } = 100;
    /// <summary>true=删除进回收站（默认安全模式）；false=直接删除</summary>
    public bool DeleteToRecycleBin { get; set; } = true;
    public List<string> CleanupExcludeDirs { get; set; } = new();

    // ---------- 功耗 ----------
    /// <summary>auto / balanced / performance / eco</summary>
    public string PowerMode { get; set; } = "auto";
    /// <summary>对本程序应用 EcoQoS（低功耗时自动降频节能）</summary>
    public bool ApplyEcoQosToSelf { get; set; } = true;

    // ---------- 弹窗拦截（全局白名单策略） ----------
    public bool PopupBlockEnabled { get; set; } = true;
    /// <summary>进程白名单（小写、无 .exe），命中白名单的进程窗口永不拦截</summary>
    public List<string> PopupWhitelist { get; set; } = DefaultWhitelist();

    // ---------- 自动更新 ----------
    public string UpdateRepo { get; set; } = "BeiXiangGuMao/QingShu.Toolkit";
    public bool AutoCheckUpdate { get; set; } = true;
    public int UpdateCheckIntervalHours { get; set; } = 24;
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>用户选择“跳过该版本”的版本号</summary>
    public string SkipVersion { get; set; } = "";
    /// <summary>更新通道：stable（正式发布）/ beta（抢先体验，含预发布）。版本只分稳定版与 Beta 两档。</summary>
    public string UpdateChannel { get; set; } = "stable";
    /// <summary>GitHub 加速：开启后自动优选内置加速线路（失败逐条回退、直连兜底）</summary>
    public bool AccelerateGitHub { get; set; } = true;
    /// <summary>最近一次成功的加速线路 id（内部记忆，auto=无记忆）</summary>
    public string GitHubMirrorId { get; set; } = "auto";

    // ---------- 日志 ----------
    public int LogRetentionDays { get; set; } = 90;

    // ---------- PDF 报告默认值（生成时填写年级班级等） ----------
    public string LastSchool { get; set; } = "";
    public string LastGrade { get; set; } = "";
    public string LastClassName { get; set; } = "";
    public string LastReporter { get; set; } = "";
    public string LastDeviceTag { get; set; } = "";

    // ---------- 演示数据标记 ----------
    public bool FirstRunDone { get; set; } = false;

    public static List<string> DefaultWhitelist() => new()
    {
        // 系统
        "explorer", "applicationframehost", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "runtimebroker", "taskmgr",
        "systemsettings", "cmd", "conhost", "powershell", "windowsterminal", "notepad",
        "mspaint", "snippingtool", "screenclipping", "calculator", "control", "regedit",
        // 自身
        "qingshu.toolkit",
        // 浏览器（浏览器内弹窗由浏览器自身策略管理）
        "msedge", "chrome", "firefox", "360se", "360chrome", "qqbrowser", "sogouexplorer",
        // 办公与教学常用软件
        "powerpnt", "winword", "excel", "onenote", "outlook", "wps", "et", "wpp",
        "easinote", "easicam", "easipaper", "seewo", "dingding", "dingtalk",
        "qq", "wechat", "weixin", "txplatform", "qqexternal",
        "studentmain", "goprojector", "networkcontrolclient", "wltransport",
        "sunloginclient", "todesk", "anydesk", "rustdesk"
    };
}

/// <summary>弹窗拦截规则（黑名单辅助策略，配合全局白名单使用；保留以支持精细化规则）。</summary>
public sealed class PopupRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> TitlePatterns { get; set; } = new();
    public List<string> ClassPatterns { get; set; } = new();
    public List<string> ProcessPatterns { get; set; } = new();
}
