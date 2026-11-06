using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Gala;

// ================= 拓扑配置（gala-topology.json，插件数据目录） =================

/// <summary>拓扑总配置。真实网络资料到位后按注释字段编辑，面板内「重载拓扑配置」即时生效。</summary>
public sealed class GalaTopologyConfig
{
    [JsonPropertyName("settings")] public GalaSettings Settings { get; set; } = new();
    [JsonPropertyName("nodes")] public List<GalaNodeConfig> Nodes { get; set; } = new();
    [JsonPropertyName("links")] public List<GalaLinkConfig> Links { get; set; } = new();
    [JsonPropertyName("routes")] public List<GalaRouteConfig> Routes { get; set; } = new();

    /// <summary>内置占位拓扑（等真实资料替换）：两出口路由 × 两服务器 × 两客户端段的示例。</summary>
    public static GalaTopologyConfig CreateTemplate()
    {
        return new GalaTopologyConfig
        {
            Settings = new GalaSettings(),
            Nodes = new List<GalaNodeConfig>
            {
                new() { Id = "master", Type = "master", Label = "主控台", Ip = "10.10.0.1" },
                new() { Id = "r1", Type = "router", Label = "出口路由A", Ip = "10.10.0.254" },
                new() { Id = "r2", Type = "router", Label = "出口路由B", Ip = "10.11.0.254" },
                new() { Id = "s1", Type = "server", Label = "流媒体服务器", Ip = "10.20.0.10", ProbePort = 8000 },
                new() { Id = "s2", Type = "server", Label = "互动/投票服务器", Ip = "10.20.0.11", ProbePort = 8080 }
            },
            Links = new List<GalaLinkConfig>
            {
                new() { From = "r1", To = "s1", CapacityMbps = 1000 },
                new() { From = "r1", To = "s2", CapacityMbps = 1000 },
                new() { From = "r2", To = "s1", CapacityMbps = 1000 },
                new() { From = "r2", To = "s2", CapacityMbps = 1000 }
            },
            Routes = new List<GalaRouteConfig>
            {
                new() { SegmentPrefix = "10.10.", ViaRouter = "r1", Servers = new List<string> { "s1", "s2" } },
                new() { SegmentPrefix = "10.11.", ViaRouter = "r2", Servers = new List<string> { "s2", "s1" } }
            }
        };
    }

    /// <summary>规范化 + 基础合法性（缺 id 的节点剔除、type 归一小写、路由引用不存在的主机剔除）。</summary>
    public static void Normalize(GalaTopologyConfig cfg)
    {
        cfg.Settings ??= new GalaSettings();
        cfg.Settings.Normalize();
        cfg.Nodes ??= new List<GalaNodeConfig>();
        cfg.Links ??= new List<GalaLinkConfig>();
        cfg.Routes ??= new List<GalaRouteConfig>();
        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in cfg.Nodes)
        {
            n.Id = (n.Id ?? "").Trim();
            n.Type = (n.Type ?? "server").Trim().ToLowerInvariant();
            n.Label = string.IsNullOrWhiteSpace(n.Label) ? n.Id : n.Label.Trim();
            n.Ip = (n.Ip ?? "").Trim();
            if (n.Id.Length > 0) nodeIds.Add(n.Id);
        }
        cfg.Nodes.RemoveAll(n => n.Id.Length == 0);
        cfg.Links.RemoveAll(l => !nodeIds.Contains((l.From ?? "").Trim()) || !nodeIds.Contains((l.To ?? "").Trim()));
        foreach (var l in cfg.Links) { l.From = l.From.Trim(); l.To = l.To.Trim(); if (l.CapacityMbps <= 0) l.CapacityMbps = 1000; }
        cfg.Routes.RemoveAll(r => !nodeIds.Contains((r.ViaRouter ?? "").Trim()));
        foreach (var r in cfg.Routes)
        {
            r.SegmentPrefix = (r.SegmentPrefix ?? "").Trim();
            r.ViaRouter = r.ViaRouter.Trim();
            r.Servers ??= new List<string>();
            r.Servers = r.Servers.Where(s => nodeIds.Contains((s ?? "").Trim())).Select(s => s.Trim()).ToList();
        }
    }

    public GalaNodeConfig? FindNode(string id)
    {
        return Nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 配置健康检查（Normalize 之后调用）：返回人话告警列表（空列表 = 未发现问题）。
    /// 只报不改——剔错仍由 Normalize 负责，这里负责让填错拓扑的人第一眼看到错在哪；
    /// 主控插件在加载/重载时写日志，独立主控台启动时打印。
    /// </summary>
    public static List<string> Validate(GalaTopologyConfig cfg)
    {
        var warns = new List<string>();

        // 节点层：重复 id / 重复 IP / 非法 IP / 缺 IP / probePort 越界
        foreach (var id in cfg.Nodes.GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
            warns.Add($"节点 id 重复：{id}（探测与路由匹配只会命中第一个）");
        foreach (var ip in cfg.Nodes.Where(n => n.Ip.Length > 0)
                     .GroupBy(n => n.Ip, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1).Select(g => g.Key))
            warns.Add($"多个节点共用 IP：{ip}");
        foreach (var n in cfg.Nodes)
        {
            if (n.Ip.Length > 0 && !IPAddress.TryParse(n.Ip, out _))
                warns.Add($"节点 {n.Id} 的 IP 不是合法地址：{n.Ip}");
            if ((n.Type == "router" || n.Type == "server") && n.Ip.Length == 0)
                warns.Add($"节点 {n.Id}（{n.Type}）未填 IP，不参与探测与压力评估");
            if (n.ProbePort is < 0 or > 65535)
                warns.Add($"节点 {n.Id} 的 probePort 越界（0~65535）：{n.ProbePort}");
        }

        // 路由层：空服务器规则 / viaRouter 类型错误 / 整体无规则
        foreach (var r in cfg.Routes)
        {
            if (r.Servers.Count == 0)
                warns.Add($"路由规则 {r.SegmentPrefix} → {r.ViaRouter} 没有可用服务器（该段终端不会得到分配）");
            var via = cfg.FindNode(r.ViaRouter);
            if (via != null && via.Type != "router")
                warns.Add($"路由规则 {r.SegmentPrefix} 的 viaRouter={r.ViaRouter} 不是 router 类型（当前 {via.Type}）");
        }
        if (cfg.Routes.Count == 0)
            warns.Add("没有任何调度规则（routes 为空）：客户端将仅监控不调度");

        // 准入与配对
        if (cfg.Settings.AllowedPrefixes.Count == 0)
            warns.Add("allowedPrefixes 为空：除本机回环外将拒绝一切接入");
        if (cfg.Settings.PairingCode.Length == 0)
            warns.Add("未设配对码：内网任何终端都可接入，现场建议设置");

        return warns;
    }

    /// <summary>客户端 IP → 候选路径（段前缀最长匹配优先，八点界匹配防 10.1 误吞 10.10.x）。</summary>
    public GalaRouteConfig? MatchRoute(string clientIp)
    {
        GalaRouteConfig? best = null;
        foreach (var r in Routes)
        {
            if (r.SegmentPrefix.Length == 0 || !GalaNet.MatchesPrefix(clientIp, r.SegmentPrefix)) continue;
            if (best == null || r.SegmentPrefix.Length > best.SegmentPrefix.Length) best = r;
        }
        return best;
    }
}

/// <summary>
/// IP 段前缀匹配（八点界）：准入白名单与调度路由共用。
/// 裸 StartsWith 有越界风险——前缀 "192.168.1" 会误匹配 "192.168.11.5"、"10.1" 会误吞
/// "10.10.x.x"；本方法要求前缀以点结尾，或 IP 在前缀长度处恰为点/恰好结束（同段语义）。
/// </summary>
public static class GalaNet
{
    public static bool MatchesPrefix(string ip, string prefix)
    {
        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(prefix)) return false;
        if (!ip.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return prefix[^1] == '.' || ip.Length == prefix.Length || ip[prefix.Length] == '.';
    }
}

/// <summary>主控端运行参数（内网准入 / 调度手感都在这里调）。</summary>
public sealed class GalaSettings
{
    /// <summary>主控监听端口（TCP）。换端口后客户端也要同步改。</summary>
    [JsonPropertyName("listenPort")] public int ListenPort { get; set; } = 47816;

    /// <summary>探测/调度周期（秒）。3~5 秒适合晚会现场。</summary>
    [JsonPropertyName("tickSeconds")] public int TickSeconds { get; set; } = 3;

    /// <summary>会话准入白名单：远端 IP 必须命中其中之一（八点界匹配，"192.168.1" 只认 192.168.1.x 段；限城域网内网，勿放公网段）。</summary>
    [JsonPropertyName("allowedPrefixes")] public List<string> AllowedPrefixes { get; set; } = new() { "10.", "172.16.", "192.168.", "100.64." };

    /// <summary>配对码（可空=不校验）。现场防误连建议设置，客户端面板填同一串即可。</summary>
    [JsonPropertyName("pairingCode")] public string PairingCode { get; set; } = "";

    /// <summary>主控端随插件加载自动启动监听（无人值守：控制机重启工具箱即恢复监听；false = 手动启动）。</summary>
    [JsonPropertyName("autoStartMaster")] public bool AutoStartMaster { get; set; } = true;

    /// <summary>切换阈值：当前路径比最优差超过该分数才切换（防抖动）。</summary>
    [JsonPropertyName("switchThreshold")] public double SwitchThreshold { get; set; } = 12;

    /// <summary>同一客户端两次切换的最小间隔（秒）。</summary>
    [JsonPropertyName("switchCooldownSeconds")] public int SwitchCooldownSeconds { get; set; } = 30;

    /// <summary>单节点并发满载客户端数（负载分 = 在线服务数 / 该值 × 100）。</summary>
    [JsonPropertyName("clientsPerNodeFull")] public int ClientsPerNodeFull { get; set; } = 10;

    /// <summary>客户端心跳空闲超时（秒）：超过该时长未收到任何帧按离线断开（终端拔电等半开连接不再虚占并发压力分）。须大于客户端上报周期数倍。</summary>
    [JsonPropertyName("clientIdleTimeoutSeconds")] public int ClientIdleTimeoutSeconds { get; set; } = 20;

    /// <summary>压力权重：响应 / 丢包 / 并发 / 带宽（建议和为 1）。</summary>
    [JsonPropertyName("weightRtt")] public double WeightRtt { get; set; } = 0.35;
    [JsonPropertyName("weightLoss")] public double WeightLoss { get; set; } = 0.25;
    [JsonPropertyName("weightLoad")] public double WeightLoad { get; set; } = 0.20;
    [JsonPropertyName("weightBandwidth")] public double WeightBandwidth { get; set; } = 0.20;

    public void Normalize()
    {
        if (ListenPort is < 1 or > 65535) ListenPort = 47816;
        if (TickSeconds < 1) TickSeconds = 3;
        if (SwitchThreshold <= 0) SwitchThreshold = 12;
        if (SwitchCooldownSeconds < 0) SwitchCooldownSeconds = 30;
        if (ClientsPerNodeFull < 1) ClientsPerNodeFull = 10;
        if (ClientIdleTimeoutSeconds < 10) ClientIdleTimeoutSeconds = 20;
        if (WeightRtt < 0) WeightRtt = 0;
        if (WeightLoss < 0) WeightLoss = 0;
        if (WeightLoad < 0) WeightLoad = 0;
        if (WeightBandwidth < 0) WeightBandwidth = 0;
        var sum = WeightRtt + WeightLoss + WeightLoad + WeightBandwidth;
        if (sum <= 0.001) { WeightRtt = 0.35; WeightLoss = 0.25; WeightLoad = 0.20; WeightBandwidth = 0.20; }
        AllowedPrefixes ??= new List<string> { "10.", "172.16.", "192.168.", "100.64." };
        AllowedPrefixes = AllowedPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
    }
}

/// <summary>拓扑节点（master/router/server/switch；客户端不进配置，运行时注册）。</summary>
public sealed class GalaNodeConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>类型：master / router / server / switch（switch 仅展示）。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "server";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    /// <summary>服务器 TCP 探测端口（0 = 只用 ICMP Ping 探测）。</summary>
    [JsonPropertyName("probePort")] public int ProbePort { get; set; }
    /// <summary>可选手工布局（0~1 归一化坐标）；缺省自动分层布局。</summary>
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
}

/// <summary>链路（带宽容量用于带宽压力分）。</summary>
public sealed class GalaLinkConfig
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("capacityMbps")] public double CapacityMbps { get; set; } = 1000;
}

/// <summary>调度规则：某 IP 段的客户端经哪台路由、可落到哪些服务器。</summary>
public sealed class GalaRouteConfig
{
    [JsonPropertyName("segmentPrefix")] public string SegmentPrefix { get; set; } = "10.10.";
    [JsonPropertyName("viaRouter")] public string ViaRouter { get; set; } = "";
    [JsonPropertyName("servers")] public List<string> Servers { get; set; } = new();
}

// ================= 协议 DTO（主控 ↔ 客户端，长度前缀 + JSON） =================

public sealed class HelloMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "hello";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    /// <summary>客户端全部 IPv4（逗号分隔，主控取与自身同段的那个做准入与段匹配）。</summary>
    [JsonPropertyName("ips")] public string Ips { get; set; } = "";
    [JsonPropertyName("pairing")] public string Pairing { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "gala-client";
}

public sealed class WelcomeMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "welcome";
    [JsonPropertyName("routerId")] public string RouterId { get; set; } = "";
    [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
    /// <summary>当前分配服务器的 IP（客户端用于直连探测，主控按拓扑表查）。</summary>
    [JsonPropertyName("serverIp")] public string ServerIp { get; set; } = "";
    /// <summary>0=高 1=中 2=低。</summary>
    [JsonPropertyName("priority")] public int Priority { get; set; } = 1;
    [JsonPropertyName("throttleKbps")] public int ThrottleKbps { get; set; }
    [JsonPropertyName("tickSeconds")] public int TickSeconds { get; set; } = 3;
}

public sealed class MetricsMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "metrics";
    /// <summary>到主控 RTT（毫秒，-1 = 未知）。</summary>
    [JsonPropertyName("rttMasterMs")] public double RttMasterMs { get; set; } = -1;
    /// <summary>到当前分配服务器 RTT（毫秒，-1 = 未分配/探测失败）。</summary>
    [JsonPropertyName("rttServerMs")] public double RttServerMs { get; set; } = -1;
    /// <summary>到主控丢包率（%，0~100）。</summary>
    [JsonPropertyName("lossPct")] public double LossPct { get; set; }
    [JsonPropertyName("rxKbps")] public double RxKbps { get; set; }
    [JsonPropertyName("txKbps")] public double TxKbps { get; set; }
    /// <summary>本工具箱进程 CPU（%，供主控参考该终端负载）。</summary>
    [JsonPropertyName("appCpu")] public double AppCpu { get; set; }
}

public sealed class CommandMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "command";
    [JsonPropertyName("cmdId")] public string CmdId { get; set; } = "";
    /// <summary>switch / priority / throttle / probe。</summary>
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("routerId")] public string RouterId { get; set; } = "";
    [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
    /// <summary>目标服务器 IP（switch 指令附带，客户端据此探测与记录）。</summary>
    [JsonPropertyName("serverIp")] public string ServerIp { get; set; } = "";
    [JsonPropertyName("value")] public int Value { get; set; }
}

public sealed class AckMsg
{
    [JsonPropertyName("type")] public string Type { get; set; } = "ack";
    [JsonPropertyName("cmdId")] public string CmdId { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

// ================= 帧编解码（4 字节小端长度 + UTF-8 JSON） =================

public static class GalaFrame
{
    public const int MaxFrameBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] Encode<T>(T msg)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        if (body.Length > MaxFrameBytes) throw new InvalidDataException("消息超出帧长度上限");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        return frame;
    }

    public static T? Decode<T>(byte[] body)
    {
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>读一帧。返回 null = 对端正常关闭；坏帧（长度非法/半包）抛 InvalidDataException。</summary>
    public static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > MaxFrameBytes) throw new InvalidDataException($"帧长度非法：{len}");
        var body = new byte[len];
        if (!await ReadExactAsync(stream, body, ct).ConfigureAwait(false)) return null;
        return body;
    }

    public static async Task WriteFrameAsync(NetworkStream stream, byte[] frame, CancellationToken ct)
    {
        await stream.WriteAsync(frame.AsMemory(0, frame.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(off, buffer.Length - off), ct).ConfigureAwait(false);
            if (n <= 0) return false; // 对端关闭（含半关闭，统一按关闭处理）
            off += n;
        }
        return true;
    }

    /// <summary>从字符串列表里取与参考 IP 同段优先的地址（准入与段匹配用）。</summary>
    public static string PickBestIp(string csvIps, IReadOnlyCollection<string> referenceIps)
    {
        var ips = (csvIps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ips.Length == 0) return "";
        foreach (var ip in ips)
        {
            if (!IPAddress.TryParse(ip, out var a)) continue;
            foreach (var r in referenceIps)
            {
                if (!IPAddress.TryParse(r, out var b)) continue;
                if (a.AddressFamily == b.AddressFamily && a.GetAddressBytes()[0] == b.GetAddressBytes()[0]) return ip;
            }
        }
        return ips[0];
    }
}
