using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using QingShu.Plugin.GalaDispatch.Gala;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.GalaDispatch;

/// <summary>
/// 元旦晚会智能分流插件：
/// 主控端（礼堂控制台）监听内网端口，汇聚各客户端（舞台/投票/直播终端）上报的链路指标，
/// 对「路由压力 × 服务器压力」做综合评分并按滞回策略远程切换线路；
/// 主控面板实时绘制拓扑（节点压力着色 + 链路流量），支持远程操控（切换线路/优先级/即时采集）。
/// 仅限城域网内网使用：准入按 IP 段前缀白名单校验，可选配对码。
/// </summary>
public sealed class GalaDispatchPlugin : IQToolkitPlugin, IQToolkitUiProvider
{
    private const string ConfigFileName = "gala-topology.json";

    private IPluginContext? _ctx;
    private GalaTopologyConfig? _cfg;
    private GalaMaster? _master;
    private GalaAgent? _agent;
    private readonly object _gate = new();

    public string DisplayName => "元旦晚会智能分流";

    public string PanelTitle => "元旦晚会智能分流 · 主控台 / 客户端";

    public async Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Log("info", "元旦晚会智能分流插件加载");

        // 拓扑配置：首次运行生成占位模板（真实拓扑资料到位后由用户编辑覆盖）
        LoadOrCreateConfig();

        context.RegisterQuickAction("打开配置目录", _ =>
        {
            var dir = context.DataFolder;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                return Task.FromResult<string?>($"已打开配置目录：{dir}");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"打开目录失败：{ex.Message}（配置文件：{Path.Combine(dir, ConfigFileName)}）");
            }
        });

        context.RegisterQuickAction("重载拓扑配置", _ =>
        {
            try
            {
                ReloadConfig();
                return Task.FromResult<string?>(
                    $"已重载：节点 {_cfg!.Nodes.Count} · 链路 {_cfg.Links.Count} · 路由规则 {_cfg.Routes.Count} · 监听端口 {_cfg.Settings.ListenPort}。运行中的主控端将按新配置重启。");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"重载失败：{ex.Message}");
            }
        });

        context.RegisterQuickAction("导出事件日志", _ =>
        {
            try
            {
                var folder = context.DataFolder;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"gala-events-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                var lines = new List<string>();
                try
                {
                    var m = EnsureMaster().Snapshot();
                    lines.Add($"===== 主控端事件（监听中：{(m.Running ? "是" : "否")}）=====");
                    lines.AddRange(m.Events);
                }
                catch (Exception ex) { lines.Add($"主控事件读取失败：{ex.Message}"); }
                lines.Add("");
                try
                {
                    var a = EnsureAgent().Snapshot();
                    lines.Add($"===== 客户端事件（运行中：{(a.Running ? "是" : "否")}）=====");
                    lines.AddRange(a.Events);
                }
                catch (Exception ex) { lines.Add($"客户端事件读取失败：{ex.Message}"); }
                File.WriteAllLines(path, lines);
                return Task.FromResult<string?>($"已导出事件日志：{path}（共 {lines.Count} 行）");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"导出失败：{ex.Message}");
            }
        });

        await Task.CompletedTask;
    }

    public async Task OnUnloadedAsync()
    {
        try
        {
            if (_master != null) await _master.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { } // 卸载收尾尽力而为（宿主 5s 兜底）
        try
        {
            if (_agent != null) await _agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { }
        _master = null;
        _agent = null;
        _cfg = null;
        _ctx?.Log("info", "元旦晚会智能分流插件已停止全部服务");
        _ctx = null;
    }

    // ================= 面板（宿主 UI 线程调用） =================

    public FrameworkElement CreatePanelView()
    {
        if (_cfg == null) LoadOrCreateConfig();
        return new GalaPanelView(this);
    }

    // ================= 面板/调度共用状态访问（线程安全） =================

    internal GalaTopologyConfig Config
    {
        get { lock (_gate) { return _cfg ?? new GalaTopologyConfig(); } }
    }

    internal GalaMaster EnsureMaster()
    {
        lock (_gate)
        {
            if (_master == null)
            {
                var ctx = _ctx;
                _master = new GalaMaster(Config, msg => ctx?.Log("info", msg));
            }
            return _master;
        }
    }

    internal GalaAgent EnsureAgent()
    {
        lock (_gate)
        {
            if (_agent == null)
            {
                var ctx = _ctx ?? throw new InvalidOperationException("插件上下文未就绪（插件未加载或已卸载）");
                _agent = new GalaAgent(msg => ctx.Log("info", msg), ctx.DataFolder);
            }
            return _agent;
        }
    }

    internal void ReloadConfig()
    {
        var folder = _ctx?.DataFolder ?? throw new InvalidOperationException("插件上下文未就绪");
        var path = Path.Combine(folder, ConfigFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"配置文件不存在：{path}");
        var cfg = JsonSerializer.Deserialize<GalaTopologyConfig>(File.ReadAllText(path))
                  ?? throw new InvalidDataException("配置文件格式不正确（反序列化为空）");
        GalaTopologyConfig.Normalize(cfg);
        lock (_gate) { _cfg = cfg; }
        _ctx?.Log("info", $"拓扑配置已重载：节点 {cfg.Nodes.Count} · 路由规则 {cfg.Routes.Count}");

        // 主控端在运行 → 按新配置重启。旧 GalaMaster 实例持有的是旧配置快照，
        // 必须先停旧实例、摘除引用后再重建（否则 EnsureMaster 返回旧实例，重启等于没换配置）
        GalaMaster? master;
        bool wasRunning;
        lock (_gate) { master = _master; wasRunning = master?.IsRunning == true; }
        if (wasRunning && master != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await master.StopAsync().ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (ReferenceEquals(_master, master)) _master = null;
                    }
                    var fresh = EnsureMaster();
                    fresh.Start();
                    _ctx?.Log("info", "主控端已按新拓扑配置重启");
                }
                catch (Exception ex) { _ctx?.Log("warn", $"按新配置重启主控失败：{ex.Message}"); }
            });
        }
    }

    private void LoadOrCreateConfig()
    {
        var folder = _ctx?.DataFolder;
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            var path = Path.Combine(folder!, ConfigFileName);
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<GalaTopologyConfig>(File.ReadAllText(path));
                if (cfg != null)
                {
                    GalaTopologyConfig.Normalize(cfg);
                    lock (_gate) { _cfg = cfg; }
                    _ctx?.Log("info", $"拓扑配置已载入：节点 {cfg.Nodes.Count} · 路由规则 {cfg.Routes.Count}");
                    return;
                }
            }
            var created = GalaTopologyConfig.CreateTemplate();
            GalaTopologyConfig.Normalize(created);
            Directory.CreateDirectory(folder!);
            File.WriteAllText(path, JsonSerializer.Serialize(created,
                new JsonSerializerOptions { WriteIndented = true }));
            lock (_gate) { _cfg = created; }
            _ctx?.Log("info", $"已生成拓扑占位配置 {path}（请用真实拓扑资料编辑后重载）");
        }
        catch (Exception ex)
        {
            lock (_gate) { _cfg = GalaTopologyConfig.CreateTemplate(); }
            _ctx?.Log("warn", $"拓扑配置读取失败（使用内置占位模板）：{ex.Message}");
        }
    }
}
