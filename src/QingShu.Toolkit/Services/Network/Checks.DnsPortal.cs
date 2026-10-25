using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 第二层：DNS / 城域网认证 / 校园内网 / 互联网连通（11 项）。
/// </summary>
public static partial class NetworkChecks
{
    private const string NcsiUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string NcsiExpected = "Microsoft Connect Test";

    // ---------------- DNS 配置 ----------------
    public sealed class DnsConfigCheck : CheckBase
    {
        public override string Id => "dns.config";
        public override string Name => "DNS 服务器配置";
        public override CheckCategory Category => CheckCategory.Dns;
        public override bool InQuickMode => true;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var dns = ctx.Primary?.DnsServers ?? new List<string>();
            var details = new List<string> { $"当前 DNS: {(dns.Count > 0 ? string.Join(", ", dns) : "无")}" };

            if (dns.Count == 0)
                return Task.FromResult(CheckOutcome.Fail("未配置任何 DNS 服务器", "将自动修复（启用自动获取 DNS）；也可在设置中手动指定首选 DNS。", canFix: true, details.ToArray()));

            var weird = dns.Where(d => NetworkProbeHelper.IsLoopbackOrInvalid(d)).ToList();
            if (weird.Count > 0)
                return Task.FromResult(CheckOutcome.Fail($"DNS 配置异常（{string.Join(", ", weird)}）", "存在无效 DNS 地址（回环/0.0.0.0），多由流氓软件或误配置导致，将尝试自动修复。", canFix: true, details.ToArray()));

            ctx.SetShared("dns.servers", dns);
            return Task.FromResult(CheckOutcome.Pass($"DNS 配置正常（{string.Join(", ", dns)}）", details.ToArray()));
        }
    }

    // ---------------- 系统域名解析 ----------------
    public sealed class DnsResolveCheck : CheckBase
    {
        public override string Id => "dns.resolve";
        public override string Name => "系统域名解析";
        public override CheckCategory Category => CheckCategory.Dns;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string[] domains = { "www.baidu.com", "www.qq.com" };
            int failed = 0;
            foreach (var d in domains)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(d).ConfigureAwait(false);
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 == null || NetworkProbeHelper.IsLoopbackOrInvalid(v4.ToString()))
                    {
                        failed++;
                        details.Add($"⚠ {d} → {string.Join(", ", addrs.Select(a => a.ToString()))}（结果无效）");
                    }
                    else details.Add($"{d} → {v4}");
                }
                catch (Exception ex)
                {
                    failed++;
                    details.Add($"✗ {d} → 解析失败（{ex.Message}）");
                }
            }
            sw.Stop();
            if (failed == domains.Length)
                return CheckOutcome.Fail("所有测试域名均解析失败", "DNS 服务不可用：将自动刷新 DNS 缓存并探测 DNS 服务器；校园网认证过期也会导致此现象。", canFix: true, details.ToArray());
            if (failed > 0)
                return CheckOutcome.Warn("部分域名解析失败", $"耗时 {sw.ElapsedMilliseconds} ms；持续出现请更换 DNS（设置中可改首选/备用）。", details.ToArray());
            ctx.SetShared("dns.resolve.ok", true);
            return CheckOutcome.Pass("域名解析正常", details.ToArray());
        }
    }

    // ---------------- DNS 服务器逐台探测 ----------------
    public sealed class DnsServerProbeCheck : CheckBase
    {
        public override string Id => "dns.servers";
        public override string Name => "DNS 服务器逐台探测";
        public override CheckCategory Category => CheckCategory.Dns;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var servers = ctx.GetShared<List<string>>("dns.servers") ?? ctx.Primary?.DnsServers ?? new List<string>();
            servers = servers.Distinct().Take(3).ToList();
            if (servers.Count == 0) return CheckOutcome.Skip("无 DNS 服务器可探测。");

            var details = new List<string>();
            int okCount = 0;
            foreach (var s in servers)
            {
                var r = await DnsQueryHelper.ResolveAsync(s, "www.baidu.com", 2000).ConfigureAwait(false);
                if (r.Ok) { okCount++; details.Add($"✓ {s} → {r.AddressText}（{r.ElapsedMs} ms）"); }
                else details.Add($"✗ {s} → {r.Error}（{r.ElapsedMs} ms）");
            }

            if (okCount == 0)
            {
                // 补测公共 DNS 以判断“DNS 服务问题”还是“全网不通”
                var publicProbe = await DnsQueryHelper.ResolveAsync(ctx.Settings.DnsPreferred, "www.baidu.com", 2000).ConfigureAwait(false);
                details.Add($"公共 DNS {ctx.Settings.DnsPreferred} 对照: {(publicProbe.Ok ? "可达（说明是本机 DNS 配置问题）" : "不可达（可能是外网不通）")}");
                return CheckOutcome.Fail("配置的 DNS 服务器全部无响应", "若对照公共 DNS 可达：将自动把 DNS 修复为公共地址；若也不可达：属于外网/出口问题，请看外网连通项结论。", canFix: true, details.ToArray());
            }
            if (okCount < servers.Count)
                return CheckOutcome.Warn("部分 DNS 服务器无响应", "不影响当前使用，但会影响稳定性：建议在设置中替换失效的 DNS 地址。", details.ToArray());
            return CheckOutcome.Pass($"{okCount} 台 DNS 服务器全部响应正常", details.ToArray());
        }
    }

    // ---------------- Hosts 文件劫持 ----------------
    public sealed class HostsCheck : CheckBase
    {
        private static readonly string[] WatchDomains =
        {
            "www.baidu.com", "www.qq.com", "www.msftconnecttest.com", "dns.msftncsi.com",
            "www.msftncsi.com", "login.weixin.qq.com", "wx.qq.com", "www.taobao.com"
        };

        public override string Id => "dns.hosts";
        public override string Name => "Hosts 文件劫持检查";
        public override CheckCategory Category => CheckCategory.Dns;

        public override Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            string path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            try
            {
                if (!File.Exists(path)) return Task.FromResult(CheckOutcome.Pass("Hosts 文件不存在（视为干净）。"));
                var lines = File.ReadAllLines(path);
                var active = lines.Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#")).ToList();
                var details = new List<string> { $"有效条目 {active.Count} 条" };
                var hijacks = new List<string>();
                foreach (var line in active)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var ip = parts[0].ToLowerInvariant();
                    foreach (var dom in parts.Skip(1))
                    {
                        var d = dom.ToLowerInvariant();
                        if (WatchDomains.Any(w => d == w || d.EndsWith("." + w)))
                            hijacks.Add($"{ip}  {d}");
                    }
                }
                if (hijacks.Count > 0)
                    return Task.FromResult(CheckOutcome.Fail($"Hosts 文件劫持了 {hijacks.Count} 个关键域名",
                        "多由病毒/破解工具/流氓软件写入，会导致认证或网页打不开：请用杀毒软件清理后手动清空相关行。", hijacks.ToArray()));
                // 大量条目提示
                if (active.Count > 50)
                    return Task.FromResult(CheckOutcome.Warn($"Hosts 文件存在 {active.Count} 条有效条目", "数量异常偏多，常见于广告过滤工具或批量屏蔽清单；如无印象建议排查。", details.ToArray()));
                return Task.FromResult(CheckOutcome.Pass("Hosts 文件干净", details.ToArray()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(CheckOutcome.Warn("无法读取 Hosts 文件", $"读取失败：{ex.Message}"));
            }
        }
    }

    // ---------------- 城域网认证门户检测 ----------------
    public sealed class PortalDetectCheck : CheckBase
    {
        public override string Id => "portal.detect";
        public override string Name => "城域网认证门户检测";
        public override CheckCategory Category => CheckCategory.PortalAuth;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false, readBody: true).ConfigureAwait(false);
            if (!r.Ok)
                return CheckOutcome.Fail("无法发起网络探测（HTTP 请求失败）", $"探测地址 {NcsiUrl} 不可达：{r.Error}。若前面网关/DNS 均正常，可能是出口防火墙拦截。");

            if (r.StatusCode == 200 && r.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
            {
                ctx.SetShared("portal.detected", false);
                ctx.SetShared("internet.http.ok", true);
                return CheckOutcome.Pass("网络直通：未检测到认证门户，城域网已放行。", $"探测 {NcsiUrl} 返回预期内容（{r.ElapsedMs} ms）");
            }

            // 被重定向或返回了门户页面 → 未认证
            string portalUrl = "";
            if (r.IsRedirect && !string.IsNullOrEmpty(r.Location))
            {
                portalUrl = r.Location;
                if (portalUrl.StartsWith("/", StringComparison.Ordinal))
                {
                    try { portalUrl = new Uri(new Uri(NcsiUrl), portalUrl).ToString(); } catch { }
                }
                var page = await NetworkProbeHelper.HttpAsync(portalUrl, followRedirect: true).ConfigureAwait(false);
                if (page.Ok && !string.IsNullOrEmpty(page.FinalUrl)) portalUrl = page.FinalUrl;
            }
            else if (r.LooksLikeHtml)
            {
                portalUrl = NcsiUrl;
            }

            var keywords = new[] { "认证", "登录", "上网", "portal", "srun", "drcom", "dr.com", "锐捷", "ruijie", "cmcc", "campus", "login", "auth" };
            var snippet = (r.BodySnippet ?? "");
            bool looksPortal = r.LooksLikeHtml && keywords.Any(k => snippet.Contains(k, StringComparison.OrdinalIgnoreCase));

            ctx.SetShared("portal.detected", true);
            ctx.SetShared("portal.url", portalUrl);
            var detail = new List<string>
            {
                $"探测 {NcsiUrl} → HTTP {r.StatusCode}{(r.IsRedirect ? $" 重定向到 {r.Location}" : "")}"
            };
            if (portalUrl.Length > 0 && !portalUrl.StartsWith(NcsiUrl, StringComparison.Ordinal)) detail.Add($"门户地址: {portalUrl}");

            // 常见错误关键字 → 认证过期/被踢
            if (looksPortal && Regex.IsMatch(snippet, @"过期|失效|被挤|离线|超时|expire|offline|kick", RegexOptions.IgnoreCase))
                return CheckOutcome.Fail("城域网认证已过期或被踢下线", "请打开认证门户重新登录；如反复被踢，可能账号在别处登录或欠费，联系信息中心核实。", canFix: true, detail.ToArray());

            return CheckOutcome.Fail("检测到认证门户（当前未通过城域网认证）",
                "一键修复可打开认证页面完成登录；也可在设置中配置门户地址获得更精确的过期判定。", canFix: true, detail.ToArray());
        }
    }

    // ---------------- 认证过期精确判定 ----------------
    public sealed class PortalExpiryCheck : CheckBase
    {
        public override string Id => "portal.expiry";
        public override string Name => "认证状态精确判定";
        public override CheckCategory Category => CheckCategory.PortalAuth;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var s = ctx.Settings;
            var internetOk = ctx.SharedBool("internet.http.ok");

            // 1) 配置了状态接口 → 直接判定
            if (!string.IsNullOrWhiteSpace(s.AuthStatusUrl))
            {
                var r = await NetworkProbeHelper.HttpAsync(s.AuthStatusUrl, followRedirect: true).ConfigureAwait(false);
                if (!r.Ok) return CheckOutcome.Warn("认证状态接口不可达", $"无法访问 {s.AuthStatusUrl}：{r.Error}，请确认地址是否正确。");
                var keywords = (s.AuthOkKeywords ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                var body = r.BodySnippet ?? "";
                if (keywords.Length > 0 && keywords.Any(k => body.Contains(k.Trim(), StringComparison.OrdinalIgnoreCase)))
                    return CheckOutcome.Pass("认证状态：在线（状态接口返回正常）", $"接口 {s.AuthStatusUrl} 命中在线关键字");
                return CheckOutcome.Fail("认证状态接口显示离线/过期", "请打开认证门户重新登录账号；若登录后仍提示离线，联系教体局信息中心核查账号状态。", $"接口 {s.AuthStatusUrl} 未命中在线关键字");
            }

            // 2) 配置了门户地址 → 可达性 + 外网联动判定
            if (!string.IsNullOrWhiteSpace(s.PortalBaseUrl))
            {
                var host = new Uri(s.PortalBaseUrl).Host;
                var tcp = await NetworkProbeHelper.TcpAsync(host, s.PortalBaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80, 3000).ConfigureAwait(false);
                var details = new List<string> { $"门户 {s.PortalBaseUrl} TCP 探测: {(tcp.Ok ? $"可达（{tcp.ElapsedMs} ms）" : tcp.Error)}", $"外网连通: {(internetOk ? "正常" : "不通")}" };

                if (tcp.Ok && !internetOk)
                    return CheckOutcome.Fail("认证已过期：门户可达但外网不通", "典型的城域网认证过期状态，请立即打开门户重新认证。", canFix: true, details.ToArray());
                if (tcp.Ok && internetOk)
                    return CheckOutcome.Pass("门户可达且外网正常，认证状态良好。", details.ToArray());
                if (!tcp.Ok && internetOk)
                    return CheckOutcome.Warn("外网正常但门户不可达", "门户地址可能配置错误或服务临时下线，不影响当前上网；请在设置中核对门户地址。", details.ToArray());
                return CheckOutcome.Fail("门户与外网均不可达", "网络整体中断：请先看前面“物理链路/网关/DNS”各项结论定位断点。", details.ToArray());
            }

            // 3) 未配置
            return CheckOutcome.Skip("未配置城域网认证地址，无法做精确过期判定（可在 设置 → 网络诊断 中填写门户地址或状态接口）。");
        }
    }

    // ---------------- 校园内网 ----------------
    public sealed class IntranetCheck : CheckBase
    {
        public override string Id => "intranet.probe";
        public override string Name => "校园内网连通";
        public override CheckCategory Category => CheckCategory.Intranet;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var host = ctx.Settings.IntranetProbeHost?.Trim();
            if (string.IsNullOrEmpty(host))
                return CheckOutcome.Skip("未配置内网探测地址（设置 → 网络诊断 中可填写教体局 OA / 内网资源地址）。");

            // 支持纯 IP 与域名
            bool isIp = IPAddress.TryParse(host, out _);
            var details = new List<string>();
            if (!isIp)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
                    details.Add($"{host} → {string.Join(", ", addrs.Select(a => a.ToString()).Take(2))}");
                }
                catch (Exception)
                {
                    return CheckOutcome.Warn($"内网域名 {host} 无法解析", "内网 DNS 未能解析该地址：请确认域名与内网 DNS 配置；校外 DNS 服务器也可能无法解析内网域名。", details.ToArray());
                }
            }
            var stats = await NetworkProbeHelper.PingAsync(host, ctx.PingCount, ctx.TimeoutMs).ConfigureAwait(false);
            details.Add($"ICMP: 丢包 {stats.LossPct:0.#}%，平均 {stats.AvgMs:0.#} ms {(stats.Error.Length > 0 ? $"（{stats.Error}）" : "")}");

            if (stats.AllLost)
            {
                var tcp = await NetworkProbeHelper.TcpAsync(host, 80, 3000).ConfigureAwait(false);
                details.Add($"TCP 80 对照: {(tcp.Ok ? $"可达（{tcp.ElapsedMs} ms）" : tcp.Error)}");
                if (tcp.Ok) return CheckOutcome.Pass($"内网 {host} 可达（ICMP 被禁用，TCP 正常）", details.ToArray());
                return CheckOutcome.Fail($"内网 {host} 不可达", "内网资源不通：若城域网/外网正常，多为内网侧问题，请联系信息中心检查目标服务器与内网策略。", details.ToArray());
            }
            if (stats.LossPct > 20)
                return CheckOutcome.Warn($"内网 {host} 丢包偏高（{stats.LossPct:0.#}%）", "内网链路质量差：请联系信息中心排查沿途链路。", details.ToArray());
            return CheckOutcome.Pass($"内网 {host} 连通正常", details.ToArray());
        }
    }

    // ---------------- 外网 ICMP ----------------
    public sealed class InternetIcmpCheck : CheckBase
    {
        public override string Id => "internet.icmp";
        public override string Name => "外网 ICMP 连通";
        public override CheckCategory Category => CheckCategory.Internet;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var details = new List<string>();
            int reachable = 0;
            foreach (var ip in NetworkProbeHelper.PublicDnsServers)
            {
                var stats = await NetworkProbeHelper.PingAsync(ip, Math.Min(3, ctx.PingCount), ctx.TimeoutMs).ConfigureAwait(false);
                if (stats.Received > 0) reachable++;
                details.Add($"{ip}: {(stats.Received > 0 ? $"✓ 平均 {stats.AvgMs:0.#} ms" : $"✗ {stats.Error}")}");
            }
            if (reachable == 0)
                return CheckOutcome.Fail("全部公共 IP 不可达（ICMP）", "不经过域名、直接 ping 公网 IP 也不通，说明出口层中断或屏蔽 ICMP：请结合 HTTP 探测结论综合判断。", details.ToArray());
            ctx.SetShared("internet.icmp.ok", true);
            return CheckOutcome.Pass($"外网 ICMP 正常（{reachable}/{NetworkProbeHelper.PublicDnsServers.Count} 可达）", details.ToArray());
        }
    }

    // ---------------- 外网 HTTP ----------------
    public sealed class InternetHttpCheck : CheckBase
    {
        public override string Id => "internet.http";
        public override string Name => "外网 HTTP 连通";
        public override CheckCategory Category => CheckCategory.Internet;
        public override bool InQuickMode => true;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync(NcsiUrl, followRedirect: false).ConfigureAwait(false);
            if (!r.Ok)
                return CheckOutcome.Fail("HTTP 探测失败", "IP 或可达但 HTTP 被拦截：常见于认证过期（配合认证项）或出口防火墙策略。", $"错误: {r.Error}（{r.ElapsedMs} ms）");

            if (r.IsRedirect)
            {
                ctx.SetShared("portal.detected", true);
                return CheckOutcome.Fail("HTTP 被重定向到认证门户",
                    "城域网认证已过期，所有 HTTP 请求都被引导到登录页，请重新认证（可一键修复打开认证页面）。",
                    canFix: true, $"Location: {r.Location}");
            }
            if (r.StatusCode == 200 && r.BodySnippet.Contains(NcsiExpected, StringComparison.Ordinal))
            {
                ctx.SetShared("internet.http.ok", true);
                return CheckOutcome.Pass("外网 HTTP 正常", $"探测 {NcsiUrl} → 200（{r.ElapsedMs} ms）");
            }
            return CheckOutcome.Warn($"HTTP 响应异常（状态码 {r.StatusCode}）", "内容不符合预期，可能存在透明代理或内容过滤，请结合代理检查项结论。", $"Content-Type: {r.ContentType}");
        }
    }

    // ---------------- HTTPS/TLS ----------------
    public sealed class InternetHttpsCheck : CheckBase
    {
        public override string Id => "internet.https";
        public override string Name => "HTTPS / TLS 连通";
        public override CheckCategory Category => CheckCategory.Internet;

        public override async Task<CheckOutcome> RunAsync(CheckContext ctx, CancellationToken ct)
        {
            var r = await NetworkProbeHelper.HttpAsync("https://www.baidu.com", followRedirect: true, readBody: false).ConfigureAwait(false);
            if (r.Ok && r.StatusCode is >= 200 and < 400)
            {
                ctx.SetShared("internet.https.ok", true);
                return CheckOutcome.Pass("HTTPS 连通正常", $"https://www.baidu.com → {r.StatusCode}（{r.ElapsedMs} ms）");
            }
            var err = r.Error;
            if (err.Contains("Ssl", StringComparison.OrdinalIgnoreCase) || err.Contains("trust", StringComparison.OrdinalIgnoreCase)
                || err.Contains("证书", StringComparison.Ordinal))
                return CheckOutcome.Fail("HTTPS 证书校验失败", "极可能是系统时间错误或被中间人过滤：请先看“系统时钟”检查项并校准时间。", $"错误: {err}");
            if (!ctx.SharedBool("internet.http.ok"))
                return CheckOutcome.Warn("HTTPS 不通（HTTP 亦不通）", "整体断网的一部分：优先解决前面的连通性问题。", $"错误: {err}");
            return CheckOutcome.Fail("HTTP 正常但 HTTPS 不通", "典型于 443 出口被防火墙拦截或 TLS 中间盒异常：请反馈信息中心检查出口策略。", $"错误: {err}");
        }
    }
}
