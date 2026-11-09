using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.DiskCleanup;

/// <summary>
/// 磁盘清理插件（内置工具真外移第三批）：
/// manifest.navTag = "cleanup" 接管宿主左侧「磁盘清理」导航页；
/// 三服务（常规清理引擎 / 微信QQ 久远文件扫描 / 大文件扫描）自包含于本程序集，随插件加载即绪；
/// 设置存插件私有 settings.json（首次启动从宿主 config.json 一次性迁移旧内置实现的同名设置）；
/// 每次导航新建视图与 VM（与内置页 transient 语义一致），视图 Unloaded 取消全部在途扫描/删除；
/// 插件卸载后宿主内置实现自动恢复（内置页保留兜底）。
/// </summary>
public sealed class DiskCleanupPlugin : IQToolkitPlugin, IQToolkitNavPageProvider
{
    private IPluginContext? _ctx;
    private CleanupSettingsStore? _settings;

    public string DisplayName => "磁盘清理";

    public string NavTitle => "磁盘清理";

    public Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _settings = CleanupSettingsStore.LoadOrCreate(context);
        context.Log("info", "磁盘清理插件就绪（已接管「磁盘清理」导航页；卸载后宿主自动回退内置实现）");
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        var ctx = _ctx;
        _ctx = null;
        try { ctx?.Log("info", "磁盘清理插件已卸载（宿主内置实现恢复接管）"); }
        catch { } // Detach 后 Log 再入即拒，收尾阶段吞掉
        return Task.CompletedTask;
    }

    public FrameworkElement CreateNavView()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("插件尚未初始化");
        var settings = _settings ?? throw new InvalidOperationException("插件设置尚未初始化");
        var log = new Action<string, string>((level, message) => ctx.Log(level, message));
        var vm = new CleanupViewModel(
            new CleanupService(log, ctx.DataFolder), new ImScanService(log), new LargeFileScanService(log),
            settings, log);
        return new DiskCleanupView(vm, log);
    }
}
