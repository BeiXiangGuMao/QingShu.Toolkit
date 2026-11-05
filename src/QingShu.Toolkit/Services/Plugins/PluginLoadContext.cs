using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>
/// 插件隔离加载上下文（collectible）：
/// · 插件目录内的 *.dll 优先从本目录加载（插件自带依赖与宿主同名也不冲突）；
/// · 其余全部回退默认上下文 —— 宿主 exe（含插件契约接口）、WinUI、BCL 共享单例，
///   保证 typeof(IQToolkitPlugin)（插件侧引用宿主 exe 编译）与宿主侧同一类型。
/// · isCollectible=true 支持卸载：宿主调用 Unload() 后无存活引用即可整体回收。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _folder;

    public PluginLoadContext(string folder)
        : base(name: $"qingshu-plugin:{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}",
               isCollectible: true)
    {
        _folder = folder;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName.Name)) return null;
        // 系统程序集（System.* / Microsoft.* / netstandard 等）一律走默认上下文，
        // 防止插件目录里误放的假 BCL dll 劫持运行时
        var name = assemblyName.Name;
        if (name.StartsWith("System", StringComparison.Ordinal)
            || name.StartsWith("Microsoft", StringComparison.Ordinal)
            || name.StartsWith("WindowsBase", StringComparison.Ordinal)
            || name == "netstandard" || name == "mscorlib")
        {
            return null;
        }
        var localPath = Path.Combine(_folder, name + ".dll");
        if (!File.Exists(localPath)) return null;
        try { return LoadFromAssemblyPath(localPath); }
        catch (BadImageFormatException) { return null; } // 架构不符时回退默认上下文再尝试
    }

    /// <summary>非程序集资源（原生 dll 等）：仅在插件目录内解析。</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidate = Path.Combine(_folder, unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? unmanagedDllName
            : unmanagedDllName + ".dll");
        return File.Exists(candidate) ? LoadUnmanagedDllFromPath(candidate) : IntPtr.Zero;
    }
}
