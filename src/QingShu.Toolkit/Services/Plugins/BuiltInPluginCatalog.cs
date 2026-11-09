using System;
using System.Collections.Generic;
using System.Linq;
using QingShu.Toolkit.Views;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>
/// 内置功能插件卡（harness 风格）：
/// 全部工具能力（网络诊断 / 磁盘清理 / U盘工具等）以「默认插件」形式在插件管理页统一呈现——
/// 默认安装、默认启用、可停用（停用即隐藏左侧导航与功能入口，随时恢复）。
/// 壳基础设施（插件管理 / 日志 / 设置）不参与插件化，始终可见。
/// 外置插件（plugins\ 目录）与本清单在管理页并列展示，构成完整的插件视图；
/// 后续版本可按 gala 插件模板把内置功能逐个外移为独立程序集（清单 + ALC 隔离加载）。
/// </summary>
public sealed record BuiltInPluginInfo(string Id, string Name, string Description, string NavTag, Type PageType);

public static class BuiltInPluginCatalog
{
    /// <summary>
    /// 内置插件清单（顺序 = 导航顺序）。navTag 稳定不可改（停用状态按 navTag
    /// 持久化到 config.json 的 DisabledBuiltIns，与 MainWindow.xaml 导航项 Tag 一一对应）。
    /// </summary>
    public static IReadOnlyList<BuiltInPluginInfo> All { get; } = new[]
    {
        new BuiltInPluginInfo("qingshu.builtin.dashboard", "仪表盘",
            "整机概览：磁盘 / 内存 / 网络 / 功耗一屏速览，快捷入口与演示数据开关。", "dashboard", typeof(DashboardPage)),
        new BuiltInPluginInfo("qingshu.builtin.network", "网络诊断",
            "城域网认证 / DNS / 测速 / 一键修复 / PDF 报告：校园网体检与排障主力。", "network", typeof(NetworkDiagnosticsPage)),
        new BuiltInPluginInfo("qingshu.builtin.cleanup", "磁盘清理",
            "微信/QQ 久远文件 / 大文件 / 临时目录扫描清理，默认回收站模式可回滚。", "cleanup", typeof(DiskCleanupPage)),
        new BuiltInPluginInfo("qingshu.builtin.optimize", "系统优化",
            "内存整理 / 启动项管理 / 功耗模式，教学机公共机一键减负。", "optimize", typeof(OptimizePage)),
        new BuiltInPluginInfo("qingshu.builtin.popupguard", "弹窗拦截",
            "全局白名单弹窗拦截，上课不再被广告弹窗打断；白名单独立管理。", "popupguard", typeof(PopupGuardPage)),
        new BuiltInPluginInfo("qingshu.builtin.usb", "U盘工具",
            "U盘体检 / 查杀 / 扩容盘检测 / 修复：健康度、坏区、读写速度与 autorun 查杀。", "usb", typeof(UsbToolPage)),
    };

    /// <summary>该导航 tag 是否属于可停用的内置插件。</summary>
    public static bool IsManagedTag(string? tag)
        => tag != null && All.Any(b => b.NavTag == tag);

    /// <summary>内置插件（按 navTag）当前是否被停用。</summary>
    public static bool IsDisabled(IEnumerable<string> disabledTags, string navTag)
        => disabledTags.Contains(navTag);

    /// <summary>给定页面类型对应的内置插件 navTag（非内置页返回 null）。</summary>
    public static string? TagOfPage(Type pageType)
        => All.FirstOrDefault(b => b.PageType == pageType)?.NavTag;

    /// <summary>按停用清单选出第一个仍启用的内置插件（全部停用时返回 null，调用方回退插件管理页）。</summary>
    public static BuiltInPluginInfo? FirstEnabled(IEnumerable<string> disabledTags)
        => All.FirstOrDefault(b => !disabledTags.Contains(b.NavTag));
}
