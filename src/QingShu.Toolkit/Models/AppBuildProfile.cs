namespace QingShu.Toolkit.Models;

/// <summary>
/// 构建形态开关：正式版（stable 分支）在 csproj 定义 STABLE_BUILD，编译期剔除实验性功能
/// （当前清单：①城域网门户自动登录——宿主修复路径与设置组、插件 NetworkDiagCore 同源实现；
/// ②U盘工具——导航入口隐藏 + 插件不自动加载 + 手动加载明确拒绝，内置页与插件同口径）；
/// 预览版（main）不定义该符号，功能全量保留。分支模型与版本推进纪律见 docs/RELEASE.md 第五章。
/// 开关为 static readonly 而非 const：固定构建形态下 const 折叠会把「形态反转分支」的
/// 代码判为不可达（CS0162 常驻警告），readonly 运行期等价且无此警告；剔除机制本体是
/// #if STABLE_BUILD 源级守护，不受影响（全库用法均为布尔条件，无 const 语义依赖）。
/// </summary>
public static class AppBuildProfile
{
#if STABLE_BUILD
    /// <summary>false=正式版：实验性功能整体剔除（设置组收起、修复路径不存在）。</summary>
    public static readonly bool ExperimentalEnabled = false;
    /// <summary>false=正式版：U盘工具（实验性）导航入口与插件加载一并剔除。</summary>
    public static readonly bool UsbToolEnabled = false;
#else
    /// <summary>true=预览版：实验性功能全量保留。</summary>
    public static readonly bool ExperimentalEnabled = true;
    /// <summary>true=预览版：U盘工具（实验性）全量保留，导航标注「（实验性）」。</summary>
    public static readonly bool UsbToolEnabled = true;
#endif
}
