using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace QingShu.Plugin.NetworkDiag;

// ================= 诊断模型（宿主 Models/Diagnostics.cs 同源移植） =================
// CheckItem / RepairAction 保持 ObservableObject + [ObservableProperty]；
// 为运行时模板（XamlReader 经典 Binding）新增 Visibility 类型 VisState 属性：
// 经典 Binding 无 bool→Visibility 隐式转换（x:Bind 的隐式转换是编译期生成器行为），
// 行模型直接暴露 Microsoft.UI.Xaml.Visibility 类型属性（跨 ALC 类型同一）供模板绑定免转换器；
// 枚举属性（Status / RepairStatus）变更经 partial void OnXxxChanged 显式补 OnPropertyChanged
// 同步派生属性（内置页 x:Bind 按属性引用自动刷新，插件经典 Binding 需要显式补）。

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
    Throughput,     // 测速与吞吐（低速专项）
    Rare            // 疑难排查（罕见/边缘问题）
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
        CheckCategory.Rare => "疑难排查",
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

    // ---- 派生展示属性（枚举/关联属性变更通知在 partial OnChanged 中显式补） ----

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

    // 状态 Chip 可见性（bool，视图层 Vis 转换器用）
    public bool PassVis => Status == CheckStatus.Pass;
    public bool WarnVis => Status == CheckStatus.Warning;
    public bool FailVis => Status == CheckStatus.Fail;
    public bool FixedVis => Status == CheckStatus.Fixed;
    public bool RunningVis => Status == CheckStatus.Running;
    public bool PendingVis => Status == CheckStatus.Pending;
    public bool SkippedVis => Status == CheckStatus.Skipped;

    // 状态 Chip 可见性（Visibility 类型，运行时模板经典 Binding 直接绑定）
    public Visibility PassVisState => PassVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisState => WarnVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailVisState => FailVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FixedVisState => FixedVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisState => RunningVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisState => PendingVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SkippedVisState => SkippedVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SuggestionVisState => SuggestionVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailsVisState => DetailsVis ? Visibility.Visible : Visibility.Collapsed;

    partial void OnStatusChanged(CheckStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SuggestionVis));
        OnPropertyChanged(nameof(SuggestionVisState));
        OnPropertyChanged(nameof(PassVis));
        OnPropertyChanged(nameof(PassVisState));
        OnPropertyChanged(nameof(WarnVis));
        OnPropertyChanged(nameof(WarnVisState));
        OnPropertyChanged(nameof(FailVis));
        OnPropertyChanged(nameof(FailVisState));
        OnPropertyChanged(nameof(FixedVis));
        OnPropertyChanged(nameof(FixedVisState));
        OnPropertyChanged(nameof(RunningVis));
        OnPropertyChanged(nameof(RunningVisState));
        OnPropertyChanged(nameof(PendingVis));
        OnPropertyChanged(nameof(PendingVisState));
        OnPropertyChanged(nameof(SkippedVis));
        OnPropertyChanged(nameof(SkippedVisState));
    }

    partial void OnSuggestionChanged(string value)
    {
        OnPropertyChanged(nameof(SuggestionVis));
        OnPropertyChanged(nameof(SuggestionVisState));
    }

    partial void OnDurationMsChanged(long value) => OnPropertyChanged(nameof(DurationText));

    partial void OnDetailsChanged(List<string> value)
    {
        OnPropertyChanged(nameof(DetailsVis));
        OnPropertyChanged(nameof(DetailsVisState));
    }
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

/// <summary>自动修复动作（宿主 Models/Diagnostics.cs 同源移植）。</summary>
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

    /// <summary>open_portal 专用：诊断时检测到的认证门户地址（优先于设置值）。</summary>
    public string? PortalUrl { get; set; }

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
    public bool RiskyVis => Risky;

    // Visibility 类型态属性（运行时模板经典 Binding 直接绑定）
    public Visibility OkVisState => OkVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailVisState => FailVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisState => RunningVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisState => PendingVis ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RiskyVisState => RiskyVis ? Visibility.Visible : Visibility.Collapsed;

    partial void OnStatusChanged(RepairStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OkVis));
        OnPropertyChanged(nameof(OkVisState));
        OnPropertyChanged(nameof(FailVis));
        OnPropertyChanged(nameof(FailVisState));
        OnPropertyChanged(nameof(RunningVis));
        OnPropertyChanged(nameof(RunningVisState));
        OnPropertyChanged(nameof(PendingVis));
        OnPropertyChanged(nameof(PendingVisState));
    }
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

// ================= 网速与低速分析模型（宿主 Models/Speed.cs 同源移植） =================

/// <summary>测速实时进度（约 400ms 一拍，UI 实时刷新）。</summary>
public sealed class SpeedProgress
{
    /// <summary>阶段描述（探测节点 / 正在测速：xxx）。</summary>
    public string Phase { get; init; } = "";
    /// <summary>瞬时速率（Mbps）。</summary>
    public double InstantMbps { get; init; }
    /// <summary>本次测速累计平均（Mbps）。</summary>
    public double AvgMbps { get; init; }
    /// <summary>已下载字节。</summary>
    public long BytesRead { get; init; }
    /// <summary>本端点已用时（ms）。</summary>
    public int ElapsedMs { get; init; }
    /// <summary>活动连接数。</summary>
    public int Connections { get; init; }
}

/// <summary>单个测速端点的结果。</summary>
public sealed class SpeedEndpointResult
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Success { get; set; }
    public double MbpsAvg { get; set; }
    public double MbpsPeak { get; set; }
    public long BytesTotal { get; set; }
    public int DurationMs { get; set; }
    public int Connections { get; set; }
    public string Error { get; set; } = "";

    public string SpeedText => Success ? $"{MbpsAvg:0.#} Mbps" : "不可用";
    public string DurationText => DurationMs > 0 ? $"{DurationMs / 1000.0:0.#} s" : "—";
    public string BytesText => FormatHelper.Bytes(BytesTotal);
}

/// <summary>一次下载吞吐实测的汇总。</summary>
public sealed class SpeedTestResult
{
    public bool Success { get; set; }
    /// <summary>代表带宽：多端点中最优平均（Mbps）。</summary>
    public double MbpsAvg { get; set; }
    /// <summary>峰值瞬时速率（Mbps）。</summary>
    public double MbpsPeak { get; set; }
    public long BytesTotal { get; set; }
    public int DurationMs { get; set; }
    /// <summary>代表带宽来源端点。</summary>
    public string EndpointLabel { get; set; } = "";
    /// <summary>链路协商速率（Mbps，对比实测效率用）。</summary>
    public double LinkMbps { get; set; }
    public List<SpeedEndpointResult> Endpoints { get; set; } = new();
    public string Error { get; set; } = "";

    /// <summary>实测占链路协商速率比例（0-1；共享/无线网络通常低于独占）。</summary>
    public double Efficiency => LinkMbps > 1 ? Math.Min(1.0, MbpsAvg / LinkMbps) : 0;

    public string SpeedText => Success ? $"{MbpsAvg:0.#} Mbps" : "未测得";
    public string PeakText => Success ? $"{MbpsPeak:0.#} Mbps" : "—";
    public string LinkText => LinkMbps >= 1000 ? $"{LinkMbps / 1000:0.#} Gbps" : LinkMbps > 0 ? $"{LinkMbps:0} Mbps" : "未知";
    public string EfficiencyText => LinkMbps > 1 ? $"{Efficiency * 100:0}%" : "—";

    /// <summary>体验档位（面向教师语义，不是实验室指标）。</summary>
    public string ExperienceBand
    {
        get
        {
            if (!Success) return "无法评估";
            return MbpsAvg switch
            {
                >= 90 => "优秀（4K 视频、大文件下载无压力）",
                >= 50 => "良好（1080P 网课与在线课堂流畅）",
                >= 20 => "一般（1080P 可看，多人共用可能缓冲）",
                >= 5 => "较差（网页与标清视频明显卡顿）",
                _ => "很差（网页图片加载都困难）"
            };
        }
    }
}

/// <summary>低速原因分析结论项：一条嫌疑原因 + 依据 + 处置措施。</summary>
public sealed class SlowCause
{
    public string Title { get; set; } = "";
    /// <summary>嫌疑等级：高 / 中 / 低。</summary>
    public string Level { get; set; } = "中";
    /// <summary>嫌疑权重（排序用，0-100）。</summary>
    public int Weight { get; set; }
    public List<string> Evidence { get; set; } = new();
    public List<string> Actions { get; set; } = new();

    public bool IsHigh => Level == "高";
    public bool IsMid => Level == "中";
    public bool IsLow => !IsHigh && !IsMid;
    public string EvidenceText => string.Join("；", Evidence);
    public string ActionText => string.Join("；", Actions);

    // Visibility 类型态属性：嫌疑档在行创建后不再变化（分析器一次性产出），
    // 普通属性即可满足经典 Binding 初始求值。
    public Visibility HighVisState => IsHigh ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MidVisState => IsMid ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LowVisState => IsLow ? Visibility.Visible : Visibility.Collapsed;
}

// ================= 网络拓扑图模型（宿主 Models/NetworkTopology.cs 同源移植） =================
// 拓扑快照由诊断结果自动构建（NetworkTopologyBuilder，纯函数无 IO）：
// 坐标在虚拟画布上由构建器计算，视图层只负责渲染（Canvas + Viewbox 缩放）。

/// <summary>节点/链路的健康档位（对齐 CheckStatus 语义，收敛为四档）。</summary>
public enum TopoHealth { Unknown, Ok, Warn, Fail }

/// <summary>节点角色（决定徽标文案与分支归属）。</summary>
public enum TopoRole { Local, Gateway, InnerHop, Exit, Dns, Portal, Internet }

/// <summary>各检查写入共享上下文的结构化键（前缀 topo.，构建器专用约定）。</summary>
public static class TopoKeys
{
    /// <summary>直连网关段实测（TopoSegmentStats，「默认网关连通」写入）。</summary>
    public const string Gateway = "topo.gw";
    /// <summary>tracert 探得的内网中间跳（List&lt;TopoHop&gt;，「分段延迟画像」写入）。</summary>
    public const string Hops = "topo.hops";
    /// <summary>公网出口段实测（TopoSegmentStats，「分段延迟画像」写入）。</summary>
    public const string Exit = "topo.exit";
    /// <summary>互联网目标段实测（TopoSegmentStats，「分段延迟画像」写入，目标禁 ICMP 时不写）。</summary>
    public const string Target = "topo.target";
    /// <summary>DNS 逐台探测（List&lt;TopoDnsProbe&gt;，「DNS 服务器逐台探测」写入）。</summary>
    public const string Dns = "topo.dns";
    /// <summary>外网 HTTP 探测（TopoSegmentStats，「外网 HTTP 连通」写入）。</summary>
    public const string Inet = "topo.inet";
    /// <summary>认证门户探测（TopoSegmentStats，「城域网认证门户检测」写入，仅检测到门户时）。</summary>
    public const string Portal = "topo.portal";
}

/// <summary>分段实测数据（Ping 与 HTTP 探测二选一：Ping 段看丢包/延迟，HTTP 段看 ElapsedMs/Note）。</summary>
public sealed record TopoSegmentStats(string Address, PingStats? Ping = null, bool Ok = false, long ElapsedMs = 0, string Note = "");

/// <summary>tracert 探得的内网中间跳（Role 已由「分段延迟画像」装配：汇聚中转/核心出口侧）。</summary>
public sealed record TopoHop(string Ip, PingStats Ping, string Role);

/// <summary>DNS 服务器逐台探测结果。</summary>
public sealed record TopoDnsProbe(string Server, bool Ok, long ElapsedMs);

/// <summary>拓扑节点（坐标为虚拟画布坐标，宽高由构建器按角色排布）。</summary>
public sealed class TopoNode
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// <summary>角色徽标短文案（本机/网关·接入段/汇聚中转/校园出口/DNS/认证门户/互联网）。</summary>
    public string Badge { get; init; } = "";
    public TopoRole Role { get; init; }
    public TopoHealth Health { get; init; }
    /// <summary>状态一句话（延迟/丢包/未检出原因），渲染进节点第二行。</summary>
    public string StatusText { get; init; } = "";
    /// <summary>ToolTip 展开的完整证据（可多行）。</summary>
    public string TooltipText { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
}

/// <summary>拓扑连线（带链路质量短标注）。</summary>
public sealed class TopoEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    /// <summary>短标注（如「2.1 ms」「无线 · 有线」「未认证」「在线」）。</summary>
    public string Label { get; init; } = "";
    public TopoHealth Health { get; init; }
    /// <summary>虚线：无线链路或对端未实测。</summary>
    public bool Dashed { get; init; }
}

/// <summary>一次诊断生成的完整拓扑（只读快照，Empty 表示无图可画）。</summary>
public sealed class NetworkTopology
{
    public List<TopoNode> Nodes { get; init; } = new();
    public List<TopoEdge> Edges { get; init; } = new();
    /// <summary>一句话结论（认证断点/链路断点/瓶颈/全链路正常）。</summary>
    public string SummaryText { get; init; } = "";
    /// <summary>生成来源（诊断模式 + 生成时间）。</summary>
    public string BuiltFrom { get; init; } = "";
    public double CanvasW { get; init; }
    public double CanvasH { get; init; }

    public static readonly NetworkTopology Empty = new() { CanvasW = 1000, CanvasH = 230 };
}
