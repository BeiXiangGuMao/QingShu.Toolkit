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
    Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct);
    CheckContext? LastContext { get; }
}

/// <summary>诊断编排器：按“物理链路 → 硬件 → IP → 网关 → DNS → 互联网 → 内网 → 认证 → 时钟 → 代理 → 系统栈 → 质量”分层执行。</summary>
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
        new NetworkChecks.MtuCheck()
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
        var src = mode == RunMode.Full ? OrderedChecks : OrderedChecks.Where(c => c.InQuickMode).ToArray();
        return src.Select(c => new CheckItem(c.Id, c.Name, c.Category)).ToList();
    }

    public async Task<DiagnosticSessionReport> ExecuteAsync(List<CheckItem> plan, RunMode mode, IProgress<CheckItem>? progress, CancellationToken ct)
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
            Wifi = wifi
        };
        LastContext = ctx;

        session.Write(LogLevel.Info, "session", $"开始诊断：{(mode == RunMode.Full ? "全面诊断" : "快速体检")}，共 {plan.Count} 项", new
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
                    outcome = await check.RunAsync(ctx, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException) { throw; }
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
        session.Write(LogLevel.Info, "session",
            $"诊断完成：通过 {report.PassCount} / 警告 {report.WarnCount} / 未通过 {report.FailCount} / 跳过 {report.SkipCount}");
        return report;
    }
}
