using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 疑难排查层（罕见/边缘问题，8 项，只读诊断、不改任何配置）：
/// IPv6 连通 / DNS 劫持与污染 / 网关级丢包 / 默认路由冲突 / 代理与直连差异 /
/// TCP 53 出境管控 / 校园认证客户端检测 / 本机协议栈自检。
/// 仅「全面诊断」模式运行（不进入快速体检与测速计划）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- IPv6 连通 ----------------
    public sealed class Ipv6Check : CheckBase
    {
        public override string Id => "rare.ipv6";
        public override string Name => "IPv6 连通性";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.GetIPProperties().GatewayAddresses.Count > 0);
            if (up == null) return CheckOutcome.Skip("无活动网卡，跳过 IPv6 检查。");

            var globals = up.GetIPProperties().UnicastAddresses.Where(a =>
                a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !a.Address.IsIPv6LinkLocal && !a.Address.IsIPv6SiteLocal &&
                !a.Address.IsIPv6Teredo && !IPAddress.IsLoopback(a.Address)).ToList();
            if (globals.Count == 0)
                return CheckOutcome.Pass("未启用全局 IPv6 地址（校园网常见配置，不影响使用）", $"网卡：{up.Name}");

            var r = await NetworkProbeHelper.TcpAsync("2400:3200::1", 443, 2500).ConfigureAwait(false);
            var details = new List<string> { $"全局 IPv6：{globals[0].Address}（前缀来源 {globals[0].PrefixOrigin}）", $"IPv6 出口探测 2400:3200::1:443 → {(r.Ok ? $"可达（{r.ElapsedMs} ms）" : r.Error)}" };
            if (r.Ok)
                return CheckOutcome.Pass("IPv6 已启用且可出公网", details.ToArray());
            return CheckOutcome.Warn("IPv6 已启用但无法出公网",
                "常见于路由器只下发地址未放行出口、或校园网过滤 IPv6：多数网站会自动回退 IPv4，如个别站点打不开可尝试关闭该网卡的 IPv6。", details.ToArray());
        }
    }

    // ---------------- DNS 劫持 / 污染探测 ----------------
    public sealed class DnsHijackCheck : CheckBase
    {
        public override string Id => "rare.dnshijack";
        public override string Name => "DNS 劫持与污染探测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            // 1) NXDOMAIN 探测：保证不存在的域名若“解析成功”即存在劫持
            var probe = $"nx-probe-{Guid.NewGuid():N}.invalid";
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(probe).ConfigureAwait(false);
                var shown = string.Join(", ", addrs.Select(a => a.ToString()).Take(3));
                details.Add($"探测域名 {probe}（保证不存在）→ 被解析为 {shown}");
                return CheckOutcome.Fail("检测到 DNS 劫持（不存在域名被强制解析）",
                    "运营商广告注入、流氓软件或门户静默劫持均会如此：多数网站打不开/弹广告可与此相关，先查 Hosts（本页已有专项），再考虑更换 DNS。",
                    details.ToArray());
            }
            catch { details.Add("探测域名未解析（NXDOMAIN 行为正确）"); }

            // 2) 系统解析 vs 权威公共 DNS 直查对照
            var direct = await DnsQueryHelper.ResolveAsync("223.5.5.5", "www.qq.com", 2000).ConfigureAwait(false);
            try
            {
                var sys = await Dns.GetHostAddressesAsync("www.qq.com").ConfigureAwait(false);
                var sysText = string.Join(", ", sys.Select(a => a.ToString()).Take(2));
                details.Add($"系统解析 www.qq.com → {sysText}");
                details.Add($"直查 223.5.5.5 → {(direct.Ok ? direct.AddressText : direct.Error)}");
                if (!direct.Ok)
                    details.Add("公共 DNS 直查不可达（外网受限时属正常，不作为劫持判据）");
            }
            catch (Exception ex)
            {
                details.Add($"系统解析失败：{ex.Message}（直查公共 DNS {(direct.Ok ? "可达 → 判定本机解析链路异常" : "也不可达 → 外网不通，非劫持")}）");
                if (direct.Ok)
                    return CheckOutcome.Warn("本机解析失败但公共 DNS 可达",
                        "解析链路异常（DNS 缓存损坏或 DNS 服务配置失效）：可尝试一键修复中的“刷新 DNS 缓存”。", details.ToArray());
            }
            return CheckOutcome.Pass("未发现 DNS 劫持迹象", details.ToArray());
        }
    }

    // ---------------- 网关级丢包 ----------------
    public sealed class GatewayLossCheck : CheckBase
    {
        public override string Id => "rare.gatewayloss";
        public override string Name => "网关链路丢包（×20）";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过网关丢包检查。");

            var stats = await NetworkProbeHelper.PingAsync(gw, 20, 1000).ConfigureAwait(false);
            var details = new List<string>
            {
                $"网关 {gw} × 20 次",
                $"收到 {stats.Received}/{stats.Sent} · 丢包 {stats.LossPct:0.#}% · 平均 {stats.AvgMs:0.#} ms · 抖动 {stats.JitterMs:0.#} ms"
            };
            if (stats.AllLost)
                return CheckOutcome.Fail($"网关完全无响应（{stats.Sent} 次全部超时）",
                    "第一跳即不通：网线/交换机口/AP 关联异常，请先看「物理连接状态」与「默认网关连通」两项结论。", details.ToArray());
            if (stats.LossPct > 20)
                return CheckOutcome.Fail($"网关丢包 {stats.LossPct:0.#}%",
                    "第一跳就丢包属于内网链路故障（网线老化/水晶头氧化/AP 过载/Wi-Fi 漫游）：换网口或换网线可快速定位。", details.ToArray());
            if (stats.LossPct > 0)
                return CheckOutcome.Warn($"网关偶发丢包 {stats.LossPct:0.#}%",
                    "轻微内网丢包：直播/网课会偶发卡顿，多为 Wi-Fi 干扰或 AP 负载，非高峰期复测确认。", details.ToArray());
            return CheckOutcome.Pass("网关链路无丢包", details.ToArray());
        }
    }

    // ---------------- 默认路由冲突 ----------------
    public sealed class RouteConflictCheck : CheckBase
    {
        public override string Id => "rare.route";
        public override string Name => "默认路由冲突";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("route print -4").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取路由表", r.Error ?? r.StdErr);

            var gateways = new List<string>();
            foreach (var line in (r.StdOut ?? "").Split('\n'))
            {
                var m = Regex.Match(line, @"^\s*0\.0\.0\.0\s+0\.0\.0\.0\s+(\S+)");
                if (m.Success) gateways.Add(m.Groups[1].Value);
            }
            var distinct = gateways.Distinct().ToList();
            var details = gateways.Select((g, i) => $"默认路由 {i + 1}: {g}").ToList();
            if (distinct.Count == 0)
                return CheckOutcome.Fail("未找到 IPv4 默认路由", "系统没有默认网关路由：所有外网流量无处可去，请查看「IP 配置 / DHCP」两项并重新获取地址。", details.ToArray());
            if (distinct.Count > 1)
                return CheckOutcome.Warn($"存在 {distinct.Count} 条不同网关的默认路由",
                    "多网卡/VPN 残留路由会造成“时通时断、部分网站不通”的路由漂移：断开不用的网络或联系管理员清理残留路由。", details.ToArray());
            return CheckOutcome.Pass($"默认路由唯一（{distinct[0]}）", details.ToArray());
        }
    }

    // ---------------- 代理与直连差异 ----------------
    public sealed class ProxyDiffCheck : CheckBase
    {
        public override string Id => "rare.proxydiff";
        public override string Name => "代理与直连差异探测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            using var sys = new HttpClient() { Timeout = TimeSpan.FromSeconds(8) };                       // 遵循系统代理/PAC
            using var direct = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(8)                                                          // 强制直连
            };
            bool sysOk, directOk;
            try { using var _ = await sys.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); sysOk = true; }
            catch { sysOk = false; }
            try { using var _ = await direct.GetAsync("http://www.msftconnecttest.com/connecttest.txt", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); directOk = true; }
            catch { directOk = false; }

            var details = new List<string> { $"走系统代理链路: {(sysOk ? "通" : "不通")}", $"强制直连: {(directOk ? "通" : "不通")}" };
            if (sysOk && directOk) return CheckOutcome.Pass("系统代理链路与直连一致（当前无代理干扰）", details.ToArray());
            if (!sysOk && directOk)
                return CheckOutcome.Fail("走系统代理不通、直连正常（典型代理故障）",
                    "系统代理/PAC 配置已失效：本页「系统代理检查」若同样告警可一键修复关闭代理；若它显示未启用，请检查是否有 PAC 残留或安全软件接管。", details.ToArray());
            if (sysOk && !directOk)
                return CheckOutcome.Warn("直连被拦但代理可达",
                    "校园网通常直连放行、代理受限：可能被出口策略管控，若日常使用正常可忽略。", details.ToArray());
            return CheckOutcome.Skip("两条链路均不通（外网不可达），跳过代理差异判断，请先看外网连通项结论。");
        }
    }

    // ---------------- TCP 53 出境管控 ----------------
    public sealed class Port53Check : CheckBase
    {
        public override string Id => "rare.port53";
        public override string Name => "TCP 53 出境管控";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var a = await NetworkProbeHelper.TcpAsync("223.5.5.5", 53, 2500).ConfigureAwait(false);
            var b = await NetworkProbeHelper.TcpAsync("119.29.29.29", 53, 2500).ConfigureAwait(false);
            var details = new List<string>
            {
                $"TCP 223.5.5.5:53 → {(a.Ok ? $"可达（{a.ElapsedMs} ms）" : a.Error)}",
                $"TCP 119.29.29.29:53 → {(b.Ok ? $"可达（{b.ElapsedMs} ms）" : b.Error)}"
            };
            if (a.Ok || b.Ok)
                return CheckOutcome.Pass("TCP 53 出境正常（公共 DNS 可用）", details.ToArray());
            return CheckOutcome.Warn("公共 DNS 的 TCP 53 出境被拦截",
                "校园网常见管控：若 UDP 53 同样被管控，则“修改 DNS”类修复（含一键修复的改 DNS）不会生效，属校方策略，需按认证门户方式上网或联系信息中心。", details.ToArray());
        }
    }

    // ---------------- 校园认证客户端检测 ----------------
    public sealed class AuthClientCheck : CheckBase
    {
        private static readonly string[] Signs =
            { "drcom", "drmain", "ruijie", "srun", "surfilter", "netkeeper", "h3c", "imcsvc", "supplicant" };

        public override string Id => "rare.authclient";
        public override string Name => "校园认证客户端检测";
        public override CheckCategory Category => CheckCategory.Rare;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            if (ctx.SharedBool("portal.detected") != true)
                return Task.FromResult(CheckOutcome.Skip("网络直通、未检测到认证门户，无需认证客户端。"));

            var found = new List<string>();
            foreach (var p in Process.GetProcesses())
            {
                var name = p.ProcessName.ToLowerInvariant();
                if (Signs.Any(s => name.Contains(s, StringComparison.Ordinal))) found.Add(p.ProcessName);
            }
            if (found.Count > 0)
                return Task.FromResult(CheckOutcome.Pass($"检测到认证客户端进程：{string.Join(", ", found.Take(3))}",
                    "门户认证应优先由官方客户端完成；若客户端在线仍被门户拦截，多为客户端版本过旧或掉线。"));
            return Task.FromResult(CheckOutcome.Warn("检测到认证门户但未发现认证客户端",
                "部分学校要求专用客户端（Dr.COM/锐捷/Srun 等）完成认证：请确认是否需要安装；若网页登录即可，可忽略本提示。",
                "已扫描进程关键字：" + string.Join("/", Signs)));
        }
    }

    // ---------------- 本机协议栈自检 ----------------
    public sealed class StackSelfTestCheck : CheckBase
    {
        public override string Id => "rare.stack_selftest";
        public override string Name => "本机协议栈自检（环回）";
        public override CheckCategory Category => CheckCategory.Rare;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var acceptTask = listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    using var accepted = await acceptTask;
                    sw.Stop();
                    return CheckOutcome.Pass($"本机 TCP 协议栈自检通过（环回连接 {sw.ElapsedMilliseconds} ms）",
                        $"127.0.0.1:{port} 建连成功。若本项失败而其余网络项异常，优先怀疑 Winsock/LSP 损坏。");
                }
                finally { listener.Stop(); }
            }
            catch (Exception ex)
            {
                return CheckOutcome.Fail("本机协议栈自检失败",
                    "环回 TCP 都无法建立是 Winsock/LSP 损坏的典型症状（第三方网络软件/病毒破坏）：可执行一键修复中的「重置 Winsock」并重启。", $"异常：{ex.Message}");
            }
        }
    }
}
