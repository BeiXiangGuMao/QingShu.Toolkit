namespace QingShu.Toolkit.Models;

/// <summary>
/// 构建形态开关：正式版（stable 分支）在 csproj 定义 STABLE_BUILD，编译期剔除实验性功能
/// （当前清单仅一项：城域网门户自动登录——宿主修复路径与设置组、插件 NetworkDiagCore 同源实现）；
/// 预览版（main）不定义该符号，功能全量保留。分支模型与版本推进纪律见 docs/RELEASE.md 第五章。
/// </summary>
public static class AppBuildProfile
{
#if STABLE_BUILD
    /// <summary>false=正式版：实验性功能整体剔除（设置组收起、修复路径不存在）。</summary>
    public const bool ExperimentalEnabled = false;
#else
    /// <summary>true=预览版：实验性功能全量保留。</summary>
    public const bool ExperimentalEnabled = true;
#endif
}
