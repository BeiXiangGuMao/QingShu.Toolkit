using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Cleanup;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Network;
using QingShu.Toolkit.Services.Optimize;
using QingShu.Toolkit.Services.Power;
using QingShu.Toolkit.Services.Popup;
using QingShu.Toolkit.Services.Reporting;
using QingShu.Toolkit.Services.Update;
using QingShu.Toolkit.ViewModels;
using QingShu.Toolkit.Views;

namespace QingShu.Toolkit;

/// <summary>应用入口：DI 装配 + 全局异常兜底 + 服务启动编排。</summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>仪表盘“开始诊断”跨页标记。</summary>
    public static bool PendingDiagAutostart;

    public App()
    {
        InitializeComponent();
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        var log = Services.GetRequiredService<ILogService>();
        log.App(LogLevel.Info, "app", $"青枢工具箱 v{AppVersion.Text} 启动",
            new { os = Environment.OSVersion.VersionString, x64 = Environment.Is64BitProcess, admin = ElevationHelper.IsCurrentUserAdmin() });
        Services.GetRequiredService<ISettingsService>();
        log.PruneOldLogs();

        Services.GetRequiredService<IPowerService>().Start();
        Services.GetRequiredService<IPopupBlockerService>().Start();
        _ = Services.GetRequiredService<IUpdateService>().AutoCheckOnStartupAsync();

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try { Services?.GetService<ILogService>()?.App(LogLevel.Error, "app", "未处理异常", new { ex = e.Exception.ToString() }); } catch { }
        e.Handled = true;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // 基础服务
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IHardwareProbeService, HardwareProbeService>();
        services.AddSingleton<IPowerService, PowerService>();
        services.AddSingleton<IPopupBlockerService, PopupBlockerService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // 网络诊断
        services.AddSingleton<INetworkDiagnosticService, NetworkDiagnosticService>();
        services.AddSingleton<INetworkRepairService, NetworkRepairService>();
        services.AddSingleton<IPdfReportService, PdfReportService>();

        // 清理
        services.AddSingleton<ICleanupService, CleanupService>();
        services.AddSingleton<IImScanService, ImScanService>();
        services.AddSingleton<ILargeFileScanService, LargeFileScanService>();

        // 优化
        services.AddSingleton<IMemoryOptimizeService, MemoryOptimizeService>();
        services.AddSingleton<IStartupListService, StartupListService>();

        // 视图模型
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<NetworkDiagViewModel>();
        services.AddTransient<CleanupViewModel>();
        services.AddTransient<OptimizeViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<SettingsViewModel>();
    }

    /// <summary>跨页导航入口（供各页面快捷跳转）。</summary>
    public static void NavigateTo(Type pageType)
        => MainWindow?.NavigateToPage(pageType);
}
