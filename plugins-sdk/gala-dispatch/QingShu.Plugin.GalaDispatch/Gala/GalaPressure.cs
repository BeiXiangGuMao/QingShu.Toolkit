using System;
using System.Collections.Generic;
using System.Linq;

namespace QingShu.Plugin.GalaDispatch.Gala;

// ================= 压力模型（纯函数，主控循环与 UI 共用） =================

/// <summary>节点压力输入快照。</summary>
public sealed class PressureInput
{
    /// <summary>探测 RTT EMA（毫秒，-1 = 探测失败）。</summary>
    public double RttMs { get; init; } = -1;
    /// <summary>探测窗口丢包率（%）。</summary>
    public double LossPct { get; init; }
    /// <summary>该节点当前服务的在线客户端数。</summary>
    public int ServedClients { get; init; }
    /// <summary>该节点所服务客户端上报的上/下行之和（Mbps）。</summary>
    public double ServedMbps { get; init; }
    /// <summary>链路容量（Mbps，&lt;=0 视为未知，带宽分量按 0 计）。</summary>
    public double CapacityMbps { get; init; }
}

/// <summary>节点压力评分器：响应 × 丢包 × 并发 × 带宽 加权综合，0（空闲）~100（打满）。</summary>
public static class GalaPressure
{
    /// <summary>RTT 达到该值记满分。</summary>
    public const double RttFullMs = 40;

    public static double Score(PressureInput input, GalaSettings s)
    {
        // 响应分：探测失败按打满计；EMA RTT 越高越满
        double rttScore = input.RttMs < 0 ? 100 : Math.Min(100, input.RttMs / RttFullMs * 100);
        // 丢包分：直接用窗口丢包率
        double lossScore = Math.Clamp(input.LossPct, 0, 100);
        // 并发分：在线服务数 / 满载数
        double loadScore = Math.Min(100, input.ServedClients * 100.0 / Math.Max(1, s.ClientsPerNodeFull));
        // 带宽分：服务流量 / 链路容量（未知容量按 0）
        double bwScore = input.CapacityMbps > 0
            ? Math.Min(100, input.ServedMbps / input.CapacityMbps * 100)
            : 0;

        double sum = s.WeightRtt + s.WeightLoss + s.WeightLoad + s.WeightBandwidth;
        if (sum <= 0.001) return rttScore * 0.5 + lossScore * 0.5;
        double score = (rttScore * s.WeightRtt + lossScore * s.WeightLoss
                        + loadScore * s.WeightLoad + bwScore * s.WeightBandwidth) / sum;
        return Math.Clamp(score, 0, 100);
    }

    /// <summary>路径分 = 路由压力 × 服务器压力 各取一半（两者有一个打满即显著抬分）。</summary>
    public static double PathScore(double routerPressure, double serverPressure)
    {
        return routerPressure * 0.5 + serverPressure * 0.5;
    }
}

/// <summary>一次调度决策（可解释：UI 事件日志直接展示）。</summary>
public sealed record DispatchDecision(
    string ClientId,
    string NewRouterId,
    string NewServerId,
    double CurrentScore,
    double BestScore,
    string Reason);

/// <summary>调度器：滞回 + 冷却 + 优先级，防止现场线路来回抖动。</summary>
public static class GalaDispatcher
{
    public static readonly string[] PriorityNames = { "高", "中", "低" };

    /// <summary>
    /// 计算是否需要切换。candidate = (路由id, 服务器id)；返回 null = 维持现状。
    /// 高优先级客户端：阈值减半、冷却减半（压力起来时先保重要终端）。
    /// </summary>
    public static DispatchDecision? Decide(
        string clientId, string clientIp,
        string? curRouterId, string? curServerId, int priority,
        IReadOnlyList<GalaRouteConfig> routes,
        IReadOnlyDictionary<string, double> nodeScores,
        GalaSettings s,
        TimeSpan cooldownRemaining)
    {
        if (routes.Count == 0 || nodeScores.Count == 0) return null;

        double threshold = priority <= 0 ? s.SwitchThreshold / 2 : s.SwitchThreshold;
        TimeSpan cooldown = TimeSpan.FromSeconds(
            priority <= 0 ? Math.Max(1, s.SwitchCooldownSeconds / 2) : s.SwitchCooldownSeconds);
        if (cooldownRemaining < TimeSpan.Zero) cooldownRemaining = TimeSpan.Zero;
        // 冷却判定：从未切换（调用方传 Zero）视为已冷却；剩余冷却小于门槛（高优=半程）即可再切

        // 当前路径分（未分配/节点缺失 → 视为极差，触发首次分配）
        double curScore = (curRouterId != null && nodeScores.TryGetValue(curRouterId, out var rp)
                           && curServerId != null && nodeScores.TryGetValue(curServerId, out var sp))
            ? GalaPressure.PathScore(rp, sp)
            : double.MaxValue;

        // 候选 = 按 IP 段匹配的规则展开为 (router, server)；段不匹配的客户端不调度
        var candidates = new List<(string RouterId, string ServerId, double Score)>();
        foreach (var r in routes)
        {
            if (r.SegmentPrefix.Length == 0 || !clientIp.StartsWith(r.SegmentPrefix, StringComparison.Ordinal)) continue;
            if (!nodeScores.TryGetValue(r.ViaRouter, out var rScore)) continue;
            foreach (var sid in r.Servers)
            {
                if (!nodeScores.TryGetValue(sid, out var sScore)) continue;
                candidates.Add((r.ViaRouter, sid, GalaPressure.PathScore(rScore, sScore)));
            }
        }
        if (candidates.Count == 0) return null;

        var best = candidates.OrderBy(c => c.Score).First();

        // 高优：阈值过半且冷却过半即可切；普通：全阈值全冷却
        bool cooled = priority <= 0
            ? cooldownRemaining <= cooldown / 2
            : cooldownRemaining <= TimeSpan.Zero;
        if (!cooled) return null;

        if (curScore - best.Score < threshold) return null;
        if (best.RouterId == curRouterId && best.ServerId == curServerId) return null;

        return new DispatchDecision(clientId, best.RouterId, best.ServerId, curScore, best.Score,
            $"路径 {curScore:F0} → {best.Score:F0}（阈值 {threshold:F0}），选 {best.RouterId}/{best.ServerId}");
    }
}
