using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Network;

public interface INetworkDiagnosticService
{
    /// <summary>生成一次运行的检查计划（全新 CheckItem 实例）。</summary>
    List<CheckItem> CreatePlan(RunMode mode);
    /// <summary>执行计划：逐项更新 CheckItem 状态（ObservableObject，UI 自动刷新），并写入会话日志。</summary>
    Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct,
        IProgress<SpeedProgress>? speedProgress = null);
    CheckContext? LastContext { get; }
}

/// <summary>诊断编排器：按“物理链路 → 硬件 → IP → 网关 → DNS → 互联网 → 内网 → 认证 → 时钟 → 代理 → 系统栈 → 质量 → 测速吞吐”分层执行。</summary>
public sealed class NetworkDiagnosticService : INetworkDiagnosticService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public CheckContext? LastContext { get; private set; }

    private static readonly ICheck[] AllChecks =
    {
        new NetworkChecks.PhysicalLinkCheck(),
        new NetworkChecks.NicHardwareCheck(),
        new NetworkChecks.WifiSignalCheck(),
        new NetworkChecks.IpConfigCheck(),
        new NetworkChecks.DhcpLeaseCheck(),
        new NetworkChecks.GatewayReachableCheck(),
        new NetworkChecks.ArpCheck(),
        new NetworkChecks.RouteCheck(),
        new NetworkChecks.DnsConfigCheck(),
        new NetworkChecks.DnsResolveCheck(),
        new NetworkChecks.DnsServerProbeCheck(),
        new NetworkChecks.HostsCheck(),
        new NetworkChecks.InternetIcmpCheck(),
        new NetworkChecks.InternetHttpCheck(),
        new NetworkChecks.InternetHttpsCheck(),
        new NetworkChecks.IntranetCheck(),
        new NetworkChecks.PortalDetectCheck(),
        new NetworkChecks.PortalExpiryCheck(),
        new NetworkChecks.ClockSkewCheck(),
        new NetworkChecks.ProxyCheck(),
        new NetworkChecks.WinHttpProxyCheck(),
        new NetworkChecks.VpnAdaptersCheck(),
        new NetworkChecks.ServicesCheck(),
        new NetworkChecks.FirewallCheck(),
        new NetworkChecks.WinsockCheck(),
        new NetworkChecks.LatencyQualityCheck(),
        new NetworkChecks.MtuCheck(),
        new NetworkChecks.NicRateCheck(),
        new NetworkChecks.WifiPhyCheck(),
        new NetworkChecks.DnsPerfCheck(),
        new NetworkChecks.PathProfileCheck(),
        new NetworkChecks.DownloadSpeedCheck(),
        new NetworkChecks.BackgroundUsageCheck(),
        new NetworkChecks.TcpTuningCheck(),
        new NetworkChecks.DelivOptCheck(),
        // 疑难排查层（罕见/边缘问题，仅全面诊断运行）
        new NetworkChecks.Ipv6Check(),
        new NetworkChecks.DnsHijackCheck(),
        new NetworkChecks.GatewayLossCheck(),
        new NetworkChecks.RouteConflictCheck(),
        new NetworkChecks.ProxyDiffCheck(),
        new NetworkChecks.Port53Check(),
        new NetworkChecks.AuthClientCheck(),
        new NetworkChecks.StackSelfTestCheck()
    };

    /// <summary>逐项硬超时（秒）：坏网环境下慢超时检查不再拖住整个诊断列表（防「待检查」滞留）。</summary>
    private static readonly Dictionary<string, int> TimeoutOverrides = new()
    {
        ["speed.download"] = 90, ["portal.detect"] = 40, ["portal.expiry"] = 40,
        ["quality.mtu"] = 30, ["rare.gatewayloss"] = 30
    };

    private static int TimeoutFor(string id) => TimeoutOverrides.TryGetValue(id, out var s) ? s : 25;

    /// <summary>低速专项计划：8 项专项检查 + 与低速强相关的既有检查（避免重复实现，复用既有证据）。</summary>
    private static readonly string[] SpeedPlanIds =
    {
        "speed.nic_rate", "speed.wifi_phy", "speed.dns_perf", "speed.path_profile",
        "speed.download", "speed.background", "speed.tcp_tuning", "speed.delivopt",
        "physical.link", "wifi.signal", "gateway.reachable",
        "dns.config", "dns.resolve", "dns.servers",
        "proxy.system", "proxy.winhttp", "vpn.adapters",
        "quality.latency", "quality.mtu",
        "internet.http", "internet.https"
    };

    private static readonly ICheck[] OrderedChecks =
        AllChecks.OrderBy(c => (int)c.Category).ToArray();

    public NetworkDiagnosticService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    public List<CheckItem> CreatePlan(RunMode mode)
    {
        var src = mode switch
        {
            RunMode.Full => OrderedChecks,
            RunMode.Quick => OrderedChecks.Where(c => c.InQuickMode),
            RunMode.Speed => OrderedChecks.Where(c => SpeedPlanIds.Contains(c.Id)),
            _ => OrderedChecks
        };
        return src.Select(c => new CheckItem(c.Id, c.Name, c.Category)).ToList();
    }

    public async Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct,
        IProgress<SpeedProgress>? speedProgress = null)
    {
        var session = _log.BeginDiagSession();
        var report = new DiagnosticSessionReport { StartedAt = DateTime.Now, Mode = mode };

        // 构建共享上下文
        var adapters = NetworkProbeHelper.GetAllAdapters();
        var primary = NetworkProbeHelper.GetPrimaryAdapter();
        var wifi = primary is { IsWireless: true } ? await NetworkProbeHelper.GetWifiInfoAsync().ConfigureAwait(true) : null;
        var ctx = new CheckContext
        {
            Settings = _settings.Current,
            Log = session,
            Ct = ct,
            Adapters = adapters,
            Primary = primary,
            Wifi = wifi,
            SpeedProgress = speedProgress
        };
        LastContext = ctx;

        session.Write(LogLevel.Info, "session", $"开始诊断：{report.ModeText}，共 {plan.Count} 项", new
        {
            host = Environment.MachineName,
            adapter = primary?.Name,
            ip = primary?.IpV4,
            gateway = primary?.Gateway
        });

        // 检查实现索引
        var impl = OrderedChecks.ToDictionary(c => c.Id, c => c);

        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = plan[i];
                if (!impl.TryGetValue(item.Id, out var check))
                {
                    item.Status = CheckStatus.Skipped;
                    continue;
                }

                item.Status = CheckStatus.Running;
                item.Summary = "正在检查…";
                progress?.Report(item);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                CheckOutcome outcome;
                try
                {
                    // 逐项硬超时：HttpClient 默认 100s / ping 长等待会把坏网环境的诊断拖住几分钟，
                    // 这里按项限时强制收尾，超时项给出结论后继续下一项（遗留任务随取消令自然终止）。
                    var timeoutSec = TimeoutFor(item.Id);
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    linked.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                    var runTask = check.RunAsync(ctx, linked.Token);
                    var done = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec + 3))).ConfigureAwait(true);
                    if (done == runTask)
                        outcome = await runTask.ConfigureAwait(true); // 正常完成；异常由此抛出统一接住
                    else
                        outcome = CheckOutcome.Fail($"检查超时（超过 {timeoutSec} 秒已强制中止）",
                            "该检查等待响应过久（多为网络无应答），已中止此项以保证其余检查不被卡住；请结合网关/DNS/外网连通项结论定位。",
                            $"超时阈值：{timeoutSec} 秒");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException)
                {
                    outcome = CheckOutcome.Fail("检查超时（已强制中止）", "该检查内部无响应被强制中止；其余检查不受影响。");
                }
                catch (Exception ex)
                {
                    outcome = CheckOutcome.Fail($"检查执行异常：{ex.Message}", "该检查项内部出现未预期错误，已跳过；不影响其他检查项。");
                    session.Write(LogLevel.Error, $"check.{item.Id}", "检查异常", new { ex = ex.ToString() });
                }
                sw.Stop();

                item.DurationMs = sw.ElapsedMilliseconds;
                item.Status = outcome.Status;
                item.Summary = outcome.Summary;
                item.Suggestion = outcome.Suggestion;
                item.CanAutoFix = outcome.CanAutoFix;
                item.Details = outcome.Details;

                session.Write(LogLevel.Info, $"check.{item.Id}", $"[{outcome.Status}] {outcome.Summary}",
                    new { ms = sw.ElapsedMilliseconds, suggestion = outcome.Suggestion, details = outcome.Details });
                progress?.Report(item);
                report.Results.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var item in plan.Where(p => p.Status is CheckStatus.Pending or CheckStatus.Running))
            {
                item.Status = CheckStatus.Skipped;
                item.Summary = "已取消";
            }
            session.Write(LogLevel.Warn, "session", "诊断被用户取消");
        }

        report.FinishedAt = DateTime.Now;

        // 低速专项：归并证据 → 嫌疑原因清单
        if (mode == RunMode.Speed && !plan.All(p => p.Status == CheckStatus.Skipped))
        {
            try
            {
                report.SlowCauses = SlowSpeedAnalyzer.Analyze(ctx, report.Results, ctx.SpeedResult);
                session.Write(LogLevel.Info, "slow",
                    $"低速原因分析完成：识别 {report.SlowCauses.Count} 项嫌疑",
                    report.SlowCauses.Select(c => $"[{c.Level}] {c.Title}").ToArray());
            }
            catch (Exception ex)
            {
                session.Write(LogLevel.Error, "slow", "低速原因分析异常", new { ex = ex.ToString() });
            }
        }

        session.Write(LogLevel.Info, "session",
            $"诊断完成：通过 {report.PassCount} / 警告 {report.WarnCount} / 未通过 {report.FailCount} / 跳过 {report.SkipCount}");
        return report;
    }
}
