using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.Optimize;

/// <summary>
/// 系统优化插件（内置工具真外移第四批）：
/// manifest.navTag = "optimize" 接管宿主左侧「性能优化」导航页；
/// 两服务（内存整理 / 启动项管理）自包含于本程序集，随插件加载即绪，无需后台常驻；
/// 启动项禁用备份沿用宿主同路径（%LocalAppData%\QingShu.Toolkit\startup_disabled.json 等），
/// 内置/插件共用，插件卸载后内置页接管同一份备份记录；
/// 每次导航新建视图与 VM（与内置页 transient 语义一致）；
/// 插件卸载后宿主内置实现自动恢复（内置页保留兜底）。
/// </summary>
public sealed class OptimizePlugin : IQToolkitPlugin, IQToolkitNavPageProvider
{
    private IPluginContext? _ctx;

    public string DisplayName => "系统优化";

    public string NavTitle => "性能优化";

    public Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Log("info", "系统优化插件就绪（已接管「性能优化」导航页；卸载后宿主自动回退内置实现）");
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        var ctx = _ctx;
        _ctx = null;
        try { ctx?.Log("info", "系统优化插件已卸载（宿主内置实现恢复接管）"); }
        catch { } // Detach 后 Log 再入即拒，收尾阶段吞掉
        return Task.CompletedTask;
    }

    public FrameworkElement CreateNavView()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("插件尚未初始化");
        var log = new Action<string, string>((level, message) => ctx.Log(level, message));
        var vm = new OptimizeViewModel(new MemoryOptimizeService(), new StartupListService());
        return new OptimizeView(vm, log);
    }
}
