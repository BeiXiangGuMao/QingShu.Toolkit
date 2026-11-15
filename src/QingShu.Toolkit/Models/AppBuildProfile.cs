namespace QingShu.Toolkit.Models;

/// <summary>
/// 构建形态开关：正式版（stable 分支）在 csproj 定义 STABLE_BUILD，编译期剔除实验性功能
/// （当前清单：①城域网门户自动登录——宿主修复路径与设置组、插件 NetworkDiagCore 同源实现；
/// ②U盘工具——导航入口隐藏 + 插件不自动加载 + 手动加载明确拒绝，内置页与插件同口径）；
/// 预览版（main）不定义该符号，功能全量保留。分支模型与版本推进纪律见 docs/RELEASE.md 第五章。
/// </summary>
public static class AppBuildProfile
{
#if STABLE_BUILD
    /// <summary>false=正式版：实验性功能整体剔除（设置组收起、修复路径不存在）。</summary>
    public const bool ExperimentalEnabled = false;
    /// <summary>false=正式版：U盘工具（实验性）导航入口与插件加载一并剔除。</summary>
    public const bool UsbToolEnabled = false;
#else
    /// <summary>true=预览版：实验性功能全量保留。</summary>
    public const bool ExperimentalEnabled = true;
    /// <summary>true=预览版：U盘工具（实验性）全量保留，导航标注「（实验性）」。</summary>
    public const bool UsbToolEnabled = true;
#endif
}
