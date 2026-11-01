using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
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
    }
}
