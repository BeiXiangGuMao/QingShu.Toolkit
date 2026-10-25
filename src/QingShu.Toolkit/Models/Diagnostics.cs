using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Models;

// ================= 诊断模型 =================

public enum CheckStatus { Pending, Running, Pass, Warning, Fail, Fixed, Skipped }

public enum CheckCategory
{
    PhysicalLink,   // 物理与链路
    HardwareDriver, // 硬件与驱动
    IpConfig,       // IP 配置
    GatewayRouting, // 网关与路由
    Dns,            // DNS
    PortalAuth,     // 城域网认证
    Intranet,       // 校园内网
    Internet,       // 互联网
    SystemStack,    // 系统网络栈
    Clock,          // 系统时钟
    ProxyVpn,       // 代理与 VPN
    Quality,        // 连接质量
    Throughput      // 测速与吞吐（低速专项）
}

public static class CheckCategoryText
{
    public static string Of(CheckCategory c) => c switch
    {
        CheckCategory.PhysicalLink => "物理链路",
        CheckCategory.HardwareDriver => "硬件驱动",
        CheckCategory.IpConfig => "IP 配置",
        CheckCategory.GatewayRouting => "网关路由",
        CheckCategory.Dns => "DNS",
        CheckCategory.PortalAuth => "城域网认证",
        CheckCategory.Intranet => "校园内网",
        CheckCategory.Internet => "互联网",
        CheckCategory.SystemStack => "系统网络栈",
        CheckCategory.Clock => "系统时钟",
        CheckCategory.ProxyVpn => "代理与 VPN",
        CheckCategory.Quality => "连接质量",
        CheckCategory.Throughput => "测速吞吐",
        _ => "其他"
    };
}

/// <summary>
/// 单个检查项（可直接绑定 UI：状态以多个 Chip 的可见性表达，避免转换器）。
/// </summary>
public sealed partial class CheckItem : ObservableObject
{
    public CheckItem(string id, string name, CheckCategory category)
    {
        Id = id; Name = name; Category = category;
    }

    public string Id { get; }
    public string Name { get; }
    public CheckCategory Category { get; }

    [ObservableProperty] private CheckStatus _status = CheckStatus.Pending;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _suggestion = "";
    [ObservableProperty] private bool _canAutoFix;
    [ObservableProperty] private long _durationMs;
    [ObservableProperty] private List<string> _details = new();
    [ObservableProperty] private bool _isExpanded;

    public string StatusText => Status switch
    {
        CheckStatus.Pending => "待检查",
        CheckStatus.Running => "检查中",
        CheckStatus.Pass => "通过",
        CheckStatus.Warning => "警告",
        CheckStatus.Fail => "未通过",
        CheckStatus.Fixed => "已修复",
        CheckStatus.Skipped => "已跳过",
        _ => ""
    };

    public string CategoryText => CheckCategoryText.Of(Category);
    public string DurationText => DurationMs <= 0 ? "" : FormatHelper.Duration(DurationMs);
    public bool SuggestionVis => !string.IsNullOrWhiteSpace(Suggestion) && Status is CheckStatus.Warning or CheckStatus.Fail or CheckStatus.Fixed;
    public bool DetailsVis => Details is { Count: > 0 };

    // 状态 Chip 可见性
    public bool PassVis => Status == CheckStatus.Pass;
    public bool WarnVis => Status == CheckStatus.Warning;
    public bool FailVis => Status == CheckStatus.Fail;
    public bool FixedVis => Status == CheckStatus.Fixed;
    public bool RunningVis => Status == CheckStatus.Running;
    public bool PendingVis => Status == CheckStatus.Pending;
    public bool SkippedVis => Status == CheckStatus.Skipped;
}

/// <summary>检查产出（引擎内部传递，落库/UI 应用到 CheckItem）。</summary>
public sealed class CheckOutcome
{
    public CheckStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public bool CanAutoFix { get; init; }
    public List<string> Details { get; init; } = new();

    // ---- 工厂方法（参数灵活：可传 string / string[] / List<string>） ----

    public static CheckOutcome Pass(string summary, params object[] parts)
        => new() { Status = CheckStatus.Pass, Summary = summary, Details = Flatten(parts) };

    public static CheckOutcome Warn(string summary, string suggestion, params object[] parts)
        => new() { Status = CheckStatus.Warning, Summary = summary, Suggestion = suggestion, Details = Flatten(parts) };

    /// <summary>显式重载：带 canAutoFix 标记（支持 canFix: true 命名参数写法，与 Fail 对齐）。</summary>
    public static CheckOutcome Warn(string summary, string suggestion, bool canFix, params string[] details)
        => new() { Status = CheckStatus.Warning, Summary = summary, Suggestion = suggestion, CanAutoFix = canFix, Details = new List<string>(details) };

    /// <summary>智能重载：可传字符串/集合，bool 参数置于集合前。</summary>
    public static CheckOutcome Fail(string summary, string suggestion, params object[] parts)
        => new() { Status = CheckStatus.Fail, Summary = summary, Suggestion = suggestion, Details = Flatten(parts) };

    /// <summary>显式重载：带 canAutoFix 标记（支持 canFix: true 命名参数写法）。</summary>
    public static CheckOutcome Fail(string summary, string suggestion, bool canFix, params string[] details)
        => new() { Status = CheckStatus.Fail, Summary = summary, Suggestion = suggestion, CanAutoFix = canFix, Details = new List<string>(details) };

    public static CheckOutcome Skip(string summary) => new() { Status = CheckStatus.Skipped, Summary = summary };

    private static List<string> Flatten(object[] parts)
    {
        var list = new List<string>();
        foreach (var p in parts)
        {
            switch (p)
            {
                case null: break;
                case string s: list.Add(s); break;
                case string[] arr: list.AddRange(arr); break;
                case IEnumerable<string> en: list.AddRange(en); break;
                default: list.Add(p.ToString() ?? ""); break;
            }
        }
        return list;
    }
}

public enum RunMode { Full, Quick, Speed }

/// <summary>修复动作状态。</summary>
public enum RepairStatus { Pending, Running, Ok, Fail, Skipped }

/// <summary>自动修复动作。</summary>
public sealed partial class RepairAction : ObservableObject
{
    public RepairAction(string id, string title, string detail, bool needsAdmin, bool risky, string[] relatedCheckIds)
    {
        Id = id; Title = title; Detail = detail; NeedsAdmin = needsAdmin; Risky = risky; RelatedCheckIds = relatedCheckIds;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool NeedsAdmin { get; }
    public bool Risky { get; }
    public string[] RelatedCheckIds { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private RepairStatus _status = RepairStatus.Pending;
    [ObservableProperty] private string _output = "";

    public string AdminText => NeedsAdmin ? "需要管理员" : "普通权限";
    public string StatusText => Status switch
    {
        RepairStatus.Pending => "待执行",
        RepairStatus.Running => "执行中",
        RepairStatus.Ok => "成功",
        RepairStatus.Fail => "失败",
        RepairStatus.Skipped => "已跳过",
        _ => ""
    };
    public bool OkVis => Status == RepairStatus.Ok;
    public bool FailVis => Status == RepairStatus.Fail;
    public bool RunningVis => Status == RepairStatus.Running;
    public bool PendingVis => Status is RepairStatus.Pending or RepairStatus.Skipped;
}

/// <summary>一次完整诊断会话的汇总（供 PDF 报告与日志）。</summary>
public sealed class DiagnosticSessionReport
{
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public RunMode Mode { get; set; }
    public List<CheckItem> Results { get; set; } = new();
    public List<string> RepairLog { get; set; } = new();
    /// <summary>低速专项：嫌疑原因分析（仅 RunMode.Speed 产出）。</summary>
    public List<SlowCause> SlowCauses { get; set; } = new();

    public int PassCount => CountOf(CheckStatus.Pass) + CountOf(CheckStatus.Fixed);
    public int WarnCount => CountOf(CheckStatus.Warning);
    public int FailCount => CountOf(CheckStatus.Fail);
    public int SkipCount => CountOf(CheckStatus.Skipped);
    public int CountOf(CheckStatus s) { int n = 0; foreach (var r in Results) if (r.Status == s) n++; return n; }

    public string ModeText => Mode switch
    {
        RunMode.Full => "全面诊断",
        RunMode.Quick => "快速体检",
        RunMode.Speed => "网速与低速分析",
        _ => ""
    };
}
