using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 低速专项检查层（测速与吞吐，8 项）：
/// 链路速率与双工 → Wi-Fi 无线电细节 → DNS 解析耗时 → 分段延迟画像 →
/// 下载吞吐实测 → 后台占用 → TCP 全局参数 → 传递优化 P2P。
/// 全部只读探测；不自动修改任何配置（提速修复走询问式一键修复）。
/// </summary>
public static partial class NetworkChecks
{
    private static string FormatMbps(double mbps)
        => mbps >= 1000 ? $"{mbps / 1000:0.#} Gbps" : mbps > 0 ? $"{mbps:0} Mbps" : "未知";

    // ---------------- 1. 链路速率与双工协商 ----------------
    public sealed class NicRateCheck : CheckBase
    {
        public override string Id => "speed.nic_rate";
        public override string Name => "链路速率与双工协商";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var p = ctx.Primary;
            if (p == null || !p.IsUp) return CheckOutcome.Skip("无活动网卡，跳过链路速率检查。");
            double mbps = p.SpeedBps / 1_000_000.0;
            ctx.SetShared("speed.linkmbps", mbps);

            var details = new List<string>
            {
                $"网卡：{p.Description}",
                $"类型：{(p.IsWireless ? "无线 Wi-Fi" : "有线以太网")}",
                $"协商速率：{FormatMbps(mbps)}"
            };

            // 一次 PowerShell 调用同时取节能托管状态与全双工状态（cmd 安全：无 % 与未引号的 &；输出带标记前缀防错位）
            bool powersave = false, halfDuplex = false, psKnown = false, duplexKnown = false;
            var ps = await ProcessRunner.RunAsync(
                "powershell -NoProfile -Command \"$a=Get-NetAdapter|Where-Object {$_.InterfaceGuid -eq '"
                + p.Id + "'}; if($a){ $p=''; try{ $p=(Get-NetAdapterPowerManagement -InterfaceIndex $a.InterfaceIndex).AllowComputerToTurnOffDevice }catch{}; Write-Output ('PWR='+$p); Write-Output ('DUP='+$a.FullDuplex) }\"",
                25000).ConfigureAwait(false);
            foreach (var raw in (ps.StdOut ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("PWR=", StringComparison.Ordinal))
                {
                    var v = line[4..].Trim();
                    psKnown = v.Length > 0 && !v.Equals("Unsupported", StringComparison.OrdinalIgnoreCase);
                    powersave = v.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("DUP=", StringComparison.Ordinal))
                {
                    duplexKnown = true;
                    halfDuplex = line[4..].Trim().Equals("False", StringComparison.OrdinalIgnoreCase);
                }
            }
            details.Add($"全双工：{(!duplexKnown ? "未知" : halfDuplex ? "否（半双工！）" : "是")}");
            details.Add($"节能托管（系统可关闭网卡省电）：{(!psKnown ? "未知" : powersave ? "开启" : "关闭")}");
            if (powersave) ctx.SetShared("nic.powersave", true);

            if (halfDuplex)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Fail("网卡处于半双工模式",
                    "半双工会使吞吐骤降、冲突重传剧增：多为网线老化/水晶头氧化或交换机端口协商异常。可尝试一键修复重新协商；无效请更换网线或换交换机端口。",
                    canFix: true, details.ToArray());
            }
            if (!p.IsWireless && mbps > 0 && mbps < 1000)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Warn($"有线链路仅协商到 {FormatMbps(mbps)}",
                    "教室有线网通常应为 1 Gbps：检查网线是否五类（CAT5e）及以上、水晶头是否氧化、交换机端口是否百兆；一键修复可重新协商，无效时更换网线。",
                    details.ToArray());
            }
            if (p.IsWireless && mbps > 0 && mbps < 100)
            {
                ctx.SetShared("nic.rate.low", true);
                return CheckOutcome.Warn($"无线协商速率偏低（{FormatMbps(mbps)}）",
                    "无线 PHY 速率低通常意味着信号弱或频段拥挤：尽量靠近 AP、切换 5G 频段 SSID，或改用有线连接。", details.ToArray());
            }
            return CheckOutcome.Pass($"链路速率正常（{FormatMbps(mbps)}，双工正常）", details.ToArray());
        }
    }

    // ---------------- 2. Wi-Fi 无线电细节 ----------------
    public sealed class WifiPhyCheck : CheckBase
    {
        public override string Id => "speed.wifi_phy";
        public override string Name => "Wi-Fi 频段与接收速率";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            if (ctx.Primary is { IsWireless: false })
                return CheckOutcome.Skip("当前使用有线连接，跳过 Wi-Fi 无线电检查。");
            var r = await ProcessRunner.RunAsync("netsh wlan show interfaces", 15000).ConfigureAwait(false);
            if (r.ExitCode != 0 || string.IsNullOrEmpty(r.StdOut))
                return CheckOutcome.Skip("本机无 Wi-Fi 接口或查询失败，跳过。");

            string radio = "";
            int channel = 0;
            double rx = 0;
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Match(line, @"(?:无线电类型|Radio\s+type)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && radio.Length == 0) radio = m.Groups[1].Value;
                m = Regex.Match(line, @"(?:信道|Channel)\s*[:：]\s*(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && channel == 0) channel = int.Parse(m.Groups[1].Value);
                m = Regex.Match(line, @"(?:接收速率|Receive\s+rate)[^:\r\n：]*[:：]\s*([\d.]+)", RegexOptions.IgnoreCase);
                if (m.Success && rx == 0) rx = double.Parse(m.Groups[1].Value);
            }
            if (radio.Length == 0 && channel == 0)
                return CheckOutcome.Skip("Wi-Fi 未连接，跳过。");

            var details = new List<string>
            {
                $"无线电类型：{radio}",
                $"信道：{(channel > 0 ? channel.ToString() : "未知")}（{(channel <= 14 && channel > 0 ? "2.4 GHz" : channel >= 36 ? "5 GHz" : "?")}）",
                $"接收速率（PHY）：{FormatMbps(rx)}"
            };
            var signal = ctx.Wifi?.SignalPct ?? -1;
            if (signal >= 0) details.Add($"信号强度：{signal}%");

            bool is24G = channel > 0 && channel <= 14;
            if (is24G)
            {
                ctx.SetShared("wifi.24g", true);
                if (radio.Contains("802.11b", StringComparison.OrdinalIgnoreCase) || radio.Contains("802.11g", StringComparison.OrdinalIgnoreCase))
                    return CheckOutcome.Warn("2.4 GHz 频段 + 老协议（b/g）",
                        "老旧协议和 2.4G 频段速率天花板极低且极易受干扰：优先切换到 5 GHz SSID，或改用有线。", details.ToArray());
                if (channel != 1 && channel != 6 && channel != 11)
                {
                    ctx.SetShared("wifi.channel.overlap", true);
                    return CheckOutcome.Warn($"2.4 GHz 信道重叠（CH{channel}）",
                        "2.4G 只有 1/6/11 三个互不重叠信道，其余信道会与邻区 AP 相互干扰拖慢全体：建议 AP 侧调整或连接 5G。", details.ToArray());
                }
                if (rx > 0 && rx < 130)
                    return CheckOutcome.Warn($"2.4 GHz 接收速率偏低（{FormatMbps(rx)}）",
                        "多为信号弱或干扰大：靠近 AP、避开微波炉/蓝牙设备，或连接 5 GHz 频段。", details.ToArray());
                return CheckOutcome.Warn("连接在 2.4 GHz 频段",
                    "2.4G 频段在教室密集场景容易拥挤：即便当前速率尚可，高峰期也可能掉速，建议优先使用 5 GHz 或有线。", details.ToArray());
            }

            // 5 GHz
            if (rx > 0 && rx < 200)
                return CheckOutcome.Warn($"5 GHz 接收速率偏低（{FormatMbps(rx)}）",
                    "5G 频段穿墙能力弱：接收速率低说明离 AP 远或有遮挡，建议调整座位/加 AP 或改有线。", details.ToArray());
            return CheckOutcome.Pass($"Wi-Fi 状态良好（{radio}，接收 {FormatMbps(rx)}）", details.ToArray());
        }
    }

    // ---------------- 3. DNS 解析耗时 ----------------
    public sealed class DnsPerfCheck : CheckBase
    {
        private static readonly string[] Domains = { "www.msftconnecttest.com", "www.baidu.com", "qq.com" };

        public override string Id => "speed.dns_perf";
        public override string Name => "DNS 解析耗时";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var servers = (ctx.Primary?.DnsServers ?? new List<string>()).Take(2).ToList();
            if (servers.Count == 0) servers = NetworkProbeHelper.PublicDnsServers.Take(1).ToList();

            var details = new List<string>();
            double allAvg = 0;
            int allCount = 0, failCount = 0;
            foreach (var srv in servers)
            {
                long sum = 0; int ok = 0; int fail = 0;
                for (int i = 0; i < 3; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = await DnsQueryHelper.ResolveAsync(srv, Domains[i % Domains.Length], 1500).ConfigureAwait(false);
                    if (r.Ok) { sum += r.ElapsedMs; ok++; }
                    else fail++;
                }
                failCount += fail;
                double avg = ok > 0 ? (double)sum / ok : 0;
                if (ok > 0) { allAvg += sum; allCount += ok; }
                details.Add($"{srv}：平均 {avg:0} ms（成功 {ok}/3）");
            }
            double overall = allCount > 0 ? allAvg / allCount : 0;
            details.Add($"总体平均解析耗时：{overall:0} ms，失败 {failCount} 次");

            if (failCount >= 5 || (allCount == 0))
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Fail("DNS 解析大面积失败", "解析失败会让每个网页都要等待多次重试：一键修复可将 DNS 切换为公共 DNS（需校园网允许）并刷新缓存。", details.ToArray());
            }
            if (overall > 800)
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Fail($"DNS 解析极慢（平均 {overall:0} ms）",
                    "打开每个网站都要先等 DNS，这是“网速慢”最容易被忽视的原因之一：建议一键修复切换 DNS。", details.ToArray());
            }
            if (overall > 300)
            {
                ctx.SetShared("dns.slow", true);
                return CheckOutcome.Warn($"DNS 解析偏慢（平均 {overall:0} ms）",
                    "正常应在 100 ms 内：解析慢会拖慢所有网站的首屏。可一键修复切换为更快的公共 DNS（若校园网限制外部 DNS 请反馈信息中心）。", details.ToArray());
            }
            return CheckOutcome.Pass($"DNS 解析迅速（平均 {overall:0} ms）", details.ToArray());
        }
    }

    // ---------------- 4. 分段延迟画像（校园拓扑感知） ----------------
    /// <summary>
    /// 学校真实链路：班级/教师电脑 →（部分班级经中转设备，常见为光猫/交换机）→ 楼宇软路由
    /// → 科技楼服务器 → 裸光纤 → 城域网；另有少量电脑直连科技楼服务器。
    /// 楼宇软路由与科技楼服务器是整栋楼/全校共享压力点，因此除「本机→网关」外，
    /// 还用 tracert 找出内网各跳并逐跳统计，把瓶颈定位到三段之一：
    /// 接入段（本机→直连网关）/ 核心段（软路由→科技楼服务器）/ 出口段（服务器→裸光纤→城域网）。
    /// </summary>
    public sealed class PathProfileCheck : CheckBase
    {
        public override string Id => "speed.path_profile";
        public override string Name => "分段延迟画像";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var gw = ctx.Primary?.Gateway;
            if (string.IsNullOrEmpty(gw)) return CheckOutcome.Skip("无网关，跳过分段延迟画像。");

            // 第 1 跳：本机 → 直连网关（班级中转设备或楼宇软路由，视教室接法而定）
            var stGw = await NetworkProbeHelper.PingAsync(gw!, 10, ctx.TimeoutMs).ConfigureAwait(false);
            var stDns = await NetworkProbeHelper.PingAsync(NetworkProbeHelper.PublicDnsServers[0], 10, ctx.TimeoutMs).ConfigureAwait(false);
            var stFar = await NetworkProbeHelper.PingAsync("www.baidu.com", 10, ctx.TimeoutMs).ConfigureAwait(false);

            var details = new List<string>
            {
                $"第1跳 接入段（班级中转设备或楼宇软路由）{gw}：平均 {stGw.AvgMs:0.#} ms · 抖动 {stGw.JitterMs:0.#} ms · 丢包 {stGw.LossPct:0.#}%"
            };

            // tracert 摸出内网中间跳（-d 免反解；-h 8 覆盖「中转→软路由→服务器→出口」典型 3-5 跳）
            var innerHops = await TraceInnerHopsAsync(ct, gw).ConfigureAwait(false);
            var innerStats = new List<(string Ip, PingStats Ping, string Role)>();
            foreach (var (ip, role) in innerHops)
            {
                if (ip == gw || innerStats.Any(x => x.Ip == ip)) continue;
                ct.ThrowIfCancellationRequested();
                // 内网跳探测限时收口：单跳 6 发 × ≤1500ms，避免整体诊断被拖长
                var st = await NetworkProbeHelper.PingAsync(ip, 6, Math.Min(ctx.TimeoutMs, 1500)).ConfigureAwait(false);
                innerStats.Add((ip, st, role));
                details.Add($"{role} {ip}：平均 {st.AvgMs:0.#} ms · 抖动 {st.JitterMs:0.#} ms · 丢包 {st.LossPct:0.#}%");
            }
            if (innerStats.Count == 0)
                details.Add("内网中间跳：tracert 未探测到（可能直连核心或中间设备禁 ICMP），按两段模式解读");

            details.Add($"公网段（223.5.5.5）：平均 {stDns.AvgMs:0.#} ms · 抖动 {stDns.JitterMs:0.#} ms · 丢包 {stDns.LossPct:0.#}%");
            if (!stFar.AllLost)
                details.Add($"互联网目标（baidu）：平均 {stFar.AvgMs:0.#} ms · 丢包 {stFar.LossPct:0.#}%");
            else
                details.Add("互联网目标（baidu）：目标禁 ICMP，跳过该段对比");

            // 判定 1：接入段 —— 班级中转设备/楼宇软路由的接入侧过载或劣化
            if (stGw.LossPct > 20 || stGw.AvgMs > 30 || stGw.JitterMs > 25)
            {
                ctx.SetShared("path.gw.bad", true);
                return CheckOutcome.Fail("接入段异常（本机 → 班级中转/楼宇软路由）",
                    "第一跳就高延迟/丢包，问题在教室到楼宇接入之间：先问同班/同楼层同学是否同时变慢——都慢则是班级中转设备（疑似光猫/交换机）或楼宇软路由过载，属本校报修；仅本机慢则换网线/换端口/换有线排除本机链路。",
                    details.ToArray());
            }

            // 判定 2：核心段 —— 网关之后内网各跳相对第一跳明显劣化（软路由上联/科技楼服务器压力）
            if (innerStats.Count > 0)
            {
                var core = innerStats[^1];
                double deltaMs = core.Ping.AvgMs - stGw.AvgMs;
                double deltaLoss = core.Ping.LossPct - stGw.LossPct;
                if (deltaMs > 15 || deltaLoss > 10)
                {
                    ctx.SetShared("path.core.bad", true);
                    return CheckOutcome.Warn("核心段压力（楼宇软路由 → 科技楼服务器）",
                        "到直连网关很快、到内网核心明显变慢：楼宇软路由上联、级联中转链路或科技楼服务器（全校共享核心）负载偏高。大流量任务（更新/资料分发）请错峰；持续出现请信息中心核查软路由连接数与服务器上联利用率。",
                        details.ToArray());
                }
            }

            // 判定 3：出口段 —— 内网快、出公网慢（裸光纤/城域网高峰拥塞）
            if (stDns.LossPct > 10 || stDns.AvgMs - stGw.AvgMs > 80)
            {
                ctx.SetShared("path.uplink.bad", true);
                return CheckOutcome.Warn("出口段拥塞（科技楼服务器 → 裸光纤 → 城域网）",
                    "内网各段都很快、出公网即慢：典型的裸光纤上联/城域网出口高峰期拥塞。可错峰使用、限制大流量应用；若持续如此，携 PDF 报告请教体局信息中心核查出口带宽与 QoS 策略。",
                    details.ToArray());
            }
            if (!stFar.AllLost && stFar.AvgMs - stDns.AvgMs > 100)
            {
                ctx.SetShared("path.remote.bad", true);
                return CheckOutcome.Warn("出口 → 互联网目标段偏慢",
                    "到公共 DNS 很快、到具体网站慢：多为该网站/CDN 本身或其到你所在地区的线路问题，换时段或换资源源站即可验证。", details.ToArray());
            }
            return CheckOutcome.Pass("各段延迟正常，瓶颈更可能在带宽占用或无线速率", details.ToArray());
        }

        /// <summary>
        /// tracert 到公网 DNS，提取「网关之后、出公网之前」的内网跳。
        /// 跃点号直接取自 tracert 行首（网关通常即第 1 跳，已在角色装配时剔除避免重复展示）；
        /// 角色标注：最后一个内网跳 = 科技楼核心（服务器侧出口），其前的内网跳 = 楼宇软路由/汇聚中转。
        /// 失败返回空列表（回退两段模式，不阻塞诊断）。
        /// </summary>
        private static async Task<List<(string Ip, string Role)>> TraceInnerHopsAsync(CancellationToken ct, string? gw)
        {
            var list = new List<(string, string)>();
            try
            {
                var r = await ProcessRunner.RunAsync("tracert -d -h 8 -w 400 223.5.5.5", 32000).ConfigureAwait(false);
                if (r.ExitCode != 0 || string.IsNullOrEmpty(r.StdOut)) return list;

                var ipRe = new Regex(@"\b(\d{1,3}(?:\.\d{1,3}){3})\b");
                var rows = new List<(int Hop, string Ip)>();
                foreach (var raw in r.StdOut.Split('\n'))
                {
                    ct.ThrowIfCancellationRequested();
                    var num = Regex.Match(raw, @"^\s*(\d{1,2})\s+");
                    if (!num.Success) continue;                                 // 只取「跃点数」行
                    var m = ipRe.Match(raw);
                    if (!m.Success) continue;                                   // 该跳全超时（* * *）
                    var ip = m.Groups[1].Value;
                    if (!NetworkProbeHelper.IsPrivateIp(ip)) break;             // 进入公网段：内网拓扑已完整
                    rows.Add((int.Parse(num.Groups[1].Value), ip));
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    var (hop, ip) = rows[i];
                    if (ip == gw) continue;                                     // 网关已作第1跳单独展示
                    var role = i == rows.Count - 1
                        ? $"第{hop}跳 核心段（科技楼服务器/核心出口侧）"
                        : $"第{hop}跳 核心段（楼宇软路由/汇聚中转）";
                    list.Add((ip, role));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* tracert 不存在/失败：回退两段模式 */ }
            return list;
        }
    }

    // ---------------- 5. 下载吞吐实测 ----------------
    public sealed class DownloadSpeedCheck : CheckBase
    {
        public override string Id => "speed.download";
        public override string Name => "下载吞吐实测";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var pre = await NetworkProbeHelper.HttpAsync("http://www.msftconnecttest.com/connecttest.txt",
                followRedirect: false, timeoutMs: 5000, readBody: false).ConfigureAwait(false);
            if (!pre.Ok)
                return CheckOutcome.Skip("当前无外网连接，跳过下载测速（请先解决连通性问题）。");

            var s = ctx.Settings;
            var result = await SpeedTestHelper.MeasureAsync(
                s.SpeedTestUrl, s.SpeedTestSeconds, s.SpeedTestConnections, (long)s.SpeedTestCapMb << 20,
                ctx.SpeedProgress, ct).ConfigureAwait(true);

            double link = ctx.GetShared<double>("speed.linkmbps");
            if (link <= 0 && ctx.Primary != null) link = ctx.Primary.SpeedBps / 1_000_000.0;
            result.LinkMbps = link;
            ctx.SpeedResult = result;
            ctx.SetShared("speed.result", result);

            if (!result.Success)
                return CheckOutcome.Warn("未能完成下载测速",
                    "测速节点不可达不代表网络故障（校园网白名单常见）：请以分段延迟画像与链路协商结果综合判断。"
                    + (result.Error.Length > 0 ? "原因：" + result.Error : ""));

            var details = new List<string>
            {
                $"代表带宽：{result.SpeedText}（峰值 {result.PeakText}）",
                $"测速节点：{result.EndpointLabel}",
                $"链路协商速率：{result.LinkText} · 实测效率：{result.EfficiencyText}",
                $"体验档位：{result.ExperienceBand}",
                $"总下载 {FormatHelper.Bytes(result.BytesTotal)} / 用时 {result.DurationMs / 1000.0:0.#} s",
                // 校园拓扑压力提示：测速流量会经楼宇软路由与科技楼服务器出口，短时占用共享带宽
                $"测速压力说明：限时 {result.DurationMs / 1000.0:0.#} s 多连接下载会短暂占用出口带宽（经楼宇软路由与科技楼服务器），建议避开上课高峰运行。"
            };
            details.AddRange(result.Endpoints.Select(e =>
                $"· {e.Label}：{(e.Success ? $"{e.MbpsAvg:0.#} Mbps（峰值 {e.MbpsPeak:0.#}，{FormatHelper.Bytes(e.BytesTotal)}）" : e.Error)}"));

            ctx.SetShared("speed.low", result.MbpsAvg < 20);
            if (result.MbpsAvg < 5)
                return CheckOutcome.Fail($"实测带宽 {result.SpeedText}，处于很差档位",
                    "结合下方原因分析逐项处置：常见根因为无线干扰、后台大流量占用或出口拥塞；修复后请复测验证。", details.ToArray());
            if (result.MbpsAvg < 20)
                return CheckOutcome.Warn($"实测带宽 {result.SpeedText}，网课可能卡顿",
                    "低于流畅观看 1080P 网课的舒适区间：查看低速原因分析的高嫌疑项，优先处理无线与后台占用。", details.ToArray());
            if (result.LinkMbps > 100 && result.Efficiency < 0.4)
                return CheckOutcome.Warn($"实测仅达链路速率的 {result.EfficiencyText}",
                    "链路本身很快、实测却远低于应有水平：多为出口拥塞、运营商/校园限速或后台占用，见原因分析。", details.ToArray());
            return CheckOutcome.Pass($"实测带宽 {result.SpeedText}（{result.ExperienceBand.Split('（')[0]}）", details.ToArray());
        }
    }

    // ---------------- 6. 后台带宽占用排查 ----------------
    public sealed class BackgroundUsageCheck : CheckBase
    {
        private static readonly string[] HogKeywords =
        {
            "onedrive", "thunder", "xunlei", "baidunetdisk", "baiduyunguanjia", "qqlive",
            "cloudmusic", "qqmusic", "kugou", "kuwo", "douyu", "bili", "qqdownload",
            "idm", "fdm", "utorrent", "qbittorrent", "xlwfp", "netdisk"
        };

        public override string Id => "speed.background";
        public override string Name => "后台带宽占用排查";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var text = await GetNetstatAsync(ctx).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return CheckOutcome.Warn("无法读取活动连接（netstat 失败）", "跳过后台占用排查，不影响其他检查。");

            var byPid = new Dictionary<int, int>();
            foreach (var raw in text.Split('\n'))
            {
                var m = Regex.Match(raw, @"^\s*TCP\s+\S+\s+(\S+)\s+ESTABLISHED\s+(\d+)");
                if (!m.Success) continue;
                var remote = m.Groups[1].Value;
                if (remote.StartsWith("[", StringComparison.Ordinal)) continue;          // IPv6 跳过
                var ip = remote.Split(':')[0];
                if (NetworkProbeHelper.IsPrivateIp(ip) || NetworkProbeHelper.IsLoopbackOrInvalid(ip)) continue; // 只看公网外联
                int pid = int.TryParse(m.Groups[2].Value, out var t) ? t : 0;
                byPid[pid] = byPid.GetValueOrDefault(pid) + 1;
            }

            var names = new Dictionary<int, string>();
            foreach (var pid in byPid.Keys.OrderByDescending(k => byPid[k]).Take(8))
            {
                try { names[pid] = Process.GetProcessById(pid).ProcessName; }
                catch { names[pid] = $"PID {pid}"; }
            }
            int total = byPid.Values.Sum();
            var details = new List<string> { $"公网活动连接：{total} 个" };
            details.AddRange(byPid.OrderByDescending(kv => kv.Value).Take(5)
                .Select(kv => $"· {names.GetValueOrDefault(kv.Key, "PID " + kv.Key)}：{kv.Value} 个连接"));

            var hogs = names.Values
                .Where(n => HogKeywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Where(n => byPid.Any(kv => kv.Value >= 5 && names.GetValueOrDefault(kv.Key) == n))
                .Distinct().ToList();

            if (hogs.Count > 0)
            {
                ctx.SetShared("bg.busy", true);
                return CheckOutcome.Warn($"发现后台下载/同步类程序在占用网络（{string.Join("、", hogs)}）",
                    "网盘同步、下载器、音乐/视频客户端会在后台偷跑流量：上课前建议退出或限制其速度；传递优化 P2P 见专项检查。", details.ToArray());
            }
            if (total > 60)
            {
                ctx.SetShared("bg.busy", true);
                return CheckOutcome.Warn($"公网活动连接偏多（{total} 个）",
                    "大量外联连接通常来自浏览器多标签页、在线协作或自动更新：关闭不用的网页与程序可立竿见影。", details.ToArray());
            }
            return CheckOutcome.Pass("无明显后台带宽占用", details.ToArray());
        }
    }

    // ---------------- 7. TCP 全局参数 ----------------
    public sealed class TcpTuningCheck : CheckBase
    {
        public override string Id => "speed.tcp_tuning";
        public override string Name => "TCP 全局参数（接收窗口自调谐）";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await ProcessRunner.RunAsync("netsh int tcp show global").ConfigureAwait(false);
            if (r.ExitCode != 0) return CheckOutcome.Warn("无法读取 TCP 全局参数", r.Error ?? r.StdErr);
            var text = r.StdOut ?? "";

            string level = "", rss = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var m = Regex.Match(line, @"(?:接收窗口.*调节级别|Receive\s+Window\s+Auto-Tuning\s+Level)\s*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && level.Length == 0) level = m.Groups[1].Value;
                m = Regex.Match(line, @"(?:接收端缩放|接收方缩放|Receive-Side\s+Scaling)[^:\r\n]*[:：]\s*(\S+)", RegexOptions.IgnoreCase);
                if (m.Success && rss.Length == 0) rss = m.Groups[1].Value;
            }
            if (level.Length == 0)
                return CheckOutcome.Warn("无法识别接收窗口自调谐级别", "系统输出格式可能不同，请查看详细信息人工确认。", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(14).ToArray());

            var details = new List<string> { $"接收窗口自动调节级别：{level}", $"接收端缩放（RSS）：{(string.IsNullOrEmpty(rss) ? "未知" : rss)}" };
            bool rssBad = rss.Equals("disabled", StringComparison.OrdinalIgnoreCase) || rss == "禁用";
            bool abnormal = !level.Equals("normal", StringComparison.OrdinalIgnoreCase) && level != "正常";

            if (level.Equals("disabled", StringComparison.OrdinalIgnoreCase) || level == "禁用")
            {
                ctx.SetShared("tcp.autotuning.abnormal", true);
                return CheckOutcome.Fail("TCP 接收窗口自动调节已被关闭",
                    "曾被“网络优化软件/全家桶”关闭的接收窗口自调谐会让高带宽链路跑不满：一键修复可恢复为 normal（无需重启）。", canFix: true, details.ToArray());
            }
            if (abnormal)
            {
                ctx.SetShared("tcp.autotuning.abnormal", true);
                return CheckOutcome.Warn($"TCP 接收窗口级别为 {level}",
                    "非 normal 级别通常是被优化软件改动过，会限制吞吐：一键修复可恢复默认值。", details.ToArray());
            }
            if (rssBad)
                return CheckOutcome.Warn("接收端缩放（RSS）已关闭",
                    "多核并行收包被关闭会限制高带宽下的吞吐：一键修复可一并恢复。", details.ToArray());
            return CheckOutcome.Pass("TCP 全局参数正常", details.ToArray());
        }
    }

    // ---------------- 8. 传递优化 P2P ----------------
    public sealed class DelivOptCheck : CheckBase
    {
        public override string Id => "speed.delivopt";
        public override string Name => "Windows 传递优化 P2P";
        public override CheckCategory Category => CheckCategory.Throughput;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            int mode = -1;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization");
                mode = Convert.ToInt32(key?.GetValue("DODownloadMode", -1) ?? -1);
            }
            catch { }

            var text = await GetNetstatAsync(ctx).ConfigureAwait(false);
            int listen = 0, estab = 0;
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var raw in text.Split('\n'))
                {
                    if (!raw.Contains(":7680", StringComparison.Ordinal)) continue;
                    if (raw.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) listen++;
                    if (raw.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) estab++;
                }
            }

            string modeText = mode switch
            {
                0 => "仅 HTTP（无 P2P）", 1 => "局域网分发", 2 => "组内分发", 3 => "互联网分发",
                100 => "旁路模式（已关闭 P2P）", _ => "未配置（默认：局域网分发）"
            };
            var details = new List<string> { $"组策略分发模式：{modeText}", $"P2P 端口 7680：监听 {listen} · 活动连接 {estab}" };

            if (listen > 0 || estab > 0)
            {
                ctx.SetShared("delivopt.p2p", true);
                return CheckOutcome.Warn("传递优化 P2P 正在活动",
                    "Windows 会向局域网/互联网其他设备上传更新分片，可能占满上行导致全体变慢：一键修复可关闭（写入单个注册表值，可随时还原）。", canFix: true, details.ToArray());
            }
            if (mode == 2 || mode == 3)
                return CheckOutcome.Warn($"传递优化为 {modeText} 模式（当前无活动连接）",
                    "虽暂未活动，但高峰期可能突发占用上行：如需彻底规避可一键修复关闭。", details.ToArray());
            return CheckOutcome.Pass("传递优化未在占用带宽", details.ToArray());
        }
    }

    // ---------------- 共享：netstat 结果缓存（同一次会话复用） ----------------
    internal static async Task<string> GetNetstatAsync(CheckContext ctx)
    {
        var cached = ctx.GetShared<string>("netstat.tcp");
        if (cached != null) return cached;
        var r = await ProcessRunner.RunAsync("netstat -ano -p tcp", 25000).ConfigureAwait(false);
        var text = r.ExitCode == 0 ? r.StdOut : "";
        ctx.SetShared("netstat.tcp", text);
        return text;
    }
}
