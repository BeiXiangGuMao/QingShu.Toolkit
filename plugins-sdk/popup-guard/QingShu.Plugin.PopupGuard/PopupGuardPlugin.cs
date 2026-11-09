using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.PopupGuard;

/// <summary>
/// 弹窗拦截插件（内置工具真外移第一批）：
/// manifest.navTag = "popupguard" 接管宿主左侧「弹窗拦截」导航页；
/// 拦截引擎随插件加载启动，设置（开关/白名单）存插件私有数据目录 settings.json。
/// 首次启动自动从宿主 config.json 迁移旧内置实现的设置（一次性，幂等）；
/// 插件卸载后宿主内置实现自动恢复运行，设置互不影响。
/// </summary>
public sealed class PopupGuardPlugin : IQToolkitPlugin, IQToolkitNavPageProvider
{
    private IPluginContext? _ctx;
    private PopupGuardStore? _store;
    private PopupGuardEngine? _engine;
    private readonly object _gate = new();

    public string DisplayName => "弹窗拦截";

    public string NavTitle => "弹窗拦截";

    public async Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        _store = PopupGuardStore.LoadOrCreate(context);
        if (_store.Enabled)
        {
            _engine = CreateEngine();
            _engine.Start();
        }
        context.Log("info", $"弹窗拦截插件就绪（{(_store.Enabled ? "已启用" : "已停用")}，白名单 {_store.Whitelist.Count} 项）");
        await Task.CompletedTask;
    }

    public async Task OnUnloadedAsync()
    {
        lock (_gate)
        {
            try { _engine?.Dispose(); } catch { }
            _engine = null;
        }
        await Task.CompletedTask;
    }

    public FrameworkElement CreateNavView() => new PopupGuardView(this);

    // ============ 视图层接口（宿主壳与插件视图经本类交互） ============

    internal PopupGuardEngine? Engine { get { lock (_gate) return _engine; } }

    internal PopupGuardStore Store => _store ?? throw new InvalidOperationException("插件尚未初始化");

    internal IPluginContext Context => _ctx ?? throw new InvalidOperationException("插件尚未初始化");

    /// <summary>开关切换：落盘 + 引擎按需启停（重拍基线，避免误拦切换前窗口）。</summary>
    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            Store.Enabled = enabled;
            Store.Save();
            if (enabled)
            {
                if (_engine == null) _engine = CreateEngine();
                _engine.SetEnabled(true);
            }
            else
            {
                _engine?.SetEnabled(false);
                _engine?.Dispose();
                _engine = null;
            }
        }
        Context.Log("info", $"弹窗拦截{(enabled ? "已开启" : "已关闭")}（插件版）");
    }

    /// <summary>白名单变更落盘（引擎每次扫描时拉最新快照，无需重启）。</summary>
    internal void SaveWhitelist()
    {
        lock (_gate) Store.Save();
    }

    private PopupGuardEngine CreateEngine()
        => new(
            () => new PopupGuardSettings(Store.Enabled, Store.Whitelist),
            (level, message) => Context.Log(level, message));
}

/// <summary>插件设置（settings.json）：开关 + 白名单。原子写（tmp + replace），损坏时回退默认。</summary>
internal sealed class PopupGuardStore
{
    private static readonly string FileName = "settings.json";

    public bool Enabled { get; set; } = true;

    public List<string> Whitelist { get; set; } = DefaultWhitelist();

    [JsonIgnore]
    private string _path = "";

    public static PopupGuardStore LoadOrCreate(IPluginContext ctx)
    {
        var store = new PopupGuardStore
        {
            _path = Path.Combine(ctx.DataFolder, FileName)
        };
        try
        {
            if (File.Exists(store._path))
            {
                var doc = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(store._path));
                if (doc != null)
                {
                    store.Enabled = doc.Enabled;
                    if (doc.Whitelist is { Count: > 0 }) store.Whitelist = doc.Whitelist;
                }
                return store;
            }
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"settings.json 读取失败（按默认设置继续）：{ex.Message}");
        }

        // 首次启动：从宿主 config.json 迁移旧内置实现的一次性设置（幂等：settings.json 建立后不再迁移）
        MigrateFromHost(ctx, store);
        store.Save();
        return store;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new StoreDto
            {
                Enabled = Enabled,
                Whitelist = Whitelist
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"弹窗拦截设置保存失败：{ex.Message}");
        }
    }

    private static void MigrateFromHost(IPluginContext ctx, PopupGuardStore store)
    {
        try
        {
            var cfg = Path.Combine(ctx.HostDataRoot, "config.json");
            if (!File.Exists(cfg))
            {
                ctx.Log("info", "宿主 config.json 不存在，按插件默认白名单初始化");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            bool migratedEnabled = false;
            int migratedWhitelist = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("PopupBlockEnabled", StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    store.Enabled = p.Value.GetBoolean();
                    migratedEnabled = true;
                }
                else if (p.Name.Equals("PopupWhitelist", StringComparison.OrdinalIgnoreCase)
                         && p.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var v in p.Value.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                            list.Add(s);
                    if (list.Count > 0)
                    {
                        store.Whitelist = list;
                        migratedWhitelist = list.Count;
                    }
                }
            }
            ctx.Log("info", $"已从宿主 config.json 迁移弹窗拦截设置（开关：{(migratedEnabled ? (store.Enabled ? "开" : "关") : "未配置")}，白名单 {migratedWhitelist} 项）");
        }
        catch (Exception ex)
        {
            ctx.Log("warn", $"宿主设置迁移失败（按插件默认白名单继续）：{ex.Message}");
        }
    }

    private sealed class StoreDto
    {
        public bool Enabled { get; set; } = true;
        public List<string>? Whitelist { get; set; }
    }

    /// <summary>默认白名单（与宿主内置实现 DefaultWhitelist 同源移植）。</summary>
    internal static List<string> DefaultWhitelist() => new()
    {
        // 系统
        "explorer", "applicationframehost", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "runtimebroker", "taskmgr",
        "systemsettings", "cmd", "conhost", "powershell", "windowsterminal", "notepad",
        "mspaint", "snippingtool", "screenclipping", "calculator", "control", "regedit",
        // 自身
        "qingshu.toolkit",
        // 浏览器（浏览器内弹窗由浏览器自身策略管理）
        "msedge", "chrome", "firefox", "360se", "360chrome", "qqbrowser", "sogouexplorer",
        // 办公与教学常用软件
        "powerpnt", "winword", "excel", "onenote", "outlook", "wps", "et", "wpp",
        "easinote", "easicam", "easipaper", "seewo", "dingding", "dingtalk",
        "qq", "wechat", "weixin", "txplatform", "qqexternal",
        "studentmain", "goprojector", "networkcontrolclient", "wltransport",
        "sunloginclient", "todesk", "anydesk", "rustdesk"
    };
}
