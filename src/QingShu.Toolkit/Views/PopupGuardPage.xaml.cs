using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

/// <summary>弹窗拦截独立管理页：开关 / 白名单 / 最近拦截记录。code-behind 组合根与其他页面一致。</summary>
public sealed partial class PopupGuardPage : Page
{
    public PopupGuardViewModel Vm { get; }

    public PopupGuardPage()
    {
        Vm = App.Services.GetRequiredService<PopupGuardViewModel>();
        InitializeComponent();
    }
}
