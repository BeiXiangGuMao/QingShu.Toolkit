using System;
using System.Collections.Generic;
using System.Linq;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 网络拓扑构建器：把一次诊断的共享上下文（结构化实测）+ 检查项状态，收敛为
/// 「本机 → 网关 →（内网跳 ≤3）→ 校园出口 → 互联网（+DNS/认证门户分支）」的拓扑快照。
/// 纯函数、无 IO、无副作用：布局坐标在此计算，视图层只渲染。
/// 数据来源优先级：结构化实测（TopoKeys.*）&gt; 对应检查项状态 &gt; Unknown（灰色未检出）。
/// </summary>
public static class NetworkTopologyBuilder
{
    // 分段健康阈值：与「分段延迟画像」判定口径一致（内网跳/出口段）
    private const double HopFailLossPct = 20;    // 内网跳丢包超此 → 异常
    private const double HopWarnAvgMs = 30;      // 内网跳平均延迟超此 → 警告
    private const double HopWarnJitterMs = 25;   // 内网跳抖动超此 → 警告
    private const double ExitWarnLossPct = 10;   // 出口段丢包超此 → 警告
    private const double ExitWarnDeltaMs = 80;   // 出口段相对接入段增量超此 → 警告
    private const int MaxInnerHops = 3;          // 拓扑最多展示的内网跳数（超出折叠）
    private const string NcsiProbe = "http://www.msftconnecttest.com/connecttest.txt";

    public static NetworkTopology Build(CheckContext? ctx, IReadOnlyList<CheckItem> results,
        string modeText = "", bool cancelled = false)
    {
        var p = ctx?.Primary;
        if (p == null) return Degraded(modeText);

        var items = new Dictionary<string, CheckStatus>();
        foreach (var it in results) items[it.Id] = it.Status;
        TopoHealth Stat(string id) => items.TryGetValue(id, out var s) ? Map(s) : TopoHealth.Unknown;

        // ---------- 1) 本机 ----------
        var localHealth = Worse(Stat("ip.config"), Stat("physical.link"));
        var localSub = string.IsNullOrEmpty(p.IpV4) ? "无 IPv4" : p.IpV4!;
        localSub += p.IsWireless
            ? (ctx?.Wifi != null ? $" · Wi-Fi {ctx.Wifi.SignalPct}%" : " · Wi-Fi")
            : " · 有线";
        var localTip = new List<string>
        {
            $"主机：{Environment.MachineName}",
            $"网卡：{p.Name}（{p.Description}）",
            $"链路速率：{(p.SpeedBps > 0 ? $"{p.SpeedBps / 1_000_000} Mbps" : "未知")}"
        };
        if (ctx?.Wifi != null) localTip.Add($"Wi-Fi：{ctx.Wifi.Ssid}（信号 {ctx.Wifi.SignalPct}%）");

        // ---------- 2) 网关（接入段） ----------
        var gwHealth = First(Stat("gateway.reachable"), Stat("ip.config"));
        var gwStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Gateway);
        var gwStatus = DescribePing(gwStats?.Ping) ?? "未实测";
        var gwIp = string.IsNullOrEmpty(p.Gateway) ? "未配置" : p.Gateway!;

        // ---------- 3) 内网中间跳（tracert 实测，仅测速模式产出） ----------
        var hops = (ctx?.GetShared<List<TopoHop>>(TopoKeys.Hops) ?? new List<TopoHop>())
            .Take(MaxInnerHops).ToList();

        // ---------- 4) 校园出口（公网段） ----------
        var exitStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Exit);
        var gwPing = gwStats?.Ping;
        TopoHealth exitHealth;
        string exitStatus;
        var exitTip = new List<string>();
        if (exitStats?.Ping is { } ep)
        {
            exitStatus = $"公网段 {ep.AvgMs:0.#} ms · 丢包 {ep.LossPct:0.#}%";
            if (ep.AllLost)
            {
                exitHealth = TopoHealth.Fail;
                exitStatus = "公网段完全无响应";
            }
            else if (ep.LossPct > ExitWarnLossPct ||
                     (gwPing is { Received: > 0 } gp && ep.AvgMs - gp.AvgMs > ExitWarnDeltaMs))
            {
                exitHealth = TopoHealth.Warn;
                exitTip.Add("出口段拥塞特征：内网快、出公网明显变慢（裸光纤/城域网高峰拥塞）");
            }
            else exitHealth = TopoHealth.Ok;
        }
        else
        {
            exitHealth = First(Stat("internet.icmp"), Stat("internet.http"));
            exitStatus = exitHealth == TopoHealth.Unknown ? "该模式未实测出口段" : "按外网 ICMP/HTTP 判定";
        }

        // ---------- 5) 互联网 ----------
        var inetHealth = First(Stat("internet.http"), Stat("internet.https"), Stat("internet.icmp"));
        var inetStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Inet);
        var target = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Target);
        var speed = ctx?.SpeedResult;
        var inetParts = new List<string>();
        if (inetStats != null)
            inetParts.Add(inetStats.Ok ? $"HTTP 通（{inetStats.ElapsedMs} ms）"
                : string.IsNullOrEmpty(inetStats.Note) ? "HTTP 不通" : inetStats.Note);
        if (target?.Ping is { Received: > 0 } tp) inetParts.Add($"到站点 {tp.AvgMs:0.#} ms");
        if (speed is { Success: true, MbpsAvg: > 0 }) inetParts.Add($"实测 {speed.MbpsAvg:0.#} Mbps");
        var inetStatus = inetParts.Count > 0 ? string.Join(" · ", inetParts.Take(2)) : "未实测";

        // ---------- 6) DNS 分支数据 ----------
        var dnsProbes = ctx?.GetShared<List<TopoDnsProbe>>(TopoKeys.Dns);

        // ---------- 7) 认证门户分支数据 ----------
        var portalDetected = ctx is not null && ctx.SharedBool("portal.detected");
        var portalStats = ctx?.GetShared<TopoSegmentStats>(TopoKeys.Portal);
        var detectHealth = Stat("portal.detect");
        var expiryHealth = Stat("portal.expiry");
        var showPortal = portalDetected || detectHealth == TopoHealth.Fail || expiryHealth == TopoHealth.Fail;

        // ---------- 布局：链式坐标 ----------
        double x = 14;
        const double yChain = 46, hChain = 84, hBranch = 84, gap = 70, yDns = 186, yPortal = 306;

        TopoNode Node(double px, double py, double w, TopoRole role, string key, string badge,
            string title, string sub, TopoHealth health, string status, string tip, double h = hChain)
            => new()
            {
                Key = key, Title = title, Subtitle = sub, Badge = badge, Role = role,
                Health = health, StatusText = status, TooltipText = tip,
                X = px, Y = py, W = w, H = h
            };

        var local = Node(x, yChain, 168, TopoRole.Local, "local", "本机",
            Trim(Environment.MachineName, 14), localSub, localHealth,
            localHealth == TopoHealth.Unknown ? "未检出" : HealthText(localHealth),
            string.Join("\n", localTip));
        x += 168 + gap;
        var gw = Node(x, yChain, 156, TopoRole.Gateway, "gw", "网关 · 接入段",
            gwIp, "班级中转/楼宇软路由", gwHealth, gwStatus,
            $"网关地址：{gwIp}\n{gwStatus}");
        x += 156 + gap;

        var hopPings = new Dictionary<string, PingStats>(); // 节点键 → 该跳实测（连线标注用）
        var chain = new List<TopoNode> { local, gw };
        foreach (var h in hops)
        {
            var key = $"hop{chain.Count - 1}";
            var hopHealth = HopHealth(h.Ping);
            var hopStatus = DescribePing(h.Ping) ?? "未实测";
            chain.Add(Node(x, yChain, 138, TopoRole.InnerHop, key,
                h.Role.Contains("科技楼", StringComparison.Ordinal) ? "核心出口侧" : "汇聚中转",
                $"内网跳 {chain.Count - 1}", h.Ip, hopHealth, hopStatus, $"{h.Role}\n{hopStatus}"));
            hopPings[key] = h.Ping;
            x += 138 + gap;
        }

        var exit = Node(x, yChain, 158, TopoRole.Exit, "exit", "校园出口",
            "裸光纤 → 城域网", "出口段（全校共享）", exitHealth, exitStatus,
            string.Join("\n", exitTip));
        x += 158 + gap;
        var inet = Node(x, yChain, 148, TopoRole.Internet, "inet", "互联网",
            "互联网", "外网连通与带宽", inetHealth, inetStatus,
            $"HTTP 探测：{inetStats?.Address ?? NcsiProbe}\n{inetStatus}");
        x += 148;
        chain.Add(exit);
        chain.Add(inet);
        var canvasW = x + 14;

        // ---------- DNS 分支节点 ----------
        var dnsNodes = new List<TopoNode>();
        if (dnsProbes is { Count: > 0 })
        {
            var exitCx = exit.X + exit.W / 2;
            var dx = Math.Max(14, exitCx - 304);
            for (int i = 0; i < Math.Min(2, dnsProbes.Count); i++)
            {
                var d = dnsProbes[i];
                dnsNodes.Add(new TopoNode
                {
                    Key = $"dns{i}",
                    Title = i == 0 ? "DNS 主" : "DNS 备",
                    Subtitle = d.Server,
                    Badge = "DNS 服务器",
                    Role = TopoRole.Dns,
                    Health = d.Ok ? TopoHealth.Ok : TopoHealth.Fail,
                    StatusText = d.Ok ? $"解析 {d.ElapsedMs} ms" : "无响应",
                    TooltipText = $"{d.Server}\n逐台探测：{(d.Ok ? $"解析正常（{d.ElapsedMs} ms）" : "无响应")}",
                    X = dx + i * 164, Y = yDns, W = 148, H = hBranch
                });
            }
        }
        else
        {
            var dnsHealth = First(Stat("dns.resolve"), Stat("dns.servers"), Stat("dns.config"));
            if (dnsHealth != TopoHealth.Unknown || p.DnsServers.Count > 0)
                dnsNodes.Add(new TopoNode
                {
                    Key = "dns0",
                    Title = "DNS 解析",
                    Subtitle = p.DnsServers.Count > 0 ? p.DnsServers[0] : "未配置",
                    Badge = "DNS 服务器",
                    Role = TopoRole.Dns,
                    Health = dnsHealth,
                    StatusText = dnsHealth == TopoHealth.Unknown ? "未检出" : HealthText(dnsHealth),
                    TooltipText = $"配置的 DNS：{string.Join(", ", p.DnsServers)}",
                    X = Math.Max(14, exit.X + exit.W / 2 - 74), Y = yDns, W = 148, H = hBranch
                });
        }

        // ---------- 认证门户分支节点 ----------
        TopoNode? portal = null;
        if (showPortal)
        {
            var portalHealth = expiryHealth == TopoHealth.Ok ? TopoHealth.Ok : First(detectHealth, expiryHealth);
            var portalStatus = portalHealth switch
            {
                TopoHealth.Ok => "在线（外网正常）",
                TopoHealth.Fail when expiryHealth == TopoHealth.Fail => "状态接口显示离线/过期",
                TopoHealth.Fail => "未认证/已过期",
                TopoHealth.Warn => "门户可达性异常",
                _ => "状态未知"
            };
            var portalTip = new List<string>();
            if (!string.IsNullOrEmpty(portalStats?.Address)) portalTip.Add(portalStats!.Address);
            if (!string.IsNullOrEmpty(portalStats?.Note)) portalTip.Add(portalStats!.Note);
            portal = new TopoNode
            {
                Key = "portal",
                Title = HostOf(portalStats?.Address),
                Subtitle = "城域网认证",
                Badge = "认证门户",
                Role = TopoRole.Portal,
                Health = portalHealth,
                StatusText = portalStatus,
                TooltipText = string.Join("\n", portalTip),
                X = Math.Max(14, exit.X + exit.W / 2 - 84),
                Y = dnsNodes.Count > 0 ? yPortal : yDns,
                W = 168, H = hBranch
            };
        }

        var nodes = chain.Concat(dnsNodes).ToList();
        if (portal != null) nodes.Add(portal);

        // ---------- 连线 ----------
        var byKey = nodes.ToDictionary(n => n.Key, n => n);
        var edges = new List<TopoEdge>
        {
            // 本机 → 网关：链路类型 + 接入段实测
            new()
            {
                From = "local", To = "gw",
                Label = LinkLabel(p.IsWireless, gwPing),
                Health = Worse(local.Health, gw.Health), Dashed = p.IsWireless
            }
        };
        // 网关 → 内网跳 →…→ 校园出口（逐跳实测标注）
        string prevKey = "gw";
        foreach (var n in chain.Skip(2).Where(n => n.Key != "inet"))
        {
            edges.Add(new TopoEdge
            {
                From = prevKey, To = n.Key,
                Label = n.Key == "exit"
                    ? (exitStats?.Ping is { Received: > 0 } xp ? $"{xp.AvgMs:0.#} ms" : "")
                    : DescribePing(hopPings.GetValueOrDefault(n.Key)) ?? "",
                Health = Worse(byKey[prevKey].Health, n.Health),
                Dashed = n.Health == TopoHealth.Unknown
            });
            prevKey = n.Key;
        }
        // 校园出口 → 互联网（测速带宽优先，其次 HTTP 耗时）
        var inetLabel = speed is { Success: true, MbpsAvg: > 0 } ? $"{speed.MbpsAvg:0.#} Mbps"
            : inetStats is { Ok: true } ? $"{inetStats.ElapsedMs} ms" : "";
        edges.Add(new TopoEdge
        {
            From = "exit", To = "inet", Label = inetLabel,
            Health = Worse(exit.Health, inet.Health), Dashed = inet.Health == TopoHealth.Unknown
        });
        // 校园出口 → DNS（逐台探测标注耗时；无探测数据的快速模式按结论给短标注）
        foreach (var d in dnsNodes)
        {
            var dnsLabel = dnsProbes != null
                ? (d.Health == TopoHealth.Ok ? $"{DnsElapsed(dnsProbes, d.Key)} ms" : "无响应")
                : d.Health switch
                {
                    TopoHealth.Fail => "解析失败",
                    TopoHealth.Warn => "偏慢",
                    _ => ""
                };
            edges.Add(new TopoEdge
            {
                From = "exit", To = d.Key,
                Label = dnsLabel,
                Health = Worse(exit.Health, d.Health),
                Dashed = d.Health == TopoHealth.Unknown
            });
        }
        // 校园出口 → 认证门户（被拦截点）
        if (portal != null)
            edges.Add(new TopoEdge
            {
                From = "exit", To = "portal",
                Label = portal.Health == TopoHealth.Ok ? "在线" : "未认证",
                Health = Worse(exit.Health, portal.Health),
                Dashed = portal.Health == TopoHealth.Unknown
            });

        var canvasH = portal != null && dnsNodes.Count > 0 ? yPortal + hBranch + 16
            : dnsNodes.Count > 0 || portal != null ? yDns + hBranch + 16
            : yChain + hChain + 16;

        return new NetworkTopology
        {
            Nodes = nodes,
            Edges = edges,
            CanvasW = canvasW,
            CanvasH = canvasH,
            SummaryText = Summarize(chain, portal, cancelled),
            BuiltFrom = BuildFrom(modeText)
        };
    }

    // ---------- 降级拓扑：无适配器 ----------
    private static NetworkTopology Degraded(string modeText)
    {
        var node = new TopoNode
        {
            Key = "local",
            Title = Trim(Environment.MachineName, 14),
            Subtitle = "未找到可用网络适配器",
            Badge = "本机",
            Role = TopoRole.Local,
            Health = TopoHealth.Fail,
            StatusText = "无适配器",
            TooltipText = "NetworkInterface 未返回可用的非回环适配器：请检查网卡硬件、驱动或在设备管理器中启用网卡。",
            X = 14, Y = 46, W = 168, H = 84
        };
        return new NetworkTopology
        {
            Nodes = { node },
            Edges = new List<TopoEdge>(),
            CanvasW = 196,
            CanvasH = 146,
            SummaryText = "未生成完整拓扑：未找到可用网络适配器（请先检查物理连接与网卡驱动）。",
            BuiltFrom = BuildFrom(modeText)
        };
    }

    // ---------- 摘要 ----------
    private static string Summarize(List<TopoNode> chain, TopoNode? portal, bool cancelled)
    {
        string summary;
        if (portal is { Health: TopoHealth.Fail })
            summary = "认证断点：城域网认证未通过 —— 重新登录门户即可恢复，链路其余段见拓扑着色";
        else
        {
            var fail = chain.FirstOrDefault(n => n.Health == TopoHealth.Fail);
            if (fail != null)
                summary = $"断点定位：{fail.Badge}「{fail.Title}」异常 —— {fail.StatusText}";
            else
            {
                var warn = chain.FirstOrDefault(n => n.Health == TopoHealth.Warn);
                summary = warn != null
                    ? $"链路可用但有瓶颈：{warn.Badge} —— {warn.StatusText}"
                    : "全链路正常：本机 → 网关 → 校园出口 → 互联网 各段实测通过";
            }
        }
        if (cancelled) summary += "（诊断已取消：灰色节点为未完成项）";
        return summary;
    }

    // ---------- 工具 ----------
    private static string BuildFrom(string modeText)
    {
        var mode = string.IsNullOrWhiteSpace(modeText) ? "诊断" : modeText;
        return $"{mode} · {DateTime.Now:HH:mm:ss} 实测生成";
    }

    private static TopoHealth Map(CheckStatus s) => s switch
    {
        CheckStatus.Pass or CheckStatus.Fixed => TopoHealth.Ok,
        CheckStatus.Warning => TopoHealth.Warn,
        CheckStatus.Fail => TopoHealth.Fail,
        _ => TopoHealth.Unknown
    };

    private static TopoHealth Worse(TopoHealth a, TopoHealth b) => (int)a > (int)b ? a : b;

    private static TopoHealth First(params TopoHealth[] hs)
    {
        foreach (var h in hs) if (h != TopoHealth.Unknown) return h;
        return TopoHealth.Unknown;
    }

    private static TopoHealth HopHealth(PingStats st)
    {
        if (st.AllLost || st.LossPct > HopFailLossPct) return TopoHealth.Fail;
        if (st.AvgMs > HopWarnAvgMs || st.JitterMs > HopWarnJitterMs) return TopoHealth.Warn;
        return TopoHealth.Ok;
    }

    private static string? DescribePing(PingStats? st)
        => st == null ? null
        : st.AllLost ? "完全无响应"
        : $"平均 {st.AvgMs:0.#} ms · 丢包 {st.LossPct:0.#}%";

    private static string LinkLabel(bool wireless, PingStats? gwPing)
    {
        var link = wireless ? "无线" : "有线";
        return gwPing is { Received: > 0 } ? $"{link} · {gwPing.AvgMs:0.#} ms" : link;
    }

    private static long DnsElapsed(List<TopoDnsProbe>? probes, string key)
    {
        if (probes == null) return 0;
        var idx = key == "dns0" ? 0 : 1;
        return idx < probes.Count ? probes[idx].ElapsedMs : 0;
    }

    private static string HealthText(TopoHealth h) => h switch
    {
        TopoHealth.Ok => "正常",
        TopoHealth.Warn => "有瓶颈",
        TopoHealth.Fail => "异常",
        _ => "未检出"
    };

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "认证门户";
        var s = url;
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        var j = s.IndexOfAny(new[] { '/', '?', ':' });
        if (j >= 0) s = s[..j];
        if (s.Length > 24) s = s[..24];
        return string.IsNullOrEmpty(s) ? "认证门户" : s;
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s[..max];
}
