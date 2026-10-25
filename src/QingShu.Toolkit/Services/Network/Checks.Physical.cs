using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 第一层：物理链路 / 硬件驱动 / IP 配置 / 网关路由（8 项）。
/// </summary>
public static partial class NetworkChecks
{
    // ---------------- 物理连接状态 ----------------
    public sealed class PhysicalLinkCheck : CheckBase
    {
        public override string Id => "physical.link";
        public override string Name => "物理连接状态";
        public override CheckCategory Category => CheckCategory.PhysicalLink;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null)
                return CheckOutcome.Fail("未找到任何可用网络适配器", "请在设备管理器确认网卡是否存在，或联系机房管理员检查硬件。", false,
                    "NetworkInterface.GetAllNetworkInterfaces() 未返回可用的非回环适配器");

            var details = new List<string>
            {
                $"适配器: {p.Name} ({p.Description})",
                $"类型: {(p.IsWireless ? "无线 Wi-Fi" : p.Type.ToString())}",
                $"状态: {p.Status}",
                $"链路速率: {p.SpeedBps / 1_000_000} Mbps"
            };

            if (p.Status != OperationalStatus.Up)
                return CheckOutcome.Fail($"适配器“{p.Name}”未连接（{p.Status}）",
                    p.IsWireless ? "请检查 Wi-Fi 是否已开启并连接到校园无线网络。" : "请检查网线是否插好、墙上端口与交换机指示灯是否正常。",
                    canFix: true, details.ToArray());

            if (p.SpeedBps is > 0 and < 100_000_000)
                details.Add($"警告: 链路速率仅 {p.SpeedBps / 1_000_000} Mbps，可能为劣质网线或老旧端口");

            if (p.IsWireless)
            {
                ctx.Wifi = await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(false);
                if (ctx.Wifi != null)
                {
                    details.Add($"Wi-Fi 状态: {ctx.Wifi.State}  SSID: {ctx.Wifi.Ssid}  信号: {ctx.Wifi.SignalPct}%");
                    ctx.SetShared("wifi.ssid", ctx.Wifi.Ssid);
                }
            }

            ctx.SetShared("physical.ok", true);
            return CheckOutcome.Pass($"物理链路正常（{p.Name}）", details.ToArray());
        }
    }

    // ---------------- 网卡硬件与驱动 ----------------
    public sealed class NicHardwareCheck : CheckBase
    {
        public override string Id => "hardware.nic_pnp";
        public override string Name => "网卡硬件与驱动状态";
        public override CheckCategory Category => CheckCategory.HardwareDriver;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var adapters = await WmiHelper.QueryAsync(
                "SELECT Name, NetConnectionStatus, NetEnabled, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter=true",
                mo => new
                {
                    Name = mo["Name"]?.ToString() ?? "",
                    Status = Convert.ToInt32(mo["NetConnectionStatus"] ?? 0),
                    Enabled = mo["NetEnabled"]?.ToString(),
                    Pnp = mo["PNPDeviceID"]?.ToString() ?? ""
                }).ConfigureAwait(false);

            if (adapters.Count == 0)
                return CheckOutcome.Warn("未通过 WMI 查询到物理网卡信息", "可忽略此项；如网卡确实存在异常，请到设备管理器检查。");

            var details = new List<string>();
            string? problem = null;
            foreach (var a in adapters)
            {
                var statusText = a.Status switch
                {
                    0 => "已断开", 1 => "正在连接", 2 => "已连接", 3 => "正在断开",
                    4 => "硬件不存在", 5 => "硬件已禁用", 6 => "硬件故障", 7 => "媒体已断开",
                    8 => "正在认证", 9 => "认证失败", 10 => "地址无效", 11 => "需要授权",
                    _ => $"状态码 {a.Status}"
                };
                details.Add($"{a.Name} → {statusText}");
                if (a.Status is 4 or 6 or 9 or 10 or 11)
                    problem ??= $"{a.Name}: {statusText}";
                if (a.Status == 5 && (ctx.Primary == null || ctx.Primary.Description.Contains(a.Name.Split(' ')[0])))
                    problem ??= $"{a.Name}: 网卡已被禁用";

                // PnP 错误码 + 驱动信息
                if (!string.IsNullOrEmpty(a.Pnp))
                {
                    var escaped = a.Pnp.Replace("\\", "\\\\");
                    var err = await WmiHelper.QueryAsync(
                        $"SELECT ConfigManagerErrorCode, Name FROM Win32_PnPEntity WHERE PNPDeviceID='{escaped}'",
                        mo => Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0)).ConfigureAwait(false);
                    if (err.Count > 0 && err[0] != 0)
                    {
                        var errText = err[0] switch
                        {
                            1 => "设备未正确配置", 10 => "设备无法启动", 22 => "设备被禁用",
                            28 => "驱动未安装", 31 => "设备驱动异常", 43 => "设备因故障停止",
                            _ => $"配置管理器错误码 {err[0]}"
                        };
                        details.Add($"⚠ {a.Name} 设备异常: {errText}");
                        problem ??= $"{a.Name}: {errText}";
                    }
                    else if (err.Count > 0)
                    {
                        var drv = await WmiHelper.QueryAsync(
                            $"SELECT DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceID='{escaped}'",
                            mo => $"{mo["DriverVersion"]} ({mo["DriverDate"]})").ConfigureAwait(false);
                        if (drv.Count > 0) details.Add($"{a.Name} 驱动: {drv[0]}");
                    }
                }
            }

            if (problem != null)
                return CheckOutcome.Fail($"网卡硬件/驱动存在异常：{problem}",
                    "建议先在设备管理器中卸载并重装网卡驱动（勾选保留驱动文件），重启后再诊断；仍异常则可能是硬件故障需报修。",
                    canFix: false, details.ToArray());

            return CheckOutcome.Pass($"物理网卡与驱动正常（{adapters.Count} 个适配器）", details.ToArray());
        }
    }

    // ---------------- Wi-Fi 信号质量 ----------------
    public sealed class WifiSignalCheck : CheckBase
    {
        public override string Id => "wifi.signal";
        public override string Name => "Wi-Fi 信号质量";
        public override CheckCategory Category => CheckCategory.PhysicalLink;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var wifi = ctx.Wifi ?? await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(false);
            if (wifi == null)
                return CheckOutcome.Skip("未使用无线网络（有线连接），跳过 Wi-Fi 检查。");
            if (wifi.SignalPct < 0)
                return CheckOutcome.Pass($"Wi-Fi 已连接：{wifi.Ssid}");
            var detail = $"SSID: {wifi.Ssid}  信号强度: {wifi.SignalPct}%";
            if (wifi.SignalPct < 40)
                return CheckOutcome.Fail($"Wi-Fi 信号极弱（{wifi.SignalPct}%）", "请靠近无线 AP / 信号发射点，或反馈信息中心检查教室 AP。", detail);
            if (wifi.SignalPct < 65)
                return CheckOutcome.Warn($"Wi-Fi 信号偏弱（{wifi.SignalPct}%）", "信号弱会导致掉线与高延迟，建议靠近 AP 或由信息中心调整 AP 功率/信道。", detail);
            return CheckOutcome.Pass($"Wi-Fi 信号良好（{wifi.SignalPct}%）", detail);
        }
    }

    // ---------------- IP 地址配置 ----------------
    public sealed class IpConfigCheck : CheckBase
    {
        public override string Id => "ip.config";
        public override string Name => "IP 地址配置";
        public override CheckCategory Category => CheckCategory.IpConfig;
        public override bool InQuickMode => true;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null) return Task.FromResult(CheckOutcome.Skip("无可用适配器。"));
            var details = new List<string>
            {
                $"IPv4: {p.IpV4 ?? "无"}  掩码: {p.MaskV4 ?? "无"}",
                $"获取方式: {(p.IsDhcpEnabled ? "DHCP 自动获取" : "静态配置")}",
                $"网关: {p.Gateway ?? "无"}",
                $"DNS: {(p.DnsServers.Count > 0 ? string.Join(", ", p.DnsServers) : "无")}"
            };
            if (string.IsNullOrEmpty(p.IpV4))
                return Task.FromResult(CheckOutcome.Fail("未获取到 IPv4 地址", "网线/无线已连接但无 IP，通常是 DHCP 服务不可达，稍后会自动尝试续租修复。", canFix: true, details.ToArray()));

            if (NetworkProbeHelper.IsApipa(p.IpV4))
                return Task.FromResult(CheckOutcome.Fail($"获取到自动私有地址（APIPA {p.IpV4}），说明 DHCP 获取失败",
                    "将自动尝试“释放并重新获取 IP”；若多次失败，请联系信息中心检查校园网 DHCP 服务。", canFix: true, details.ToArray()));

            if (string.IsNullOrEmpty(p.Gateway))
                return Task.FromResult(CheckOutcome.Fail("已获取 IP 但未配置默认网关", "无法访问本网段以外的资源；将尝试续租修复，静态网络请核对网关配置。", canFix: true, details.ToArray()));

            if (p.DnsServers.Count == 0)
                return Task.FromResult(CheckOutcome.Fail("未配置 DNS 服务器", "无法解析任何域名；将尝试自动修复，也可在设置中配置首选 DNS。", canFix: true, details.ToArray()));

            ctx.SetShared("ip.ok", true);
            return Task.FromResult(CheckOutcome.Pass($"IP 配置正常（{p.IpV4}，{(p.IsDhcpEnabled ? "DHCP" : "静态")}）", details.ToArray()));
        }
    }

    // ---------------- DHCP 租约 ----------------
    public sealed class DhcpLeaseCheck : CheckBase
    {
        public override string Id => "ip.dhcp_lease";
        public override string Name => "DHCP 租约状态";
        public override CheckCategory Category => CheckCategory.IpConfig;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null) return CheckOutcome.Skip("无可用适配器。");
            if (!p.IsDhcpEnabled) return CheckOutcome.Pass("静态地址配置，不涉及 DHCP 租约。");

            var list = await WmiHelper.QueryAsync(
                "SELECT Description, DHCPEnabled, DHCPLeaseObtained, DHCPLeaseExpires, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=true",
                mo => new
                {
                    Desc = mo["Description"]?.ToString() ?? "",
                    Obtained = ManagementDateTimeConverter.ToDateTime(mo["DHCPLeaseObtained"]?.ToString() ?? ""),
                    Expires = ManagementDateTimeConverter.ToDateTime(mo["DHCPLeaseExpires"]?.ToString() ?? "")
                }).ConfigureAwait(false);

            var lease = list.FirstOrDefault(l => p.Description.StartsWith(l.Desc[..Math.Min(l.Desc.Length, 20)], StringComparison.OrdinalIgnoreCase))
                        ?? list.FirstOrDefault();
            if (lease == null || lease.Expires == default)
                return CheckOutcome.Pass("DHCP 租约信息暂不可读，但 IP 已正常获取。");

            var details = new List<string> { $"租约获得: {lease.Obtained:yyyy-MM-dd HH:mm}", $"租约到期: {lease.Expires:yyyy-MM-dd HH:mm}" };
            if (lease.Expires < DateTime.Now)
                return CheckOutcome.Fail($"DHCP 租约已过期（{lease.Expires:HH:mm}）", "将自动尝试续租；若反复过期，请联系信息中心检查 DHCP 作用域地址池是否耗尽。", canFix: true, details.ToArray());
            if (lease.Expires < DateTime.Now.AddMinutes(10))
                return CheckOutcome.Warn("DHCP 租约即将到期", "系统会自动续租，一般无需处理；如正在续租中出现断网属正常现象。", details.ToArray());
            return CheckOutcome.Pass("DHCP 租约正常", details.ToArray());
        }
    }

    // ---------------- 网关连通 ----------------
    public sealed class GatewayReachableCheck : CheckBase
    {
        public override string Id => "gateway.reachable";
        public override string Name => "默认网关连通（路由器/交换机）";
        public override CheckCategory Category => CheckCategory.GatewayRouting;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw))
                return CheckOutcome.Fail("没有默认网关可测试", "未配置网关意味着无法访问其他网段，请先解决 IP 配置问题。");

            var stats = await NetworkProbeHelper.PingAsync(gw, ctx.PingCount, ctx.TimeoutMs).ConfigureAwait(false);
            var details = new List<string>
            {
                $"网关地址: {gw}",
                $"丢包: {stats.LossPct:0.#}% ({stats.Received}/{stats.Sent})",
                $"延迟: 平均 {stats.AvgMs:0.#} ms  抖动 {stats.JitterMs:0.#} ms  最小/最大 {stats.MinMs:0.#}/{stats.MaxMs:0.#} ms"
            };

            if (stats.AllLost)
                return CheckOutcome.Fail($"网关 {gw} 完全不通", "本机到路由器/三层交换机之间断开：请检查网线、交换机端口，或重启教室路由器后重试。", canFix: true, details.ToArray());
            if (stats.LossPct > 20)
                return CheckOutcome.Fail($"网关丢包严重（{stats.LossPct:0.#}%）", "网线老化/水晶头接触不良/AP 过载的典型表现：先换一根网线或换一个端口，仍异常则报修。", details.ToArray());
            if (stats.AvgMs > ctx.Settings.LatencyFailMs)
                return CheckOutcome.Warn($"网关延迟过高（平均 {stats.AvgMs:0.#} ms）", "内网延迟异常，可能为 AP 过载或内网环路，请反馈信息中心。", details.ToArray());
            return CheckOutcome.Pass($"网关连通正常（平均 {stats.AvgMs:0.#} ms，丢包 {stats.LossPct:0.#}%）", details.ToArray());
        }
    }

    // ---------------- ARP 表项 ----------------
    public sealed class ArpCheck : CheckBase
    {
        public override string Id => "gateway.arp";
        public override string Name => "网关 ARP 表项（防欺骗/冲突）";
        public override CheckCategory Category => CheckCategory.GatewayRouting;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过。");

            var r = await ProcessRunner.RunAsync("arp -a").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 ARP 表", $"arp 命令失败：{r.Error ?? r.StdErr}");

            string? gwMac = null;
            var macMap = new Dictionary<string, string>();
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && System.Net.IPAddress.TryParse(parts[0], out _))
                {
                    var mac = parts[1].ToUpperInvariant();
                    macMap[parts[0]] = mac;
                    if (parts[0] == gw) gwMac = mac;
                }
            }
            var details = macMap.Select(kv => $"{kv.Key} → {kv.Value}").ToList();

            if (gwMac == null)
                return CheckOutcome.Fail($"ARP 表中缺少网关 {gw} 的表项", "将自动清除 ARP 缓存并重新学习；若仍缺失，可能是二层隔离或网关故障。", canFix: true,
                    $"ARP 表共 {macMap.Count} 条");
            if (gwMac.Contains("00-00-00-00") || gwMac.StartsWith("FF"))
                return CheckOutcome.Fail($"网关 {gw} 的 ARP 表项无效（{gwMac}）", "将自动清除 ARP 缓存重试；反复出现请检查是否有人使用 ARP 欺断工具。", canFix: true);

            // 同一 MAC 出现在多个 IP → IP/MAC 冲突或欺骗
            var dup = macMap.GroupBy(kv => kv.Value).Where(g => g.Count() > 1 && !g.Key.StartsWith("FF")).ToList();
            if (dup.Count > 0)
            {
                var lines = dup.Select(g => $"MAC {g.Key} 同时对应: {string.Join(", ", g.Select(x => x.Key))}");
                return CheckOutcome.Warn("发现多个 IP 绑定同一 MAC，存在 IP 冲突或 ARP 欺骗嫌疑",
                    "请反馈信息中心做内网排查（可能有人在用限速/欺骗软件）。", details.Concat(lines).ToArray());
            }
            return CheckOutcome.Pass($"网关 ARP 表项正常（{gw} → {gwMac}）", details.ToArray());
        }
    }

    // ---------------- 默认路由 ----------------
    public sealed class RouteCheck : CheckBase
    {
        public override string Id => "gateway.route";
        public override string Name => "默认路由检查";
        public override CheckCategory Category => CheckCategory.GatewayRouting;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("route print -4").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取路由表", r.Error ?? r.StdErr);

            var defaults = new List<string>();
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                    defaults.Add($"目标 {parts[0]}/{parts[1]} → 网关 {parts[2]}（接口 {parts[3]}，跃点 {parts[4]}）");
            }
            if (defaults.Count == 0)
                return CheckOutcome.Fail("路由表中不存在默认路由（0.0.0.0/0）", "将尝试续租修复；静态网络请检查网关与跃点数配置。", canFix: true, "route print -4 中未发现默认路由");

            var details = defaults.ToList();
            if (defaults.Count > 1)
                return CheckOutcome.Warn("存在多条默认路由（多出口/多网卡并存）", "多默认路由会造成时通时断：建议断开多余网络（如同时插网线又连 Wi-Fi），或由信息中心调整跃点数。", details.ToArray());
            return CheckOutcome.Pass("默认路由正常", details.ToArray());
        }
    }
}
