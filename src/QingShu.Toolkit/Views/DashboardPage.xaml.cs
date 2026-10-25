using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel Vm { get; }

    public DashboardPage()
    {
        Vm = App.Services.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
    }

    private void PowerModes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // RadioButtons 与 VM.PowerModeIndex 双向绑定；此处仅触发一次保存由 VM 侧处理
    }

    private void GoNetworkAutostart_Click(object sender, RoutedEventArgs e)
    {
        App.PendingDiagAutostart = true;
    }
}
