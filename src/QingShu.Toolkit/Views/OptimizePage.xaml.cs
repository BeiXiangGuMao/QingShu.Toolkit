using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

/// <summary>性能优化页。与其他页面一致：code-behind 组合根，解析页面级 VM 供 x:Bind 使用。</summary>
public sealed partial class OptimizePage : Page
{
    public OptimizeViewModel Vm { get; }

    public OptimizePage()
    {
        Vm = App.Services.GetRequiredService<OptimizeViewModel>();
        InitializeComponent();
    }
}
