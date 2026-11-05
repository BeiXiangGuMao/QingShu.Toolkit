using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

/// <summary>插件管理页：扫描 / 验签状态 / 加载卸载 / 启用停用 / 快捷操作。</summary>
public sealed partial class PluginsPage : Page
{
    public PluginsViewModel Vm { get; }

    public PluginsPage()
    {
        Vm = App.Services.GetRequiredService<PluginsViewModel>();
        InitializeComponent();
        // 与其它页一致的生命周期约定：离开页面即收尾（VM 当前无在途句柄，保持对称防未来扩展遗漏）
        this.Unloaded += (_, _) => Vm.Dispose();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (Vm.Plugins.Count == 0 && !Vm.IsBusy)
            _ = Vm.ScanCommand.ExecuteAsync(null);
    }
}
