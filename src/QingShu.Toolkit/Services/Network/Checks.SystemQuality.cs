using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 第三层：系统时钟 / 代理与 VPN / 系统网络栈 / 连接质量（9 项）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- 系统时钟校准 ----------------
    public sealed class ClockSkewCheck : CheckBase
    {
        public override string Id => "clock.skew";
        public override string Name => "系统时钟校准";
        public override CheckCategory Category => CheckCategory.Clock;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            // 用不重定向的 HTTP Date 头对照（不依赖 TLS，避免时间错误造成证书失败而测不了）
            var r = await NetworkProbeHelper.HttpAsync("http://www.msftconnecttest.com/connecttest.txt", followRedirect: false, readBody: false).ConfigureAwait(false);
            if (r.ServerDateUtc == null)
                return CheckOutcome.Warn("无法获取服务器时间作为对照", "网络探测失败或响应无 Date 头，跳过时间校准检查。");

            var skew = Math.Abs((DateTime.UtcNow - r.ServerDateUtc.Value).TotalSeconds);
            var details = new List<string> { $"本机 UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}", $"服务器 UTC: {r.ServerDateUtc:yyyy-MM-dd HH:mm:ss}", $"偏差: {skew:0} 秒" };
            if (skew > 300)
                return CheckOutcome.Fail($"系统时钟偏差过大（{skew:0} 秒）", "时间错误会导致 HTTPS 证书失败、城域网认证失败：请立即校准时间（一键修复可自动同步）。", canFix: true, details.ToArray());
            if (skew > 60)
                return CheckOutcome.Warn($"系统时钟偏差 {skew:0} 秒", "建议校准时间，避免部分网站证书告警与认证异常。", details.ToArray());
            return CheckOutcome.Pass($"系统时钟准确（偏差 {skew:0} 秒）", details.ToArray());
        }
    }

    // ---------------- 系统代理 ----------------
    public sealed class ProxyCheck : CheckBase
    {
        public override string Id => "proxy.system";
        public override string Name => "系统代理检查";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
                int proxyEnable = Convert.ToInt32(key?.GetValue("ProxyEnable", 0) ?? 0);
                string proxyServer = key?.GetValue("ProxyServer")?.ToString() ?? "";
                string pac = key?.GetValue("AutoConfigURL")?.ToString() ?? "";
                var details = new List<string> { $"用户代理开关: {(proxyEnable == 1 ? "启用" : "关闭")}", $"代理服务器: {(string.IsNullOrEmpty(proxyServer) ? "无" : proxyServer)}", $"PAC 脚本: {(string.IsNullOrEmpty(pac) ? "无" : pac)}" };

                if (proxyEnable == 1)
                {
                    ctx.SetShared("proxy.enabled", true);
                    return Task.FromResult(CheckOutcome.Fail($"系统已启用代理（{proxyServer}）",
                        "错误代理会让“能 ping 通但打不开网页”：如无特殊需求将自动关闭代理并刷新系统设置。", canFix: true, details.ToArray()));
                }
                if (!string.IsNullOrEmpty(pac))
                    return Task.FromResult(CheckOutcome.Warn("配置了 PAC 自动代理脚本", "校园网内通常无需 PAC；若网页打不开可尝试关闭。", details.ToArray()));
                return Task.FromResult(CheckOutcome.Pass("未启用系统代理", details.ToArray()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("无法读取代理配置", ex.Message));
            }
        }
    }

    // ---------------- WinHTTP 代理 ----------------
    public sealed class WinHttpProxyCheck : CheckBase
    {
        public override string Id => "proxy.winhttp";
        public override string Name => "WinHTTP 代理检查";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh winhttp show proxy").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 WinHTTP 代理", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            if (Regex.IsMatch(text, @"直接访问|Direct access", RegexOptions.IgnoreCase))
                return CheckOutcome.Pass("WinHTTP 未设置代理。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(6).ToArray());
            ctx.SetShared("proxy.winhttp.set", true);
            return CheckOutcome.Warn("WinHTTP 设置了代理", "系统服务类程序会走此代理，配置错误会导致更新/认证异常：可一键修复重置。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8).ToArray());
        }
    }

    // ---------------- VPN / 多网卡 ----------------
    public sealed class VpnAdaptersCheck : CheckBase
    {
        public override string Id => "vpn.adapters";
        public override string Name => "VPN 与多网卡干扰";
        public override CheckCategory Category => CheckCategory.ProxyVpn;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var adapters = ctx.Adapters;
            var details = adapters.Select(a => $"{a.Name} [{a.Type}] {(a.IsUp ? "已连接" : "断开")} {(a.Gateway != null ? $"GW:{a.Gateway}" : "无网关")}").ToList();
            var suspicious = adapters.Where(a =>
                    a.Type == NetworkInterfaceType.Tunnel
                    || a.Name.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("TAP", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase)
                    || a.Name.Contains("Ppp", StringComparison.OrdinalIgnoreCase))
                .Where(a => a.IsUp).ToList();
            if (suspicious.Count > 0)
            {
                var names = string.Join(", ", suspicious.Select(a => a.Name));
                return Task.FromResult(CheckOutcome.Warn($"检测到 VPN/隧道适配器在运行（{names}）",
                    "VPN 会接管全部或部分流量，造成校内认证与内网访问异常：教学机建议关闭 VPN 后再诊断。", details.ToArray()));
            }
            var gwCount = adapters.Count(a => a.IsUp && a.Gateway != null);
            if (gwCount > 1)
                return Task.FromResult(CheckOutcome.Warn($"有 {gwCount} 个已连接网卡同时具备网关", "多网络并存会引起路由漂移：建议只保留教学所需的网络（断开 Wi-Fi 或拔掉多余网线）。", details.ToArray()));
            return Task.FromResult(CheckOutcome.Pass("无 VPN 干扰，网络环境干净。", details.ToArray()));
        }
    }

    // ---------------- 网络相关服务状态（只读，不做任何修改） ----------------
    public sealed class ServicesCheck : CheckBase
    {
        private static readonly (string Name, string Display)[] Watch =
        {
            ("Dhcp", "DHCP 客户端"), ("Dnscache", "DNS 缓存"), ("NlaSvc", "网络位置感知"),
            ("netprofm", "网络列表服务"), ("iphlpsvc", "IP Helper"), ("BFE", "基础筛选引擎")
        };

        public override string Id => "stack.services";
        public override string Name => "网络相关服务状态（只读）";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            var stopped = new List<string>();
            try
            {
                foreach (var (name, display) in Watch)
                {
                    try
                    {
                        using var sc = new System.ServiceProcess.ServiceController(name);
                        var st = sc.Status;
                        var text = st == System.ServiceProcess.ServiceControllerStatus.Running ? "运行中" : st.ToString();
                        details.Add($"{display} ({name}): {text}");
                        if (st != System.ServiceProcess.ServiceControllerStatus.Running)
                            stopped.Add($"{display} ({name})");
                    }
                    catch { details.Add($"{display} ({name}): 无法查询"); }
                }
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("服务状态查询失败", ex.Message));
            }

            if (stopped.Count > 0)
                // 遵循设计边界：绝不代改服务，仅给出处理建议
                return Task.FromResult(CheckOutcome.Fail($"网络相关服务未运行：{string.Join("、", stopped)}",
                    "本工具遵循安全边界不修改系统服务：请以管理员打开 services.msc，将上述服务设为“自动”并“启动”，或联系信息中心。", details.ToArray()));
            return Task.FromResult(CheckOutcome.Pass("网络相关服务全部运行中", details.ToArray()));
        }
    }

    // ---------------- 防火墙状态 ----------------
    public sealed class FirewallCheck : CheckBase
    {
        public override string Id => "stack.firewall";
        public override string Name => "防火墙状态";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh advfirewall show allprofiles state").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取防火墙状态", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            var lines = text.Split('\n').Where(l => l.Contains("配置文件") || l.Contains("Profile") || l.Contains("状态") || l.Contains("State")).Select(l => l.Trim()).ToList();
            int on = Regex.Matches(text, @"启用|ON\b", RegexOptions.IgnoreCase).Count;
            int off = Regex.Matches(text, @"禁用|OFF\b", RegexOptions.IgnoreCase).Count;
            if (off > 0 && on == 0)
                return CheckOutcome.Warn("所有配置文件的防火墙均已关闭", "校园终端建议保持防火墙开启（本工具不会代开，请自行权衡或由信息中心统一处理）。", lines.ToArray());
            return CheckOutcome.Pass($"防火墙状态正常（启用 {on} 项 / 关闭 {off} 项）", lines.ToArray());
        }
    }

    // ---------------- Winsock 目录 ----------------
    public sealed class WinsockCheck : CheckBase
    {
        public override string Id => "stack.winsock";
        public override string Name => "Winsock 目录完整性";
        public override CheckCategory Category => CheckCategory.SystemStack;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh winsock show catalog").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 Winsock 目录", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";
            var providers = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Regex.Match(raw, @"^(?:描述|Description)\s*[:：]\s*(.+)$");
                if (m.Success) providers.Add(m.Groups[1].Value.Trim());
            }
            var foreign = providers.Where(p => !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("微软", StringComparison.Ordinal)
                                              && !p.Contains("MSAFD", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("RSPCP", StringComparison.OrdinalIgnoreCase)
                                              && !p.Contains("TCP/IP", StringComparison.OrdinalIgnoreCase)).ToList();
            var details = providers.Select(p => p).Take(12).ToArray();
            if (foreign.Count > 0)
                return CheckOutcome.Warn($"发现 {foreign.Count} 个第三方 LSP 提供程序", "第三方 LSP 可能劫持/拖慢网络，甚至导致“能 ping 通但上不了网”：如无印象，可一键修复重置 Winsock（需重启）。", details.Concat(foreign.Select(f => "第三方: " + f)).ToArray());
            return CheckOutcome.Pass($"Winsock 目录正常（{providers.Count} 个提供程序）", details);
        }
    }

    // ---------------- 外网延迟与抖动 ----------------
    public sealed class LatencyQualityCheck : CheckBase
    {
        public override string Id => "quality.latency";
        public override string Name => "外网延迟与抖动";
        public override CheckCategory Category => CheckCategory.Quality;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var target = NetworkProbeHelper.PublicDnsServers[0];
            var stats = await NetworkProbeHelper.PingAsync(target, 10, ctx.TimeoutMs).ConfigureAwait(false);
            var details = new List<string>
            {
                $"目标 {target} × 10 次",
                $"平均 {stats.AvgMs:0.#} ms · 抖动 {stats.JitterMs:0.#} ms · 丢包 {stats.LossPct:0.#}%",
                $"最小/最大: {stats.MinMs:0.#}/{stats.MaxMs:0.#} ms"
            };
            if (stats.LossPct > 10)
                return CheckOutcome.Fail($"外网丢包 {stats.LossPct:0.#}%", "丢包明显：出口带宽拥塞或链路质量差，请反馈信息中心做出口侧排查。", details.ToArray());
            if (stats.JitterMs > 50)
                return CheckOutcome.Warn($"抖动偏大（{stats.JitterMs:0.#} ms）", "延迟波动明显，网课/直播会卡顿：可能是 Wi-Fi 干扰或 AP 过载。", details.ToArray());
            if (stats.AvgMs > 150)
                return CheckOutcome.Warn($"平均延迟偏高（{stats.AvgMs:0.#} ms）", "请结合时段判断：高峰期偏高属带宽问题，非高峰偏高属链路问题。", details.ToArray());
            return CheckOutcome.Pass($"外网延迟良好（平均 {stats.AvgMs:0.#} ms，丢包 {stats.LossPct:0.#}%）", details.ToArray());
        }
    }

    // ---------------- MTU 探测 ----------------
    public sealed class MtuCheck : CheckBase
    {
        public override string Id => "quality.mtu";
        public override string Name => "链路 MTU 探测";
        public override CheckCategory Category => CheckCategory.Quality;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过 MTU 探测。");

            // payload = MTU - 28(IP+ICMP头)
            var (payload, mtu) = await ProbeAsync(gw, 1472).ConfigureAwait(false);
            var details = new List<string> { $"测试有效载荷 {payload} 字节（DF 置位）→ 推断 MTU {mtu}" };
            if (mtu >= 1500)
                return CheckOutcome.Pass("链路 MTU 为标准 1500。", details.ToArray());
            if (mtu >= 1464)
                return CheckOutcome.Warn($"MTU 约 {mtu}（PPPoE 典型值 1492）", "PPPoE/隧道开销导致大包分片：网页/视频偶发加载失败可与此相关，一般无需处理。", details.ToArray());
            return CheckOutcome.Warn($"MTU 偏小（约 {mtu}）", "多层隧道/VPN 会压低 MTU，影响大文件传输：如有明显问题请联系信息中心核查中间链路。", details.ToArray());
        }

        private static async Task<(int Payload, int Mtu)> ProbeAsync(string host, int startPayload)
        {
            int payload = startPayload;
            int[] fallbacks = { 1464, 1452, 1400, 1300, 1200 };
            foreach (var p in new[] { payload }.Concat(fallbacks))
            {
                try
                {
                    using var ping = new Ping();
                    // SendPingAsync 的 buffer 参数是 byte[]（载荷），int 大小需转换
                    var reply = await ping.SendPingAsync(host, 1200, new byte[p], new PingOptions(64, true)).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) return (p, p + 28);
                }
                catch { }
            }
            return (0, 0);
        }
    }
}
