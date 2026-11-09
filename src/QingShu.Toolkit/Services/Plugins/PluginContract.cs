using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>
/// 插件契约（DeepSeek-harness 风格插件体系）：
/// 插件 = 目录（plugin.json + 主程序集，可选 plugin.sig），宿主校验后以可卸载
/// AssemblyLoadContext 隔离加载，卸载时回收整个上下文。官方签名为可选增强：
/// 未签名插件默认放行（徽章区分）；提供 sig 的目录走完整验签，验签不符仍拒绝（防篡改）。
/// 契约接口全部定义在宿主程序集内，插件工程以宿主 exe 为引用编译，运行期类型同一。
/// </summary>
public interface IQToolkitPlugin
{
    /// <summary>插件显示名（可覆盖 manifest.name，缺省用 manifest）。</summary>
    string DisplayName { get; }

    /// <summary>加载完成回调（宿主在验签与隔离上下文就绪后调用；10s 未返回视为初始化失败）。</summary>
    Task OnLoadedAsync(IPluginContext context, CancellationToken ct);

    /// <summary>卸载回调：插件在此释放句柄/停任务/落盘状态；返回后宿主立即卸载 ALC。</summary>
    Task OnUnloadedAsync();
}

/// <summary>
/// 可选能力：插件自绘 UI 面板（挂载在「插件管理」页的面板区）。
/// 实现 = 插件程序集引用 Microsoft.WindowsAppSDK（与宿主同版本）并实现本接口；
/// 未实现的插件不受影响（可选接口）。Microsoft.UI.Xaml.* 在默认加载上下文中共享，
/// 跨 ALC 类型同一，宿主可直接把返回元素放进视觉树。
/// </summary>
public interface IQToolkitUiProvider
{
    /// <summary>面板标题（显示在面板头部，如「元旦晚会智能分流 · 主控台」）。</summary>
    string PanelTitle { get; }

    /// <summary>
    /// 创建面板视图（宿主 UI 线程调用；每次打开面板调用一次，旧视图由宿主替换丢弃）。
    /// 插件应在元素从视觉树移除（Unloaded）后自行停掉界面定时器等纯 UI 资源；
    /// 后台服务（网络监听等）由 OnUnloadedAsync 统一收尾，与面板开关无关。
    /// </summary>
    FrameworkElement CreatePanelView();
}

/// <summary>宿主提供给插件的运行环境。权限由 manifest.permissions 声明、宿主强制执行。</summary>
public interface IPluginContext
{
    /// <summary>插件标识（= 目录名 = manifest.id，三方一致才允许加载）。</summary>
    string PluginId { get; }

    /// <summary>插件安装目录（只读约定：宿主验签对象，插件不应改写自身文件）。</summary>
    string PluginFolder { get; }

    /// <summary>插件私有数据目录 %LOCALAPPDATA%\QingShu.Toolkit\plugins-data\&lt;id&gt;（自动创建）。</summary>
    string DataFolder { get; }

    /// <summary>宿主日志（app-*.log，category = plugin:&lt;id&gt;；绝不记录 Token 等凭据）。</summary>
    void Log(string level, string message);

    /// <summary>权限查询（"releases" / "storage"；未声明一律拒绝）。</summary>
    bool HasPermission(string permission);

    /// <summary>仓库 Release 访问入口；manifest 未声明 "releases" 权限时返回 null。</summary>
    IPluginReleaseAccess? Releases { get; }

    /// <summary>注册快捷操作：出现在「插件管理」页该插件卡片上，由用户点击触发。</summary>
    void RegisterQuickAction(string name, Func<CancellationToken, Task<string?>> action);
}

/// <summary>
/// 插件可访问的仓库 Release 只读入口（默认指向 settings.UpdateRepo = BeiXiangGuMao/QingShu.Toolkit）。
/// 走与自动更新同一套多线路链与 Token（仅直连请求附带认证，绝不发给加速镜像）。
/// </summary>
public interface IPluginReleaseAccess
{
    /// <summary>列出最新发布（按 GitHub 返回序，含预发布；limit 上限 30）。</summary>
    Task<IReadOnlyList<PluginReleaseInfo>> ListAsync(int limit = 10, CancellationToken ct = default);

    /// <summary>下载指定发布的资产到宿主下载目录，返回本地文件完整路径。</summary>
    Task<string> DownloadAssetAsync(string tagName, string assetName, CancellationToken ct = default);
}

/// <summary>一条 Release 摘要（插件视图，防依赖宿主内部模型）。</summary>
public sealed record PluginReleaseInfo(
    string TagName,
    string Name,
    string Body,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<PluginReleaseAsset> Assets);

/// <summary>Release 内一个资产（附件）。</summary>
public sealed record PluginReleaseAsset(string Name, long SizeBytes, string BrowserDownloadUrl);

/// <summary>权限常量。宿主按声明放行；未声明 = 拒绝（宁可少给）。</summary>
public static class PluginPermissions
{
    /// <summary>允许读取/下载仓库 Releases。</summary>
    public const string Releases = "releases";

    /// <summary>允许使用私有数据目录（当前版本默认放行；保留开关位）。</summary>
    public const string Storage = "storage";
}

// ================= manifest（plugin.json） =================

/// <summary>插件清单。mainAssemblySha256 与签名由 tools/plugin-sign.ps1 在签名时写入/计算。</summary>
public sealed class PluginManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("version")] public string Version { get; set; } = "";

    [JsonPropertyName("author")] public string Author { get; set; } = "";

    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>主程序集文件名（仅文件名，禁止路径分隔符）。</summary>
    [JsonPropertyName("mainAssembly")] public string MainAssembly { get; set; } = "";

    /// <summary>入口类型全名（省略则扫描唯一 IQToolkitPlugin 实现）。</summary>
    [JsonPropertyName("entryType")] public string EntryType { get; set; } = "";

    /// <summary>最低宿主版本（如 1.2.0；低于则拒绝加载）。</summary>
    [JsonPropertyName("minHostVersion")] public string MinHostVersion { get; set; } = "";

    /// <summary>权限声明：releases / storage。</summary>
    [JsonPropertyName("permissions")] public List<string> Permissions { get; set; } = new();

    /// <summary>主程序集 SHA-256（签名工具注入；宿主验签后核对，防清单与程序集错配）。</summary>
    [JsonPropertyName("mainAssemblySha256")] public string MainAssemblySha256 { get; set; } = "";
}

// ================= 验签结果 =================

/// <summary>插件可信状态（列出全部失败原因，UI 直接展示）。</summary>
public enum PluginVerifyState
{
    /// <summary>验签通过（签名 + 程序集哈希 + 版本 + 目录一致性全过）。</summary>
    Ok,

    /// <summary>目录缺少 plugin.json 或不可解析。</summary>
    ManifestInvalid,

    /// <summary>缺少 plugin.sig（未签名）。未签名插件默认放行加载（官方签名为可选增强徽章）；
    /// 若清单自行登记了 mainAssemblySha256，仍会做自一致性哈希核对。</summary>
    Unsigned,

    /// <summary>签名验签失败（非官方密钥或清单被改动）。</summary>
    BadSignature,

    /// <summary>主程序集与清单中登记的 SHA-256 不符。</summary>
    HashMismatch,

    /// <summary>插件要求更高的宿主版本。</summary>
    HostTooOld,

    /// <summary>目录名与 manifest.id 不一致（防冒名）。</summary>
    IdMismatch
}

/// <summary>一次验签的完整结论。</summary>
public sealed class PluginVerifyResult
{
    public PluginVerifyState State { get; init; } = PluginVerifyState.ManifestInvalid;
    public PluginManifest? Manifest { get; init; }
    public string ErrorText { get; init; } = "";
    public bool Ok => State == PluginVerifyState.Ok;
}
