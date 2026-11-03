using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Models;

// ================= 网络拓扑图模型 =================
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
