using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace QingShu.Toolkit.Views;

/// <summary>
/// 插件导航页宿主壳：外移插件接管左侧导航项后（manifest.navTag + IQToolkitNavPageProvider），
/// CreateNavView 返回的任意 FrameworkElement 由本页承载进 ContentFrame
/// （Frame 只能导航 Page，插件视图通过导航参数挂载）。
/// 离开页面即断开内容引用：插件视图的 Unloaded 已随视觉树移除触发（插件自行停 UI 定时器），
/// 这里再断引用帮助宿主侧及时回收。
/// </summary>
public sealed class PluginNavHostPage : Page
{
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is FrameworkElement content) Content = content;
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Content = null;
        base.OnNavigatedFrom(e);
    }
}
