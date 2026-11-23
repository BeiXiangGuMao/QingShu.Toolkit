using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace QingShu.Toolkit.Views;

/// <summary>
/// 插件导航页宿主壳：外移插件接管左侧导航项后（manifest.navTag + IQToolkitNavPageProvider），
/// CreateNavView 返回的任意 FrameworkElement 由本页承载进 ContentFrame
/// （Frame 只能导航 Page，插件视图通过导航参数挂载）。
/// 注意：OnNavigatedFrom 里绝不主动改 Content（历史版本曾在此置 null）——
/// Frame 换页时新旧内容仍在过渡动画窗口内共存，此刻强拆视觉树会踩原生
/// stowed-exception 崩溃（0xc000027b 布局期 E_INVALIDARG，托管 UnhandledException
/// 捕获不到，用户侧表现即「切换插件闪退」）；本页为 transient + 宿主导航后即清
/// BackStack，旧页整体失去引用，插件视图的回收时机不受影响，无需手动断链。
/// 宿主以 SuppressNavigationTransitionInfo 进入本页（2026-11 全程抑制导航过渡），
/// 过渡动画窗口不存在；本页每次导航全新实例，承载的插件视图亦每次全新重建
/// （transient 契约：视图 Unloaded=VM.Dispose 取消在途操作，2026-11 已回退第 95 条的视图缓存）。
/// </summary>
public sealed class PluginNavHostPage : Page
{
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is FrameworkElement content) Content = content;
        base.OnNavigatedTo(e);
    }
}
