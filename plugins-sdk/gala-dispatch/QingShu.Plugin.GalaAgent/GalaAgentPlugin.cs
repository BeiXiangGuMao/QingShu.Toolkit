using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Gala;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.GalaAgent;

/// <summary>
/// 元旦晚会智能分流 · 服务端插件（被控终端，常驻）：
/// 连接主控端，周期上报 RTT/丢包/收发速率/进程 CPU，执行主控指令
/// （切换线路/优先级/限速标记/即时采集），当前分配落盘 assignment.json 供本机执行器消费。
/// 连接参数持久化在 gala-agent.json：面板/快捷动作成功连接后写入，此后随插件加载自动重连
/// （常驻语义，适合舞台/投票/直播终端无人值守）。
/// 主控侧安装 qingshu.galamaster 主控端插件（或独立 QingShu.GalaMaster 程序）。
/// </summary>
public sealed class GalaAgentPlugin : IQToolkitPlugin, IQToolkitUiProvider
{
    private const string ConfigFileName = "gala-agent.json";

    private IPluginContext? _ctx;
    private GalaAgent? _agent;
    private readonly object _gate = new();
    private string _host = "";
    private int _port = 47816;
    private string _pairing = "";

    public string DisplayName => "元旦晚会智能分流 · 服务端";

    public string PanelTitle => "元旦晚会智能分流 · 客户端状态";

    public async Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Log("info", "元旦晚会智能分流 · 服务端插件加载");

        LoadOrCreateConfig();

        context.RegisterQuickAction("连接主控端", async _ =>
        {
            try
            {
                string host;
                int port;
                string pairing;
                lock (_gate) { host = _host; port = _port; pairing = _pairing; }
                return await ConnectAsync(host, port, pairing);
            }
            catch (Exception ex)
            {
                return $"连接失败：{ex.Message}";
            }
        });

        context.RegisterQuickAction("断开连接", async _ =>
        {
            try
            {
                return await DisconnectAsync();
            }
            catch (Exception ex)
            {
                return $"断开失败：{ex.Message}";
            }
        });

        context.RegisterQuickAction("客户端状态", _ =>
        {
            try
            {
                return Task.FromResult<string?>(DescribeStatus());
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"读取状态失败：{ex.Message}");
            }
        });

        context.RegisterQuickAction("导出客户端事件日志", _ =>
        {
            try
            {
                var folder = context.DataFolder;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"gala-agent-events-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                var agent = EnsureAgent().Snapshot();
                var lines = new List<string>
                {
                    $"===== 客户端事件（运行中：{(agent.Running ? "是" : "否")}）====="
                };
                lines.AddRange(agent.Events);
                File.WriteAllLines(path, lines);
                return Task.FromResult<string?>($"已导出客户端事件日志：{path}（共 {lines.Count} 行）");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"导出失败：{ex.Message}");
            }
        });

        context.RegisterQuickAction("打开数据目录", _ =>
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
                return Task.FromResult<string?>($"已打开数据目录：{dir}（assignment.json 在此，供本机执行器消费）");
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>($"打开目录失败：{ex.Message}");
            }
        });

        // 常驻语义：已配置主控地址 → 随插件加载自动连接（工具箱重启/插件重载后不掉线）
        if (_host.Length > 0)
        {
            try
            {
                await ConnectAsync(_host, _port, _pairing);
            }
            catch (Exception ex)
            {
                context.Log("warn", $"按 {ConfigFileName} 自动连接主控失败：{ex.Message}（可稍后用快捷动作重试）");
            }
        }
        else
        {
            context.Log("info", $"尚未配置主控地址：在面板填写并连接后会写入 {ConfigFileName}，此后随插件加载自动连接");
        }

        await Task.CompletedTask;
    }

    public async Task OnUnloadedAsync()
    {
        try
        {
            if (_agent != null) await _agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch { } // 卸载收尾尽力而为（宿主 5s 兜底）
        _agent = null;
        _ctx?.Log("info", "服务端插件已断开与主控的连接");
        _ctx = null;
    }

    // ================= 面板（宿主 UI 线程调用） =================

    public FrameworkElement CreatePanelView() => new GalaAgentPanelView(this);

    // ================= 连接管理（面板/快捷动作共用） =================

    /// <summary>面板当前连接参数（重建面板时回填输入框）。</summary>
    internal (string Host, int Port, string Pairing) CurrentConnection
    {
        get { lock (_gate) { return (_host, _port, _pairing); } }
    }

    /// <summary>
    /// 连接（或改连）主控端：先停旧会话（改目标必须重启会话），再按新参数启动，
    /// 并把参数写入 gala-agent.json（下次加载自动重连）。
    /// </summary>
    internal async Task<string> ConnectAsync(string host, int port, string pairing)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("插件上下文未就绪（插件未加载或已卸载）");
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new InvalidOperationException("请先填写主控 IP（同网可达）");
        if (port is < 1 or > 65535) port = 47816;
        pairing ??= "";

        GalaAgent? old;
        lock (_gate) { old = _agent; }
        if (old != null)
        {
            try { await old.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        var agent = EnsureAgent();
        agent.Start(host, port, pairing); // 主控地址为空等异常抛出 → 调用方 catch 显示
        lock (_gate) { _host = host; _port = port; _pairing = pairing; }
        SaveConfig();
        ctx.Log("info", $"服务端已连接目标主控 {host}:{port}（连接参数已写入 {ConfigFileName}）");
        return $"服务端已启动：目标 {host}:{port}，连接中…（参数已写入 {ConfigFileName}，此后随插件加载自动重连）";
    }

    internal async Task<string> DisconnectAsync()
    {
        GalaAgent? agent;
        lock (_gate) { agent = _agent; }
        if (agent == null || !agent.IsRunning) return "服务端未在运行。";
        try { await agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _ctx?.Log("info", "服务端已断开与主控的连接");
        return "服务端已断开（gala-agent.json 保留，下次加载仍会自动重连；不再自动连接请清空主控 IP 后连接一次）。";
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

    internal string DescribeStatus()
    {
        var agent = EnsureAgent().Snapshot();
        if (!agent.Running) return "服务端未运行（未连接主控）。";
        if (!agent.Connected)
            return $"未连接主控 {agent.MasterHost}:{agent.Port}（自动重连中，3 秒一次）。";
        var assign = $"已连接主控 {agent.MasterHost}:{agent.Port} · 当前分配：服务器 {OrDash(agent.ServerId)}（{OrDash(agent.ServerIp)}）"
                     + $" · 经 {OrDash(agent.RouterId)} · 优先级 {GalaDispatcher.PriorityNames[Math.Clamp(agent.Priority, 0, 2)]}"
                     + (agent.ThrottleKbps > 0 ? $" · 限速标记 {agent.ThrottleKbps} kbps" : "");
        return assign + $" · 到主控 RTT {FmtMs(agent.RttMasterMs)} · 到服务器 RTT {FmtMs(agent.RttServerMs)}";
    }

    // ================= gala-agent.json（与独立服务端程序同字段名） =================

    private sealed class AgentPersistConfig
    {
        [JsonPropertyName("masterHost")] public string MasterHost { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; } = 47816;
        [JsonPropertyName("pairingCode")] public string PairingCode { get; set; } = "";
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
                var cfg = JsonSerializer.Deserialize<AgentPersistConfig>(File.ReadAllText(path));
                if (cfg != null)
                {
                    lock (_gate)
                    {
                        _host = (cfg.MasterHost ?? "").Trim();
                        _port = cfg.Port is >= 1 and <= 65535 ? cfg.Port : 47816;
                        _pairing = (cfg.PairingCode ?? "").Trim();
                    }
                    _ctx?.Log("info", _host.Length > 0
                        ? $"已载入连接配置：主控 {_host}:{_port}"
                        : "连接配置存在但未填主控地址（等待面板/快捷动作配置）");
                    return;
                }
            }
            Directory.CreateDirectory(folder!);
            File.WriteAllText(path, JsonSerializer.Serialize(new AgentPersistConfig(),
                new JsonSerializerOptions { WriteIndented = true }));
            _ctx?.Log("info", $"已生成连接配置模板 {path}（填好 masterHost 后连接一次即可常驻自动重连）");
        }
        catch (Exception ex)
        {
            _ctx?.Log("warn", $"连接配置读取失败（沿用默认值）：{ex.Message}");
        }
    }

    private void SaveConfig()
    {
        var folder = _ctx?.DataFolder;
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            string host, pairing;
            int port;
            lock (_gate) { host = _host; port = _port; pairing = _pairing; }
            Directory.CreateDirectory(folder!);
            File.WriteAllText(Path.Combine(folder!, ConfigFileName),
                JsonSerializer.Serialize(new AgentPersistConfig
                {
                    MasterHost = host,
                    Port = port,
                    PairingCode = pairing
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _ctx?.Log("warn", $"连接配置写盘失败（本次会话仍生效）：{ex.Message}");
        }
    }

    // ================= 小工具 =================

    private static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string FmtMs(double v) => v < 0 ? "—" : $"{v:F0}ms";
}
