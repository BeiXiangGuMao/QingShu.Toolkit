using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Gala;

public sealed class AgentSnapshot
{
    public bool Running { get; init; }
    public bool Connected { get; init; }
    public string MasterHost { get; init; } = "";
    public int Port { get; init; }
    public string RouterId { get; init; } = "";
    public string ServerId { get; init; } = "";
    public string ServerIp { get; init; } = "";
    public int Priority { get; init; } = 1;
    public int ThrottleKbps { get; init; }
    public double RttMasterMs { get; init; } = -1;
    public double RttServerMs { get; init; } = -1;
    public double LossPct { get; init; }
    public double RxKbps { get; init; }
    public double TxKbps { get; init; }
    public double AppCpu { get; init; }
    public List<string> Events { get; init; } = new();
    public string AssignmentFile { get; init; } = "";
}

/// <summary>
/// 客户端 Agent：常驻连接主控（断线 3 秒重连），周期上报本地链路指标
/// （主控/分配服务器 RTT、丢包、收发速率、进程 CPU），接收并执行主控指令
/// （切换线路 / 优先级 / 限速标记 / 即时上报），把当前分配持久化到 assignment.json
/// 供本机其它执行器（如路由脚本）消费。
/// </summary>
public sealed class GalaAgent
{
    private readonly Action<string> _log;
    private readonly string _dataFolder;
    private readonly object _sync = new();
    private readonly List<string> _events = new();
    private readonly Queue<bool> _pingWindow = new();

    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private TcpClient? _tcp;
    private volatile bool _running;
    private volatile bool _connected;

    private string _masterHost = "";
    private int _port;
    private string _pairing = "";
    private string _routerId = "";
    private string _serverId = "";
    private string _serverIp = "";
    private int _priority = 1;
    private int _throttleKbps;
    private double _rttMaster = -1, _rttServer = -1, _rxKbps, _txKbps, _appCpu, _lossPct;
    private int _tickSeconds = 3;
    private int _failStreak;
    private readonly SemaphoreSlim _hookLock = new(1, 1);

    public bool IsRunning => _running;
    public bool IsConnected => _connected;
    public string AssignmentFile { get; }

    /// <summary>
    /// 可选钩子：assignment.json 每次落盘后触发（参数 = 文件完整路径），供本机执行器
    /// 从「轮询文件」升级为「变更即执行」。内部已串行化（防并发拉起）+ 异常留痕；
    /// 实现方仍应避免长时间阻塞（钩子在后台线程执行，不影响上报/指令收发）。
    /// </summary>
    public Action<string>? AssignmentChanged { get; set; }

    public GalaAgent(Action<string> log, string dataFolder)
    {
        _log = log;
        _dataFolder = dataFolder;
        AssignmentFile = Path.Combine(dataFolder, "assignment.json");
    }

    // ================= 启停 =================

    public void Start(string host, int port, string pairing)
    {
        lock (_sync)
        {
            if (_running) return;
            _masterHost = (host ?? "").Trim();
            if (_masterHost.Length == 0) throw new ArgumentException("主控地址不能为空", nameof(host));
            _port = port is >= 1 and <= 65535 ? port : 47816;
            _pairing = pairing ?? "";
            _running = true;
            _cts = new CancellationTokenSource();
            _runLoop = Task.Run(() => RunLoopAsync(_cts.Token));
            Event($"客户端已启动：目标主控 {_masterHost}:{_port}" +
                  (_pairing.Length > 0 ? "（已填配对码）" : "（未填配对码）"));
            LoadAssignment();
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        TcpClient? tcp;
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            cts = _cts; loop = _runLoop; tcp = _tcp;
        }
        try { tcp?.Close(); } catch { }
        try { cts?.Cancel(); } catch { }
        try { if (loop != null) await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        lock (_sync)
        {
            _cts = null; _runLoop = null; _tcp = null; _connected = false;
        }
        Event("客户端已停止");
    }

    // ================= 主循环 =================

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = new TcpClient();
                tcp.NoDelay = true;
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(8));
                await tcp.ConnectAsync(_masterHost, _port, connectCts.Token).ConfigureAwait(false);
                var stream = tcp.GetStream();
                lock (_sync) { _tcp = tcp; _connected = true; _failStreak = 0; }
                lock (_pingWindow) { _pingWindow.Clear(); } // 新连接重开丢包窗口，旧连接样本不作数
                Event($"已连接主控 {_masterHost}:{_port}");

                var hello = new HelloMsg
                {
                    Hostname = Environment.MachineName,
                    Ips = LocalIpv4Csv(),
                    Pairing = _pairing,
                    Version = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    Link = DetectLinkType() // 网线/直连 WiFi，主控画布按类型配色逐台展示
                };
                await SendFrameAsync(stream, GalaFrame.Encode(hello), ct).ConfigureAwait(false);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var senderTask = SendMetricsLoopAsync(stream, linked.Token);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var body = await GalaFrame.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                        if (body == null) break;
                        HandleInbound(stream, body, ct);
                    }
                }
                finally
                {
                    linked.Cancel();
                    try { await senderTask.ConfigureAwait(false); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _failStreak++;
                // 重连失败日志降频：首次与之后每 10 次记一条，防主控未开时刷屏
                if (_failStreak == 1 || _failStreak % 10 == 0)
                    Event($"连接主控 {_masterHost}:{_port} 失败（连续 {_failStreak} 次）：{ex.Message}；持续重试中");
            }
            finally
            {
                lock (_sync) { _tcp = null; _connected = false; }
                try { tcp?.Close(); } catch { }
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendMetricsLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var (lastRx, lastTx) = NetworkBytes();
        double lastCpuSec = ProcessCpuSeconds();
        var lastTs = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            int tick;
            lock (_sync) { tick = _tickSeconds; }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, tick)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            var (rx, tx) = NetworkBytes();
            double cpuSec = ProcessCpuSeconds();
            double dt = (now - lastTs).TotalSeconds;
            if (dt < 0.2) continue;

            double rxK = Math.Max(0, (rx - lastRx) * 8 / dt / 1000.0);
            double txK = Math.Max(0, (tx - lastTx) * 8 / dt / 1000.0);
            lastRx = rx; lastTx = tx; lastTs = now;
            double appCpu = Math.Max(0, (cpuSec - lastCpuSec) / dt / Math.Max(1, Environment.ProcessorCount) * 100);
            lastCpuSec = cpuSec;

            double rttMaster = await ProbeRttAsync(_masterHost).ConfigureAwait(false);
            string serverIp;
            lock (_sync) { serverIp = _serverIp; }
            double rttServer = serverIp.Length > 0 ? await ProbeRttAsync(serverIp).ConfigureAwait(false) : -1;
            double loss = RecordPing(rttMaster >= 0);

            lock (_sync)
            {
                _rttMaster = rttMaster; _rttServer = rttServer;
                _rxKbps = rxK; _txKbps = txK; _appCpu = appCpu; _lossPct = loss;
            }

            var msg = new MetricsMsg
            {
                RttMasterMs = rttMaster, RttServerMs = rttServer, LossPct = loss,
                RxKbps = rxK, TxKbps = txK, AppCpu = appCpu
            };
            try { await SendFrameAsync(stream, GalaFrame.Encode(msg), ct).ConfigureAwait(false); }
            catch { break; }
        }
    }

    // ================= 入站消息 =================

    private void HandleInbound(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        string type = GalaFrame.Decode<HelloMsg>(body)?.Type ?? "";
        if (type == "welcome")
        {
            var w = GalaFrame.Decode<WelcomeMsg>(body);
            if (w == null) return;
            lock (_sync)
            {
                _routerId = w.RouterId;
                _serverId = w.ServerId;
                _serverIp = w.ServerIp;
                _priority = Math.Clamp(w.Priority, 0, 2);
                _throttleKbps = w.ThrottleKbps;
                _tickSeconds = Math.Max(1, w.TickSeconds);
            }
            PersistAssignment();
            Event($"主控确认分配：服务器 {w.ServerId}" +
                  (w.ServerIp.Length > 0 ? $"（{w.ServerIp}）" : "") +
                  $" · 经 {w.RouterId} · 优先级 {GalaDispatcher.PriorityNames[_priority]}");
        }
        else if (type == "command")
        {
            var c = GalaFrame.Decode<CommandMsg>(body);
            if (c == null) return;
            string detail = ExecuteCommand(c);
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendFrameAsync(stream,
                        GalaFrame.Encode(new AckMsg { CmdId = c.CmdId, Ok = true, Detail = detail }), ct)
                        .ConfigureAwait(false);
                }
                catch { }
            });
        }
    }

    private string ExecuteCommand(CommandMsg c)
    {
        lock (_sync)
        {
            switch (c.Action)
            {
                case "switch":
                    _routerId = c.RouterId;
                    _serverId = c.ServerId;
                    _serverIp = c.ServerIp;
                    _rttServer = -1;
                    break;
                case "priority":
                    _priority = Math.Clamp(c.Value, 0, 2);
                    break;
                case "throttle":
                    _throttleKbps = Math.Max(0, c.Value);
                    break;
                case "probe":
                    return "已收到即时上报请求";
                default:
                    return $"未知指令 {c.Action}";
            }
        }
        PersistAssignment();
        var line = c.Action switch
        {
            "switch" => $"线路切换：→ {c.ServerId}" + (c.ServerIp.Length > 0 ? $"（{c.ServerIp}）" : "") + $" · 经 {c.RouterId}",
            "priority" => $"优先级调整：{GalaDispatcher.PriorityNames[Math.Clamp(c.Value, 0, 2)]}",
            "throttle" => $"限速标记：{Math.Max(0, c.Value)} kbps（写入 assignment.json 供本机执行器消费）",
            _ => c.Action
        };
        Event(line);
        return c.Action switch
        {
            "switch" => $"已切换到 {c.ServerId}",
            "priority" => "优先级已更新",
            "throttle" => "限速标记已更新",
            _ => "完成"
        };
    }

    // ================= 分配持久化（assignment.json） =================

    private sealed class AssignmentRecord
    {
        [JsonPropertyName("routerId")] public string RouterId { get; set; } = "";
        [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
        [JsonPropertyName("serverIp")] public string ServerIp { get; set; } = "";
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("throttleKbps")] public int ThrottleKbps { get; set; }
        [JsonPropertyName("updatedAt")] public string UpdatedAt { get; set; } = "";
    }

    private void PersistAssignment()
    {
        AssignmentRecord rec;
        lock (_sync)
        {
            rec = new AssignmentRecord
            {
                RouterId = _routerId, ServerId = _serverId, ServerIp = _serverIp,
                Priority = _priority, ThrottleKbps = _throttleKbps,
                UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
        try
        {
            Directory.CreateDirectory(_dataFolder);
            File.WriteAllText(AssignmentFile, JsonSerializer.Serialize(rec,
                new JsonSerializerOptions { WriteIndented = true }));
            TriggerAssignmentHook();
        }
        catch (Exception ex) { Event($"分配状态落盘失败：{ex.Message}"); }
    }

    /// <summary>分配落盘成功后触发变更钩子（串行执行防并发拉起；异常仅留痕不重试）。</summary>
    private void TriggerAssignmentHook()
    {
        var hook = AssignmentChanged;
        if (hook == null) return;
        _ = Task.Run(async () =>
        {
            try { await _hookLock.WaitAsync().ConfigureAwait(false); } catch { return; }
            try { hook(AssignmentFile); }
            catch (Exception ex) { Event($"分配变更钩子执行失败：{ex.Message}"); }
            finally { try { _hookLock.Release(); } catch { } }
        });
    }

    private void LoadAssignment()
    {
        try
        {
            if (!File.Exists(AssignmentFile)) return;
            var rec = JsonSerializer.Deserialize<AssignmentRecord>(File.ReadAllText(AssignmentFile));
            if (rec == null) return;
            lock (_sync)
            {
                // 仅作展示恢复；真实分配以主控 welcome 为准
                _routerId = rec.RouterId ?? "";
                _serverId = rec.ServerId ?? "";
                _serverIp = rec.ServerIp ?? "";
                _priority = Math.Clamp(rec.Priority, 0, 2);
                _throttleKbps = Math.Max(0, rec.ThrottleKbps);
            }
            Event($"已恢复上次分配显示：{rec.ServerId}（以主控下发为准）");
        }
        catch (Exception ex) { Event($"分配状态读取失败：{ex.Message}"); }
    }

    // ================= 采集与工具 =================

    private double RecordPing(bool ok)
    {
        lock (_pingWindow)
        {
            _pingWindow.Enqueue(ok);
            while (_pingWindow.Count > 10) _pingWindow.Dequeue();
            return _pingWindow.Count(x => !x) * 100.0 / _pingWindow.Count;
        }
    }

    private static async Task<double> ProbeRttAsync(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return -1;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1200).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
        }
        catch { return -1; }
    }

    private static (long Rx, long Tx) NetworkBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var st = nic.GetIPv4Statistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    private static double ProcessCpuSeconds()
    {
        try { return Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds; }
        catch { return 0; }
    }

    private static string LocalIpv4Csv()
    {
        try
        {
            return string.Join(",", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .Distinct());
        }
        catch { return ""; }
    }

    /// <summary>
    /// 接入方式识别：取「Up + 有 IPv4 网关」的网卡（真正在上网的那张），按介质类型判定
    /// ethernet=网线 / wifi=直连 WiFi；多网卡取首个符合的；识别不了留空（主控按中性色展示）。
    /// </summary>
    private static string DetectLinkType()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                     && n.GetIPProperties().GatewayAddresses.Any(g =>
                                         g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                     && n.GetIPProperties().UnicastAddresses.Any(a =>
                                         a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
            if (nic == null) return "";
            return nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi"
                 : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "ethernet"
                 : "";
        }
        catch { return ""; }
    }

    // ================= 快照 / 事件 =================

    public AgentSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new AgentSnapshot
            {
                Running = _running,
                Connected = _connected,
                MasterHost = _masterHost,
                Port = _port,
                RouterId = _routerId,
                ServerId = _serverId,
                ServerIp = _serverIp,
                Priority = _priority,
                ThrottleKbps = _throttleKbps,
                RttMasterMs = _rttMaster,
                RttServerMs = _rttServer,
                LossPct = _lossPct,
                RxKbps = _rxKbps,
                TxKbps = _txKbps,
                AppCpu = _appCpu,
                Events = new List<string>(_events),
                AssignmentFile = AssignmentFile
            };
        }
    }

    private void Event(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (_sync)
        {
            _events.Add(line);
            if (_events.Count > 200) _events.RemoveRange(0, _events.Count - 200);
        }
        try { _log(text); } catch { }
    }
}
