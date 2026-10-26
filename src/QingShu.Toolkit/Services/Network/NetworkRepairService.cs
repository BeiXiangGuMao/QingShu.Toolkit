using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Network;

public interface INetworkRepairService
{
    /// <summary>根据诊断结果生成可执行的修复清单。</summary>
    List<RepairAction> BuildPlan(IReadOnlyList<CheckItem> results, CheckContext? ctx);
    /// <summary>执行选中的修复动作：普通动作本进程完成；管理类动作合并为一次 UAC 批处理。</summary>
    Task RunAsync(IReadOnlyList<RepairAction> actions, IProgress<RepairAction>? progress, CancellationToken ct);
}

/// <summary>
/// 一键修复服务（傻瓜模式）。
/// 安全边界：不改系统服务启动类型、不改虚拟内存、不导入注册表文件、不精简系统、不动 Defender；
/// 对服务仅允许「启动未运行的网络基础服务」（应 2026-10 用户需求由只读放开，仅启动、不改启动类型）；
/// 涉及网络配置的操作仅限：DNS 缓存 / ARP / DHCP 续租 / 网卡启停 / Winsock 重置 / 时间同步 / 代理复位，
/// 以及低速专项的单点可逆命令：TCP 自调谐（netsh）/ 传递优化单值（reg add 单值，可删除还原）/
/// 网卡节能托管（PowerShell 单项）/ 网卡重协商（netsh）。全部动作逐条说明、逐项可回滚。
/// </summary>
public sealed class NetworkRepairService : INetworkRepairService
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    public NetworkRepairService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    public List<RepairAction> BuildPlan(IReadOnlyList<CheckItem> results, CheckContext? ctx)
    {
        var plan = new List<RepairAction>();
        var failed = results.Where(r => r.Status is CheckStatus.Fail or CheckStatus.Warning).ToList();
        bool Has(string id) => failed.Any(f => f.Id == id);

        var adapter = ctx?.Primary ?? NetworkProbeHelper.GetPrimaryAdapter();
        var adapterName = adapter?.Name ?? "";
        var portalUrl = ctx?.GetShared<string>("portal.url");
        if (string.IsNullOrEmpty(portalUrl) && Uri.TryCreate(_settings.Current.PortalBaseUrl, UriKind.Absolute, out var pu))
            portalUrl = _settings.Current.PortalBaseUrl;

        if (Has("dns.resolve") || Has("dns.servers") || Has("dns.config"))
            plan.Add(new RepairAction("flushdns", "刷新 DNS 缓存",
                "清除本机 DNS 缓存，解决解析陈旧/污染造成的打不开网页。", false, false,
                new[] { "dns.resolve", "dns.servers", "dns.config" }));

        if (Has("gateway.arp"))
            plan.Add(new RepairAction("arp_clear", "清除 ARP 缓存",
                "删除错误的网关 ARP 表项并重新学习，解决 ARP 欺骗/冲突导致的断网。", true, false,
                new[] { "gateway.arp" }));

        if (Has("ip.config") || Has("ip.dhcp_lease") || Has("gateway.route"))
            plan.Add(new RepairAction("renew_dhcp", $"重新获取 IP 地址（{(adapterName.Length > 0 ? adapterName : "所有网卡")}）",
                "释放当前 IP 并向 DHCP 服务器重新申请，解决租约过期与 APIPA（169.254）问题。", true, false,
                new[] { "ip.config", "ip.dhcp_lease", "gateway.route" }));

        if (Has("physical.link"))
            plan.Add(new RepairAction("enable_adapter", $"启用网络适配器（{adapterName}）",
                "尝试重新启用被禁用或未连接的适配器。", true, false,
                new[] { "physical.link" }));

        if (Has("clock.skew"))
            plan.Add(new RepairAction("resync_clock", "同步系统时间",
                "向时间服务器强制同步系统时钟，修复证书错误与认证失败。", true, false,
                new[] { "clock.skew" }));

        if (Has("proxy.system"))
            plan.Add(new RepairAction("proxy_user", "关闭用户级系统代理",
                "关闭 Internet 选项中的错误代理设置并立即刷新生效（单点设置，不导入任何注册表文件）。", false, false,
                new[] { "proxy.system" }));

        if (Has("proxy.winhttp") || Has("proxy.system"))
            plan.Add(new RepairAction("proxy_winhttp", "重置 WinHTTP 代理",
                "netsh winhttp reset proxy：清除系统服务级代理，恢复直连。", true, false,
                new[] { "proxy.winhttp" }));

        if (Has("stack.winsock") || (Has("dns.resolve") && Has("internet.http")))
            plan.Add(new RepairAction("winsock_reset", "重置 Winsock 目录（需重启生效）",
                "清除第三方 LSP 与损坏的 Winsock 目录，解决“能 ping 通但上不了网”。执行后需重启计算机。", true, true,
                new[] { "stack.winsock" }));

        if (Has("stack.services"))
            plan.Add(new RepairAction("start_net_services", "启动未运行的网络基础服务",
                "启动诊断中未运行的网络基础服务（DHCP/DNS 缓存/网络位置感知等）：仅启动，不改动任何服务的启动类型，"
                + "被禁用的服务会跳过并提示。需要管理员授权。", true, false,
                new[] { "stack.services" }));

        if (Has("dns.servers") && adapter != null && adapter.DnsServers.Count > 0)
            plan.Add(new RepairAction("dns_set_public", $"将 DNS 修改为公共 DNS（{_settings.Current.DnsPreferred}）",
                $"把“{adapterName}”的 DNS 设为 {_settings.Current.DnsPreferred} / {_settings.Current.DnsAlternate}。仅当校园网允许使用外部 DNS 时勾选。", true, true,
                new[] { "dns.servers" }));

        if (Has("portal.detect") || Has("portal.expiry"))
        {
            var st = _settings.Current;
            var autoHint = st.PortalAutoLogin && st.PortalUsername.Length > 0 && st.PortalPassword.Length > 0
                ? "已开启自动登录：一键修复将直接提交认证表单，并用连通性复核验证；失败时自动回退浏览器人工登录。"
                : "调起默认浏览器打开检测到的认证门户；在 设置 → 网络诊断 配置账号并开启自动登录后，可免每次手动登录。";
            var openPortal = new RepairAction("open_portal", "打开城域网认证页面",
                autoHint,
                false, false, new[] { "portal.detect", "portal.expiry" })
            { PortalUrl = portalUrl }; // 优先携带诊断时实际检测到的门户地址
            plan.Add(openPortal);
        }

        // ===== 提速类修复（低速专项；均为单点命令、可逆，遵循安全边界：不导入注册表文件、不改系统服务） =====
        bool SharedTrue(string k) => ctx?.SharedBool(k) == true;

        if (Has("speed.tcp_tuning") && SharedTrue("tcp.autotuning.abnormal"))
            plan.Add(new RepairAction("tcp_autotuning", "恢复 TCP 接收窗口自动调节",
                "netsh 单点设置：autotuninglevel=normal 并启用 RSS。被“网络优化软件”改小/关闭的接收窗口是低速常见原因，恢复后无需重启。", true, false,
                new[] { "speed.tcp_tuning" }));

        if (Has("speed.delivopt") && SharedTrue("delivopt.p2p"))
            plan.Add(new RepairAction("disable_delivopt", "关闭 Windows 传递优化 P2P 分发",
                "写入单个注册表值 DODownloadMode=100（旁路模式，非导入 .reg 文件），阻止系统向局域网/互联网上传更新分片占满上行。还原：删除该值即恢复默认。", true, false,
                new[] { "speed.delivopt" }));

        if (Has("speed.nic_rate") && SharedTrue("nic.powersave"))
            plan.Add(new RepairAction("nic_powersave_off", "关闭网卡节能托管",
                "禁止系统为省电降低网卡性能（AllowComputerToTurnOffDevice=Disabled），避免课中网卡休眠导致的瞬时卡顿。", true, false,
                new[] { "speed.nic_rate" }));

        if (Has("speed.nic_rate") && SharedTrue("nic.rate.low"))
            plan.Add(new RepairAction("adapter_reset", $"网卡重协商（{(adapterName.Length > 0 ? adapterName : "主网卡")}）",
                "禁用并重新启用网卡，重新协商速率与双工；会短暂断网数秒，默认不勾选。", true, true,
                new[] { "speed.nic_rate" }));

        return plan;
    }

    public async Task RunAsync(IReadOnlyList<RepairAction> actions, IProgress<RepairAction>? progress, CancellationToken ct)
    {
        var session = _log.BeginDiagSession();
        session.Write(LogLevel.Info, "repair", $"开始一键修复，共 {actions.Count} 项", actions.Select(a => a.Id).ToArray());

        var normal = actions.Where(a => !a.NeedsAdmin).ToList();
        var admin = actions.Where(a => a.NeedsAdmin).ToList();

        foreach (var action in normal)
        {
            ct.ThrowIfCancellationRequested();
            action.Status = RepairStatus.Running;
            progress?.Report(action);
            try
            {
                var (ok, output) = await RunNormalAsync(action).ConfigureAwait(true);
                action.Status = ok ? RepairStatus.Ok : RepairStatus.Fail;
                action.Output = output;
                session.Write(ok ? LogLevel.Info : LogLevel.Error, "repair", $"[{(ok ? "OK" : "FAIL")}] {action.Title}", output);
            }
            catch (Exception ex)
            {
                action.Status = RepairStatus.Fail;
                action.Output = ex.Message;
            }
            progress?.Report(action);
        }

        if (admin.Count > 0)
        {
            // 合并为一次 UAC
            foreach (var a in admin) { a.Status = RepairStatus.Running; progress?.Report(a); }
            try
            {
                var (script, outLog) = BuildAdminScript(admin);
                session.Write(LogLevel.Info, "repair", $"提权脚本：{script}");
                var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var a in admin)
                {
                    var okLine = lines.FirstOrDefault(l => l.Contains($"##OK##{a.Id}"));
                    var failLine = lines.FirstOrDefault(l => l.Contains($"##FAIL##{a.Id}"));
                    a.Status = okLine != null ? RepairStatus.Ok : failLine != null ? RepairStatus.Fail : RepairStatus.Fail;
                    a.Output = okLine != null ? "执行成功" : $"未在提权脚本输出中找到成功标记（用户可能取消了 UAC）\n{failLine}".Trim();
                    session.Write(a.Status == RepairStatus.Ok ? LogLevel.Info : LogLevel.Error, "repair",
                        $"[{a.Status}] {a.Title}", a.Output);
                    progress?.Report(a);
                }
            }
            catch (Exception ex)
            {
                foreach (var a in admin)
                {
                    a.Status = RepairStatus.Fail;
                    a.Output = $"提权执行失败：{ex.Message}";
                    progress?.Report(a);
                }
            }
        }

        session.Write(LogLevel.Info, "repair", "修复流程结束");
    }

    private async Task<(bool Ok, string Output)> RunNormalAsync(RepairAction action)
    {
        switch (action.Id)
        {
            case "flushdns":
            {
                var r = await ProcessRunner.RunAsync("ipconfig /flushdns").ConfigureAwait(true);
                return (r.ExitCode == 0, r.FullText);
            }
            case "proxy_user":
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
                    key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    NativeMethods.NotifyProxySettingsChanged();
                    return (true, "已关闭用户级系统代理并刷新 WinINet 设置");
                }
                catch (Exception ex) { return (false, ex.Message); }
            }
            case "open_portal":
            {
                // 优先使用诊断时实际检测到的门户地址（可能为 IP 门户），其次用户配置值
                var url = action.PortalUrl ?? _settings.Current.PortalBaseUrl;
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var portal))
                    return (false, "未检测到认证门户且未配置：请手动在浏览器打开认证页，或在 设置 → 网络诊断 中填写门户地址。");

                // 实验性自动登录：配置了账号密码时先尝试直接提交认证表单，失败回退浏览器
                var s = _settings.Current;
                if (s.PortalAutoLogin && s.PortalUsername.Length > 0 && s.PortalPassword.Length > 0)
                {
                    var r = await PortalAutoLoginHelper.TryLoginAsync(
                        portal.ToString(), s.PortalUsername, s.PortalPassword, CancellationToken.None).ConfigureAwait(true);
                    if (r.VerifiedOnline) return (true, r.Message);
                    Process.Start(new ProcessStartInfo(portal.ToString()) { UseShellExecute = true });
                    return (true, $"{r.Message}\n已在默认浏览器打开 {portal} 供人工确认。");
                }
                Process.Start(new ProcessStartInfo(portal.ToString()) { UseShellExecute = true });
                return (true, $"已在默认浏览器打开 {portal}（提示：在 设置 → 网络诊断 配置账号并开启自动登录，可免每次手动登录）");
            }
            default:
                return (false, $"动作 {action.Id} 不属于普通权限类别");
        }
    }

    private (string ScriptPath, string OutLog) BuildAdminScript(IReadOnlyList<RepairAction> actions)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var script = Path.Combine(dir, $"repair-{ts}.cmd");
        var outLog = Path.Combine(dir, $"repair-{ts}.log");

        var adapter = NetworkProbeHelper.GetPrimaryAdapter();
        var adapterName = adapter?.Name ?? "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo [QingShu] 青枢工具箱正在执行修复，请保持窗口开启…");

        foreach (var a in actions)
        {
            switch (a.Id)
            {
                case "arp_clear":
                    sb.AppendLine($"arp -d * >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "renew_dhcp":
                    if (adapterName.Length > 0)
                        sb.AppendLine($"ipconfig /release \"{adapterName}\" >nul 2>&1");
                    sb.AppendLine($"ipconfig /renew >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "enable_adapter":
                    sb.AppendLine($"netsh interface set interface \"{adapterName}\" enable >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "resync_clock":
                    sb.AppendLine($"w32tm /resync /force >nul 2>&1 && echo ##OK##{a.Id} || (net time \\\\localhost /set /y >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id})");
                    break;
                case "proxy_winhttp":
                    sb.AppendLine($"netsh winhttp reset proxy >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "winsock_reset":
                    sb.AppendLine($"netsh winsock reset >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                case "start_net_services":
                    // 仅启动未运行的网络基础服务；被禁用（StartType=Disabled）的跳过计为失败，不改启动类型。
                    // PowerShell 以 $bad 作退出码：全启动成功返回 0 → cmd && 打 OK 标记；任一失败/被禁用 → FAIL
                    sb.AppendLine("powershell -NoProfile -Command \"$ids=@('Dhcp','Dnscache','NlaSvc','netprofm','iphlpsvc','BFE'); $bad=0; foreach($s in $ids){ $svc=Get-Service -Name $s -ErrorAction SilentlyContinue; if($svc -and $svc.Status -ne 'Running'){ if($svc.StartType -eq 'Disabled'){ $bad++ } else { try{ Start-Service -Name $s -ErrorAction Stop; Start-Sleep -Milliseconds 500 }catch{ $bad++ } } } }; exit $bad\" >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "dns_set_public":
                    if (adapterName.Length > 0)
                    {
                        sb.AppendLine($"netsh interface ip set dns \"{adapterName}\" static {_settings.Current.DnsPreferred} primary >nul 2>&1");
                        sb.AppendLine($"netsh interface ip add dns \"{adapterName}\" {_settings.Current.DnsAlternate} index=2 >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    }
                    break;
                case "tcp_autotuning":
                    sb.AppendLine("netsh int tcp set global autotuninglevel=normal >nul 2>&1");
                    sb.AppendLine("netsh int tcp set global rss=enabled >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "disable_delivopt":
                    sb.AppendLine("reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\DeliveryOptimization\" /v DODownloadMode /t REG_DWORD /d 100 /f >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "nic_powersave_off":
                    sb.AppendLine("powershell -NoProfile -Command \"Get-NetAdapterPowerManagement | Where-Object {$_.AllowComputerToTurnOffDevice -eq 'Enabled'} | Set-NetAdapterPowerManagement -AllowComputerToTurnOffDevice Disabled\" >nul 2>&1 && echo ##OK##" + a.Id + " || echo ##FAIL##" + a.Id);
                    break;
                case "adapter_reset":
                    if (adapterName.Length > 0)
                    {
                        sb.AppendLine($"netsh interface set interface \"{adapterName}\" disable >nul 2>&1");
                        sb.AppendLine("timeout /t 3 /nobreak >nul 2>&1");
                        sb.AppendLine($"netsh interface set interface \"{adapterName}\" enable >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    }
                    break;
                default:
                    sb.AppendLine($"echo ##FAIL##{a.Id} 未支持");
                    break;
            }
        }
        sb.AppendLine("echo ##DONE##");
        File.WriteAllText(script, sb.ToString(), System.Text.Encoding.UTF8);
        return (script, outLog);
    }
}
