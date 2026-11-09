using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.NetworkDiag;

/// <summary>
/// 网络诊断插件（内置工具真外移第五批）：
/// manifest.navTag = "network" 接管宿主左侧「网络诊断」导航页；
/// 诊断引擎（43 项检查 × 全/快/速三模式）、一键修复（管理类合并一次 UAC）、QuestPDF 报告渲染
/// 与硬件探测四服务自包含于本程序集，随插件加载即绪，无需后台常驻；
/// 设置存插件私有 settings.json（Last* 报告表单记忆五项 + 宿主诊断设置只读镜像，
/// 首次启动从宿主 config.json 一次性迁移同名键）；
/// 诊断会话日志与 PDF 报告目录沿用宿主同路径（宿主数据根 + \logs 与 + \reports），内置/插件共用；
/// 每次导航新建视图与 VM（与内置页 transient 语义一致），视图 Unloaded 取消在途诊断/修复；
/// 插件卸载后宿主内置实现自动恢复（内置页保留兜底）。
/// </summary>
public sealed class NetworkDiagPlugin : IQToolkitPlugin, IQToolkitNavPageProvider
{
    private IPluginContext? _ctx;
    private NetworkDiagSettingsStore? _settings;

    public string DisplayName => "网络诊断";

    public string NavTitle => "网络诊断";

    public Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _settings = NetworkDiagSettingsStore.LoadOrCreate(context);
        context.Log("info", "网络诊断插件就绪（已接管「网络诊断」导航页；卸载后宿主自动回退内置实现）");
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        var ctx = _ctx;
        _ctx = null;
        try { ctx?.Log("info", "网络诊断插件已卸载（宿主内置实现恢复接管）"); }
        catch { } // Detach 后 Log 再入即拒，收尾阶段吞掉
        return Task.CompletedTask;
    }

    public FrameworkElement CreateNavView()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("插件尚未初始化");
        var settings = _settings ?? throw new InvalidOperationException("插件设置尚未初始化");
        var log = new Action<string, string>((level, message) => ctx.Log(level, message));
        // 会话日志根与 VM 侧「日志位置」提示同源：宿主数据根 + \logs，与内置页共用同一日志目录
        var logsRoot = Path.Combine(ctx.HostDataRoot, "logs");
        // 诊断/修复/报告/硬件探测服务与 VM/视图全部每次导航新建：宿主内置诊断服务为 DI 单例 +
        // LastContext 并发写入兜底；插件版改为每导航新实例、不共享 LastContext，语义等价
        // 且无并发交叉写风险（旧操作随视图 Unloaded → VM.Dispose 取消，新会话天然隔离）。
        var diag = new NetworkDiagnosticService(settings, log, logsRoot);
        var repair = new NetworkRepairService(settings, log, logsRoot);
        var pdf = new PdfReportService();
        var probe = new HardwareProbeService();
        var vm = new NetworkDiagViewModel(diag, repair, pdf, settings, probe, log, logsRoot);
        return new NetworkDiagView(vm, settings, probe, log);
    }
}
