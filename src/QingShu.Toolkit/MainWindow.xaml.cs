using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Power;
using QingShu.Toolkit.Services.Popup;
using QingShu.Toolkit.Services.Update;
using QingShu.Toolkit.Views;
using Windows.UI;

namespace QingShu.Toolkit;

/// <summary>主窗口壳：自定义标题栏（含手动更新按钮）/ 导航 / 状态栏三色功耗标识 / 材质与主题。</summary>
public sealed partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private readonly IPowerService _power;
    private readonly IPopupBlockerService _popup;
    private readonly IUpdateService _update;
    private readonly WindowBackdropService _backdrop = new();
    private bool _navSyncing;

    private static readonly Dictionary<string, Type> PageMap = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["network"] = typeof(NetworkDiagnosticsPage),
        ["cleanup"] = typeof(DiskCleanupPage),
        ["optimize"] = typeof(OptimizePage),
        ["popupguard"] = typeof(PopupGuardPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage)
    };

    public MainWindow()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _power = App.Services.GetRequiredService<IPowerService>();
        _popup = App.Services.GetRequiredService<IPopupBlockerService>();
        _update = App.Services.GetRequiredService<IUpdateService>();

        Title = "青枢工具箱 | QingShu.Toolkit";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragArea);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico")); } catch { }
        VersionText.Text = "v" + AppVersion.DisplayText;

        // 材质：Win11 → Mica；Win10 → DesktopAcrylic；不支持 → 不透明
        if (_settings.Current.UseAcrylicBackdrop && _backdrop.TryAttach(this))
        {
            RootGrid.Background = ResolveRootBackground();
        }
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            _backdrop.UpdateTheme(RootGrid.ActualTheme);
            RefreshRootBackground();
            ApplyCaptionTheme(); // 系统标题栏按钮颜色必须随主题重着色，否则深浅切换后不协调
        };

        ApplyTheme();
        _settings.Changed += () =>
        {
            ApplyTheme();
            RefreshRootBackground();
        };

        _power.Changed += OnPowerChanged;
        UpdatePowerBadge(_power.Current);
        _popup.Blocked += count => DispatcherQueue.TryEnqueue(() =>
            PopupCountText.Text = $"拦截 {count}");
        PopupCountText.Text = $"拦截 {_popup.BlockedCount}";
        _update.StatusChanged += OnUpdateStatus;

        NavigateToPage(typeof(DashboardPage));
        _navSyncing = true;
        SelectNavItem("dashboard");
        _navSyncing = false;
    }

    /// <summary>
    /// 按当前元素实际主题解析根背景刷。
    /// 不得直接 Application.Current.Resources[key]：那只按应用级（=系统）主题解析
    /// ThemeDictionaries，系统深色 + 应用内浅色时会拿到深色变体铺底，
    /// 左侧栏等透明区域随之发黑（历史 bug，经 ResolveThemeBrush 按主题字典显式取值）。
    /// </summary>
    private Brush ResolveRootBackground()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var withBackdrop = _backdrop.Kind != WindowBackdropService.BackdropKind.None
                           && _settings.Current.UseAcrylicBackdrop;
        var key = withBackdrop ? "PageBackgroundAcrylicBrush" : "PageBackgroundBrush";
        return WindowBackdropService.ResolveThemeBrush(key, dark)
               ?? (Brush)Application.Current.Resources[key];
    }

    private void RefreshRootBackground() => RootGrid.Background = ResolveRootBackground();

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _settings.Current.ThemeMode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        // 保险：不依赖 ActualThemeChanged 的事件时序（初始化时主题未变化则事件不触发），
        // 强制同步背板参数与根背景刷，确保浅/深切换后背板与底色同轨。
        _backdrop.UpdateTheme(RootGrid.ActualTheme);
        RefreshRootBackground();
        ApplyCaptionTheme();
    }

    /// <summary>自定义标题栏下，系统最小化/最大化/关闭按钮必须随主题着色，否则深浅切换后观感割裂。</summary>
    private void ApplyCaptionTheme()
    {
        try
        {
            var dark = RootGrid.ActualTheme == ElementTheme.Dark;
            var tb = AppWindow.TitleBar;
            // WinUI 3 投影不含 Windows.UI.Colors（其实现位于 Windows.UI.Xaml.dll，
            // 被 WinUI 3 排除，裸用报 CS0103），统一经本地 FromHex 构造 Windows.UI.Color
            tb.ButtonBackgroundColor = FromHex("#00000000");
            tb.ButtonInactiveBackgroundColor = FromHex("#00000000");
            tb.ButtonForegroundColor = dark ? FromHex("#FFFFFF") : FromHex("#000000");
            tb.ButtonHoverBackgroundColor = dark ? FromHex("#1AFFFFFF") : FromHex("#0F000000");
            tb.ButtonPressedBackgroundColor = dark ? FromHex("#2EFFFFFF") : FromHex("#1F000000");
            tb.ButtonHoverForegroundColor = dark ? FromHex("#FFFFFF") : FromHex("#000000");
            tb.ButtonInactiveForegroundColor = dark ? FromHex("#7F98989D") : FromHex("#7F8E8E93");
        }
        catch { }
    }

    // ================= 导航 =================

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSyncing) return;
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag && PageMap.TryGetValue(tag, out var page))
            NavigateToPage(page);
    }

    public void NavigateToPage(Type pageType)
    {
        if (ContentFrame.CurrentSourcePageType == pageType) return;
        ContentFrame.Navigate(pageType, null, new Microsoft.UI.Xaml.Media.Animation.EntranceNavigationTransitionInfo());
        _navSyncing = true;
        SelectNavItemByPage(pageType);
        _navSyncing = false;
    }

    private void SelectNavItemByPage(Type pageType)
    {
        var kv = PageMap.FirstOrDefault(p => p.Value == pageType);
        if (kv.Key != null) SelectNavItem(kv.Key);
    }

    private void SelectNavItem(string tag)
    {
        var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag)
                   ?? NavView.FooterMenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        if (item != null) NavView.SelectedItem = item;
    }

    // ================= 状态栏 =================

    private void OnPowerChanged(PowerUpdate u) => DispatcherQueue.TryEnqueue(() => UpdatePowerBadge(u.State));

    private void UpdatePowerBadge(PowerUiState state)
    {
        PowerText.Text = state switch
        {
            PowerUiState.Eco => "低功耗",
            PowerUiState.High => "高性能",
            _ => "平衡"
        };
        // 功耗三色仅用于小标识（禁止大面积填充），取当前主题对应色
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        PowerDot.Fill = state switch
        {
            PowerUiState.Eco => new SolidColorBrush(FromHex(dark ? "#34D399" : "#059669")),
            PowerUiState.High => new SolidColorBrush(FromHex(dark ? "#F87171" : "#DC2626")),
            _ => new SolidColorBrush(FromHex(dark ? "#3B82F6" : "#2563EB"))
        };
    }

    /// <summary>解析 #RRGGBB / #AARRGGBB（WinUI 3 的 ColorHelper 无 FromHex，本地实现）。</summary>
    private static Windows.UI.Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        byte a = 255;
        if (s.Length == 8) { a = Convert.ToByte(s[..2], 16); s = s[2..]; }
        return Windows.UI.Color.FromArgb(a,
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s.Substring(2, 2), 16),
            Convert.ToByte(s.Substring(4, 2), 16));
    }

    // ================= 弹窗白名单（右侧状态栏入口 → 独立管理页） =================

    private void BtnWhitelist_Click(object sender, RoutedEventArgs e)
        => NavigateToPage(typeof(PopupGuardPage));

    // ================= 手动更新按钮 =================

    private void BtnUpdate_Click(object sender, RoutedEventArgs e) => _ = HandleUpdateClickAsync();

    private async System.Threading.Tasks.Task HandleUpdateClickAsync()
    {
        var st = _update.Current;
        switch (st.Phase)
        {
            case UpdatePhase.Available:
                await ConfirmAndDownloadAsync(st).ConfigureAwait(true);
                break;
            case UpdatePhase.ReadyToInstall:
                ConfirmInstall();
                break;
            case UpdatePhase.Downloading or UpdatePhase.Checking:
                break;
            default:
                await _update.CheckAsync(manual: true).ConfigureAwait(true);
                break;
        }
    }

    private async System.Threading.Tasks.Task ConfirmAndDownloadAsync(UpdateStatus st)
    {
        var rel = st.Release!;
        var dlg = new ContentDialog
        {
            Title = "发现新版本",
            Content = $"青枢工具箱 v{rel.VersionText} 已发布（当前 v{AppVersion.DisplayText}）。\n\n" +
                      (rel.AssetSize > 0 ? $"安装包大小约 {rel.AssetSize / 1024 / 1024} MB。\n" : "") +
                      "是否现在下载？下载完成后会再次询问是否安装。",
            PrimaryButtonText = "下载更新",
            CloseButtonText = "暂不",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ContentFrame.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await _update.DownloadAsync(rel).ConfigureAwait(true);
    }

    private async void ConfirmInstall()
    {
        var dlg = new ContentDialog
        {
            Title = "准备安装更新",
            Content = "更新包已下载完成。点击“立即安装”后，青枢工具箱将自动退出、完成文件替换并重新启动。",
            PrimaryButtonText = "立即安装",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ContentFrame.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary && _update.Current.DownloadedFile.Length > 0)
            _update.Install(_update.Current.DownloadedFile);
    }

    private void OnUpdateStatus(UpdateStatus st)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateRing.IsActive = st.Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
            UpdateDot.Visibility = st.Phase == UpdatePhase.Available ? Visibility.Visible : Visibility.Collapsed;
            UpdateIcon.Visibility = UpdateRing.IsActive ? Visibility.Collapsed : Visibility.Visible;
            UpdateIcon.Glyph = st.Phase switch
            {
                UpdatePhase.Available => "\uE896",          // Download
                UpdatePhase.ReadyToInstall => "\uE73E",     // CheckMark
                UpdatePhase.UpToDate => "\uE895",           // Sync（更新语义）
                UpdatePhase.Error => "\uE783",              // Error
                _ => "\uE895"
            };
            ToolTipService.SetToolTip(BtnUpdate, st.Phase switch
            {
                UpdatePhase.Available => $"发现新版本 v{st.Release?.VersionText}，点击下载",
                UpdatePhase.ReadyToInstall => "更新包已就绪，点击安装",
                UpdatePhase.Downloading => $"下载中 {st.ProgressPercent:0}%",
                UpdatePhase.Checking => "正在检查更新…",
                UpdatePhase.UpToDate => "已是最新版本，点击重新检查",
                UpdatePhase.Error => "检查更新失败，点击重试",
                _ => "检查更新"
            });
        });
    }
}
