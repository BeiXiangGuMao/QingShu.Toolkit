using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.UsbTool;

/// <summary>
/// U盘工具插件（内置工具真外移第二批）：
/// manifest.navTag = "usb" 接管宿主左侧「U盘工具」导航页；
/// 四服务（探查/健康检测/查杀/修复）自包含于本程序集，随插件加载即绪，无需后台常驻；
/// 每次导航新建视图与 VM（与内置页 transient 语义一致），视图 Unloaded 取消在途破坏性操作；
/// 插件卸载后宿主内置实现自动恢复（内置页保留兜底）。
/// </summary>
public sealed class UsbToolPlugin : IQToolkitPlugin, IQToolkitNavPageProvider
{
    private IPluginContext? _ctx;

    public string DisplayName => "U盘工具";

    public string NavTitle => "U盘工具";

    public Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Log("info", "U盘工具插件就绪（已接管「U盘工具」导航页；卸载后宿主自动回退内置实现）");
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        var ctx = _ctx;
        _ctx = null;
        try { ctx?.Log("info", "U盘工具插件已卸载（宿主内置实现恢复接管）"); }
        catch { } // Detach 后 Log 再入即拒，收尾阶段吞掉
        return Task.CompletedTask;
    }

    public FrameworkElement CreateNavView()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("插件尚未初始化");
        var log = new Action<string, string>((level, message) => ctx.Log(level, message));
        var vm = new UsbToolViewModel(
            new UsbProbeService(), new UsbHealthService(), new UsbScanService(), new UsbRepairService(), log);
        return new UsbToolView(vm);
    }
}
