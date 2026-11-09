using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.Services.Plugins;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; }

    public SettingsPage()
    {
        Vm = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        // 离开页面即释放 VM：退订 UpdateService 单例事件（Transient VM 否则越进越重）
        this.Unloaded += (_, _) => Vm.Dispose();

        // 弹窗拦截被外移插件接管：设置页全局开关失效（拦截运行时已由插件自治），整卡隐藏；
        // 插件卸载后本页重建即恢复显示（Page 每次导航新建，构造时判定即可）
        if (App.Services.GetRequiredService<PluginManager>().IsNavTagLoaded("popupguard"))
        {
            PopupCard.Visibility = Visibility.Collapsed;
        }
    }
}
