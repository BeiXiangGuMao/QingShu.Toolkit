using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>插件运行状态快照（管理器 → VM 的只读视图）。</summary>
public sealed class PluginEntry
{
    public string FolderName { get; init; } = "";
    public string Folder { get; init; } = "";
    public PluginManifest? Manifest { get; init; }
    public PluginVerifyState VerifyState { get; init; } = PluginVerifyState.ManifestInvalid;
    public string VerifyError { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Loaded { get; init; }
    /// <summary>运行中且实现了 IQToolkitUiProvider（可在插件页打开自绘面板）。</summary>
    public bool HasUiPanel { get; init; }
    /// <summary>运行中且声明 navTag + 实现 IQToolkitNavPageProvider（接管左侧导航页）。</summary>
    public bool HasNavPage { get; init; }
    /// <summary>接管的导航 Tag（manifest.navTag，未接管为空）。</summary>
    public string NavTag { get; init; } = "";
    public IReadOnlyList<PluginQuickAction> QuickActions { get; init; } = Array.Empty<PluginQuickAction>();
}

/// <summary>一个已注册的快捷动作（名称 + 插件回调）。</summary>
public sealed record PluginQuickAction(string Name, Func<CancellationToken, Task<string?>> Invoke);

/// <summary>插件管理器（扫描 / 验签 / 隔离加载 / 卸载 / 启用禁用持久化）。</summary>
public interface IPluginManager
{
    string PluginsRoot { get; }
    string KeyFingerprint { get; }

    /// <summary>
    /// 运行集变化（任一插件加载成功 / 卸载完成 / 退出收尾开始）后触发。
    /// 注意：在后台线程触发（加载/卸载均为异步管线），订阅方必须调度回 UI 线程。
    /// </summary>
    event Action? RuntimesChanged;
    Task<List<PluginEntry>> ScanAsync();
    /// <summary>加载指定插件，返回面向用户的结果文案（失败抛异常）。</summary>
    Task<string> LoadAsync(string folderName);
    /// <summary>卸载指定插件，返回面向用户的结果文案（失败抛异常）。</summary>
    Task<string> UnloadAsync(string folderName);
    Task SetEnabledAsync(string folderName, bool enabled);
    /// <summary>启动时自动加载全部「已启用且可加载（验签通过或未签名放行）」的插件（逐个隔离失败，绝不让单个插件拖垮启动）。</summary>
    Task AutoLoadEnabledAsync();
    /// <summary>进程退出前的尽力收尾（fire-and-forget，不阻塞退出）。</summary>
    void BeginShutdown();
}

public sealed class PluginManager : IPluginManager
{
    private const int InitTimeoutSeconds = 10;
    private const int ShutdownTimeoutSeconds = 5;
    // internal：PluginContext（同文件独立类）注册快捷动作时引用同一上限，单一事实源
    internal const int MaxQuickActions = 8;

    private readonly ILogService _log;
    private readonly PluginReleaseAccess _releaseAccess;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 加载/卸载/扫描串行化
    private readonly object _sync = new();              // 字典短临界区

    /// <summary>插件安装根：exe 同级 plugins\ 目录（未打包 exe，可写、随目录分发）。</summary>
    public string PluginsRoot { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    public string KeyFingerprint => PluginSigning.Fingerprint;

    /// <summary>运行集变化（任一插件加载成功 / 卸载完成 / 退出收尾开始）后触发（后台线程，订阅方须自行调度回 UI 线程）。</summary>
    public event Action? RuntimesChanged;

    /// <summary>运行中插件：目录名 → 运行时句柄。</summary>
    private readonly Dictionary<string, RuntimePlugin> _runtime = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>启用表：目录名 → 是否启用（缺省 true）。</summary>
    private Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    public PluginManager(ILogService log, PluginReleaseAccess releaseAccess)
    {
        _log = log;
        _releaseAccess = releaseAccess;
        _enabled = LoadEnabledMap();
        _log.App(LogLevel.Debug, "plugin", $"插件管理器就绪（根目录 {PluginsRoot}，官方签名可选校验，指纹 {KeyFingerprint}）");
    }

    // ================= 扫描 =================

    public async Task<List<PluginEntry>> ScanAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return ScanCore();
        }
        finally { _gate.Release(); }
    }

    private List<PluginEntry> ScanCore()
    {
        var list = new List<PluginEntry>();
        try { Directory.CreateDirectory(PluginsRoot); } catch { }
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(PluginsRoot); }
        catch (Exception ex)
        {
            _log.App(LogLevel.Error, "plugin", $"插件目录枚举失败：{ex.Message}");
            return list;
        }
        lock (_sync)
        {
            foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || name.StartsWith(".")) continue;
                var verify = PluginSigning.VerifyFolderCore(dir);
                bool loaded = _runtime.TryGetValue(name, out var rt);
                bool enabled = !_enabled.TryGetValue(name, out var en) || en;
                list.Add(new PluginEntry
                {
                    FolderName = name,
                    Folder = dir,
                    Manifest = verify.Manifest,
                    VerifyState = verify.State,
                    VerifyError = verify.ErrorText,
                    Enabled = enabled,
                    Loaded = loaded,
                    HasUiPanel = rt?.Instance is IQToolkitUiProvider,
                    HasNavPage = rt != null
                                 && !string.IsNullOrWhiteSpace(rt.Manifest.NavTag)
                                 && rt.Instance is IQToolkitNavPageProvider,
                    NavTag = rt?.Manifest.NavTag ?? "",
                    QuickActions = loaded && rt != null
                        ? rt.Context.ActionsSnapshot()
                        : Array.Empty<PluginQuickAction>()
                });
            }
        }
        return list;
    }

    // ================= 完整校验（签名 + 目录一致 + 程序集哈希 + 版本） =================

    private PluginVerifyResult VerifyFull(string folder, string folderName)
    {
        var core = PluginSigning.VerifyFolderCore(folder);
        // 清单不可解析 / 签名存在但验签不符 → 仍拒绝（防篡改红线；想按未签名加载删除 plugin.sig 即可）
        if (core.Manifest == null || core.State == PluginVerifyState.BadSignature) return core;
        var m = core.Manifest;

        if (!string.Equals(m.Id, folderName, StringComparison.OrdinalIgnoreCase))
            return new PluginVerifyResult
            {
                State = PluginVerifyState.IdMismatch,
                ErrorText = $"目录名「{folderName}」与清单 id「{m.Id}」不一致（防冒名检查）。目录名必须与 plugin.json 的 id 完全一致。"
            };

        if (m.MainAssembly.Contains('/') || m.MainAssembly.Contains('\\') || m.MainAssembly.Contains(".."))
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = "manifest.mainAssembly 必须是纯文件名（不含路径）" };
        var dllPath = Path.Combine(folder, m.MainAssembly);
        if (!File.Exists(dllPath))
            return new PluginVerifyResult { State = PluginVerifyState.ManifestInvalid, ErrorText = $"主程序集缺失：{m.MainAssembly}" };

        // 哈希核对：已签名（Ok）必查（锁定签名对象）；未签名仅在清单自行登记哈希时做自一致性检查
        if (core.State == PluginVerifyState.Ok || !string.IsNullOrWhiteSpace(m.MainAssemblySha256))
        {
            var actual = PluginSigning.Sha256File(dllPath);
            if (!string.Equals(actual, m.MainAssemblySha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return new PluginVerifyResult
                {
                    State = PluginVerifyState.HashMismatch,
                    ErrorText = core.State == PluginVerifyState.Ok
                        ? "主程序集与清单登记的 SHA-256 不符（插件文件在签名后被改动）。请用 tools/plugin-sign.ps1 重新签名，或删除 plugin.sig 按未签名加载。"
                        : "主程序集与清单登记的 SHA-256 不符（mainAssemblySha256 与实际文件不一致，请更正清单或删除该字段）。"
                };
        }

        if (!string.IsNullOrWhiteSpace(m.MinHostVersion))
        {
            bool ok = Version.TryParse(m.MinHostVersion.Trim(), out var min)
                      && Version.TryParse(AppVersion.Text, out var local)
                      && local >= min;
            if (!ok)
                return new PluginVerifyResult
                {
                    State = PluginVerifyState.HostTooOld,
                    ErrorText = $"插件要求宿主版本 ≥ {m.MinHostVersion.Trim()}，当前 v{AppVersion.DisplayText}。请先更新青枢工具箱。"
                };
        }
        return core;
    }

    // ================= 加载 =================

    public async Task<string> LoadAsync(string folderName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_runtime.ContainsKey(folderName)) return "插件已在运行。";
            }
            var folder = Path.Combine(PluginsRoot, folderName);
            var verify = VerifyFull(folder, folderName);
            if (!verify.Ok || verify.Manifest == null) throw new InvalidOperationException(verify.ErrorText);
            var manifest = verify.Manifest;

            bool enabledNow;
            lock (_sync) { enabledNow = !_enabled.TryGetValue(folderName, out var en) || en; }
            if (!enabledNow)
                throw new InvalidOperationException("插件处于「停用」状态，请先在插件页勾选启用。");

            var alc = new PluginLoadContext(folder);
            RuntimePlugin? registered = null;
            try
            {
                var asm = alc.LoadFromAssemblyPath(Path.Combine(folder, manifest.MainAssembly));
                var entryType = ResolveEntryType(asm, manifest)
                    ?? throw new InvalidOperationException(
                        $"未找到插件入口：entryType「{manifest.EntryType}」无效，且程序集中没有 IQToolkitPlugin 实现");

                if (Activator.CreateInstance(entryType) is not IQToolkitPlugin instance)
                    throw new InvalidOperationException($"入口类型 {entryType.FullName} 无法实例化（需要公共无参构造）");

                var actions = new List<PluginQuickAction>();
                var context = new PluginContext(manifest.Id, folder, manifest.Permissions, _releaseAccess, actions, _log);
                registered = new RuntimePlugin(manifest, instance, alc, context, new WeakReference(alc));
                lock (_sync) { _runtime[folderName] = registered; }

                using var initCts = new CancellationTokenSource(TimeSpan.FromSeconds(InitTimeoutSeconds));
                var initTask = instance.OnLoadedAsync(context, initCts.Token);
                var done = await Task.WhenAny(initTask, Task.Delay(Timeout.Infinite, initCts.Token)).ConfigureAwait(false);
                if (done != initTask)
                    throw new TimeoutException($"插件初始化超时（{InitTimeoutSeconds}s）：OnLoadedAsync 迟迟未返回");
                await initTask.ConfigureAwait(false); // 观察初始化异常

                _log.App(LogLevel.Info, "plugin", $"插件已加载：{manifest.Id} v{manifest.Version}（{instance.DisplayName}）"
                    + (manifest.NavTag.Length > 0 ? "，接管导航页 " + manifest.NavTag : ""));
                RaiseRuntimesChanged(); // 导航接管状态可能变化（宿主回 UI 线程刷新导航）
                return $"已加载「{instance.DisplayName}」v{manifest.Version}。";
            }
            catch
            {
                // 回滚：初始化失败 = 插件不可信，注册表移除 + 上下文整体回收
                lock (_sync) { _runtime.Remove(folderName); }
                registered?.Context.Detach();
                try { alc.Unload(); } catch { }
                CollectNow();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static Type? ResolveEntryType(Assembly asm, PluginManifest manifest)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(manifest.EntryType))
            {
                var t = asm.GetType(manifest.EntryType.Trim(), throwOnError: false);
                if (t != null && typeof(IQToolkitPlugin).IsAssignableFrom(t)) return t;
                return null; // 明确指定了入口却对不上 → 不做兜底扫描（防歧义）
            }
            return asm.GetExportedTypes().FirstOrDefault(t => !t.IsAbstract && typeof(IQToolkitPlugin).IsAssignableFrom(t));
        }
        catch { return null; }
    }

    /// <summary>
    /// 创建插件自绘 UI 面板（必须在 UI 线程调用）。插件未运行 / 未实现 IQToolkitUiProvider /
    /// 视图创建抛异常时返回 null（异常已记日志）。
    /// </summary>
    public Microsoft.UI.Xaml.FrameworkElement? CreatePanelView(string folderName)
    {
        lock (_sync)
        {
            if (!_runtime.TryGetValue(folderName, out var rt)) return null;
            if (rt.Instance is not IQToolkitUiProvider ui) return null;
            try
            {
                return ui.CreatePanelView();
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Error, "plugin", $"插件 {folderName} 创建面板失败：{ex.Message}");
                return null;
            }
        }
    }

    // ================= 导航页接管（内置工具外移通道） =================

    /// <summary>当前已运行插件接管的导航 Tag 集合（去重；宿主导航用它判断内置页是否被取代）。</summary>
    public IReadOnlyList<string> LoadedNavTags()
    {
        lock (_sync)
        {
            return _runtime.Values
                .Where(rt => !string.IsNullOrWhiteSpace(rt.Manifest.NavTag)
                             && rt.Instance is IQToolkitNavPageProvider)
                .Select(rt => rt.Manifest.NavTag.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>指定导航 Tag 是否已被某个运行中插件接管。</summary>
    public bool IsNavTagLoaded(string navTag)
    {
        if (string.IsNullOrWhiteSpace(navTag)) return false;
        lock (_sync)
        {
            return _runtime.Values.Any(rt =>
                string.Equals(rt.Manifest.NavTag?.Trim(), navTag, StringComparison.Ordinal)
                && rt.Instance is IQToolkitNavPageProvider);
        }
    }

    /// <summary>
    /// 创建被接管导航页的插件视图（必须在 UI 线程调用）。tag 未被接管 / 视图创建抛异常时
    /// 返回 null（异常已记日志，调用方回退内置页）。
    /// </summary>
    public Microsoft.UI.Xaml.FrameworkElement? CreateNavViewFor(string navTag)
    {
        RuntimePlugin? rt;
        lock (_sync)
        {
            rt = _runtime.Values.FirstOrDefault(p =>
                string.Equals(p.Manifest.NavTag?.Trim(), navTag, StringComparison.Ordinal)
                && p.Instance is IQToolkitNavPageProvider);
        }
        if (rt == null) return null;
        try
        {
            return ((IQToolkitNavPageProvider)rt.Instance).CreateNavView();
        }
        catch (Exception ex)
        {
            _log.App(LogLevel.Error, "plugin", $"插件 {rt.Manifest.Id} 创建导航页失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>运行集变化事件触发（后台线程；订阅方自行调度 UI）。吞异常防插件侧 handler 拖垮加载管线。</summary>
    private void RaiseRuntimesChanged()
    {
        try { RuntimesChanged?.Invoke(); } catch (Exception ex)
        {
            _log.App(LogLevel.Warn, "plugin", $"RuntimesChanged 订阅方异常：{ex.Message}");
        }
    }

    // ================= 卸载 =================

    public async Task<string> UnloadAsync(string folderName)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RuntimePlugin? rt;
            lock (_sync)
            {
                if (!_runtime.TryGetValue(folderName, out rt)) return "插件未在运行。";
            }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
                await rt.Instance.OnUnloadedAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Warn, "plugin", $"插件 {folderName} 收尾异常（继续卸载）：{ex.Message}");
            }
            rt.Context.Detach();
            lock (_sync) { _runtime.Remove(folderName); }

            bool fully;
            try
            {
                rt.Alc.Unload();
                CollectNow();
                fully = !rt.AlcRef.IsAlive;
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Warn, "plugin", $"插件 {folderName} 上下文回收异常：{ex.Message}");
                fully = false;
            }
            _log.App(LogLevel.Info, "plugin", $"插件已卸载：{folderName}（上下文回收：{(fully ? "完全" : "待后台释放")}）");
            RaiseRuntimesChanged(); // 被接管导航页回退内置实现
            return fully
                ? $"已卸载「{rt.Instance.DisplayName}」，加载上下文完全释放。"
                : $"已卸载「{rt.Instance.DisplayName}」；个别后台引用将在空闲后回收，不影响使用。";
        }
        finally { _gate.Release(); }
    }

    private static void CollectNow()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        catch { }
    }

    // ================= 启用禁用（持久化） =================

    public async Task SetEnabledAsync(string folderName, bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                _enabled[folderName] = enabled;
            }
            SaveEnabledMap();
            _log.App(LogLevel.Info, "plugin", $"插件 {folderName} 已{(enabled ? "启用" : "停用")}");
        }
        finally { _gate.Release(); }
    }

    private static string StatePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingShu.Toolkit", "plugins-state.json");
    }

    private Dictionary<string, bool> LoadEnabledMap()
    {
        try
        {
            var path = StatePath();
            if (!File.Exists(path)) return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var doc = JsonSerializer.Deserialize<EnabledState>(File.ReadAllText(path));
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (doc?.Enabled != null)
                foreach (var kv in doc.Enabled) map[kv.Key] = kv.Value;
            return map;
        }
        catch { return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveEnabledMap()
    {
        try
        {
            Dictionary<string, bool> copy;
            lock (_sync) { copy = new Dictionary<string, bool>(_enabled, StringComparer.OrdinalIgnoreCase); }
            var json = JsonSerializer.Serialize(new EnabledState { Enabled = copy },
                new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath())!);
            File.WriteAllText(StatePath(), json);
        }
        catch (Exception ex)
        {
            _log.App(LogLevel.Warn, "plugin", $"启用状态保存失败：{ex.Message}");
        }
    }

    private sealed class EnabledState
    {
        public Dictionary<string, bool>? Enabled { get; set; }
    }

    // ================= 启动自动加载 / 退出收尾 =================

    public async Task AutoLoadEnabledAsync()
    {
        try
        {
            var entries = await ScanAsync().ConfigureAwait(false);
            foreach (var e in entries.Where(e => e.Enabled
                         && (e.VerifyState == PluginVerifyState.Ok || e.VerifyState == PluginVerifyState.Unsigned)
                         && !e.Loaded))
            {
                try
                {
                    var msg = await LoadAsync(e.FolderName).ConfigureAwait(false);
                    _log.App(LogLevel.Info, "plugin", $"自动加载 {e.FolderName}：{msg}");
                }
                catch (Exception ex)
                {
                    _log.App(LogLevel.Error, "plugin", $"自动加载失败 {e.FolderName}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.App(LogLevel.Error, "plugin", $"插件自动加载流程异常：{ex.Message}");
        }
    }

    public void BeginShutdown()
    {
        RaiseRuntimesChanged(); // 退出收尾开始：宿主先回退内置导航/服务，插件随后逐个卸载
        _ = Task.Run(async () =>
        {
            List<string> names;
            lock (_sync) { names = _runtime.Keys.ToList(); }
            foreach (var n in names)
            {
                try { await UnloadAsync(n).ConfigureAwait(false); }
                catch { } // 退出阶段尽力而为
            }
        });
    }

    // ================= 运行时句柄 =================

    private sealed class RuntimePlugin
    {
        public RuntimePlugin(PluginManifest manifest, IQToolkitPlugin instance, PluginLoadContext alc,
            PluginContext context, WeakReference alcRef)
        {
            Manifest = manifest;
            Instance = instance;
            Alc = alc;
            Context = context;
            AlcRef = alcRef;
        }

        public PluginManifest Manifest { get; }
        public IQToolkitPlugin Instance { get; }
        public PluginLoadContext Alc { get; }
        public PluginContext Context { get; }
        public WeakReference AlcRef { get; }
    }
}

/// <summary>插件上下文：权限强制 + 日志 + 快捷动作注册。卸载后 Detach，一切再入即拒。</summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly HashSet<string> _perms;
    private readonly IPluginReleaseAccess? _releaseAccess;
    private readonly ILogService _log;
    private readonly List<PluginQuickAction> _actions;
    private readonly object _actionLock = new();
    private volatile bool _attached = true;

    public string PluginId { get; }
    public string PluginFolder { get; }

    public string HostDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit");

    public PluginContext(string pluginId, string pluginFolder, IEnumerable<string> permissions,
        IPluginReleaseAccess? releaseAccess, List<PluginQuickAction> actions, ILogService log)
    {
        PluginId = pluginId;
        PluginFolder = pluginFolder;
        _perms = new HashSet<string>(permissions ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        _releaseAccess = releaseAccess;
        _actions = actions;
        _log = log;
    }

    public IPluginReleaseAccess? Releases
        => HasPermission(PluginPermissions.Releases) ? _releaseAccess : null;

    public bool HasPermission(string permission) => _attached && _perms.Contains(permission);

    public string DataFolder
    {
        get
        {
            if (!HasPermission(PluginPermissions.Storage))
                throw new UnauthorizedAccessException(
                    $"插件未在 plugin.json 声明「{PluginPermissions.Storage}」权限，无法使用数据目录");
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingShu.Toolkit", "plugins-data", PluginId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Log(string level, string message)
    {
        var lv = (level ?? "").Trim().ToLowerInvariant() switch
        {
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Info
        };
        if (message.Length > 800) message = message[..800] + "…";
        _log.App(lv, $"plugin:{PluginId}", message);
    }

    public void RegisterQuickAction(string name, Func<CancellationToken, Task<string?>> action)
    {
        if (!_attached) throw new InvalidOperationException("插件已卸载，不能再注册快捷操作");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("快捷操作名称不能为空", nameof(name));
        if (action == null) throw new ArgumentNullException(nameof(action));
        lock (_actionLock)
        {
            if (_actions.Count >= PluginManager.MaxQuickActions)
                throw new InvalidOperationException($"快捷操作数量已达上限（{PluginManager.MaxQuickActions}）");
            if (_actions.Any(a => a.Name == name))
                throw new InvalidOperationException($"快捷操作重名：{name}");
            _actions.Add(new PluginQuickAction(name.Trim(), action));
        }
    }

    /// <summary>管理器读取当前已注册动作（卸载后返回空）。</summary>
    public IReadOnlyList<PluginQuickAction> ActionsSnapshot()
    {
        if (!_attached) return Array.Empty<PluginQuickAction>();
        lock (_actionLock) { return _actions.ToList(); }
    }

    public void Detach() => _attached = false;
}
