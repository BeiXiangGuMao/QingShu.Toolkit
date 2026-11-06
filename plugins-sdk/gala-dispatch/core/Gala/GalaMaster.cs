using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Gala;

// ================= 快照模型（主控 → 面板 UI，只读拷贝） =================

public sealed class NodeSnap
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = "";
    public string Ip { get; init; } = "";
    /// <summary>综合压力 0~100（-1 = 不参与压力评估，如 master/switch）。</summary>
    public double Pressure { get; init; } = -1;
    public double RttMs { get; init; } = -1;
    public double LossPct { get; init; }
    public int ServedClients { get; init; }
    public bool Down { get; init; }
}

public sealed class ClientSnap
{
    public string ClientId { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Ip { get; init; } = "";
    public string RouterId { get; init; } = "";
    public string ServerId { get; init; } = "";
    public int Priority { get; init; } = 1;
    public int ThrottleKbps { get; init; }
    /// <summary>手动锁定（操作员手动切换后置位，自动调度跳过，恢复自动后清除）。</summary>
    public bool Pinned { get; init; }
    public bool Online { get; init; }
    public double RttMasterMs { get; init; } = -1;
    public double RxKbps { get; init; }
    public double TxKbps { get; init; }
    public double LastAgeSec { get; init; }
}

public sealed class MasterSnapshot
{
    public bool Running { get; init; }
    public int Port { get; init; }
    public List<NodeSnap> Nodes { get; init; } = new();
    public List<ClientSnap> Clients { get; init; } = new();
    public List<string> Events { get; init; } = new();
    /// <summary>各压力节点（router/server）的近期压力历史（旧→新，最多 90 点），供面板趋势线。</summary>
    public Dictionary<string, List<double>> PressureHistory { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 主控端：TCP 监听（仅内网段准入）+ 客户端会话管理 + 路由/服务器探针 +
/// 「路由压力 × 服务器压力」综合调度 + 远程操控指令下发。全部后台循环可取消、可重入启动。
/// </summary>
public sealed class GalaMaster
{
    private readonly GalaTopologyConfig _cfg;
    private readonly Action<string> _log;
    private readonly object _sync = new();

    private readonly Dictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PressureProbe> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<double>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _events = new();
    private int _pairFails; // 配对码连续失败计数（防内网脚本爆破）

    private CancellationTokenSource? _cts;
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private Task? _probeLoop;
    private volatile bool _running;

    public bool IsRunning => _running;
    public int Port => _cfg.Settings.ListenPort;

    public GalaMaster(GalaTopologyConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
    }

    // ================= 启停 =================

    public void Start()
    {
        lock (_sync)
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, _cfg.Settings.ListenPort);
            _listener.Start(16); // 端口被占等异常直接抛给调用方（面板显示）
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _running = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(ct));
            _probeLoop = Task.Run(() => ProbeLoopAsync(ct));
            Event($"主控端已启动 0.0.0.0:{_cfg.Settings.ListenPort}（准入段 {string.Join(" / ", _cfg.Settings.AllowedPrefixes)}" +
                  (_cfg.Settings.PairingCode.Length > 0 ? "，配对码已启用" : "，未设配对码") + "）");
        }
    }

    public async Task StopAsync()
    {
        Task? accept, probe;
        CancellationTokenSource? cts;
        TcpListener? listener;
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            cts = _cts; listener = _listener; accept = _acceptLoop; probe = _probeLoop;
        }
        try { listener?.Stop(); } catch { }
        List<ClientSession> all;
        lock (_sync) { all = _sessions.Values.ToList(); }
        foreach (var s in all)
        {
            s.Alive = false;
            try { s.Tcp?.Close(); } catch { }
        }
        try { cts?.Cancel(); } catch { }
        try { if (accept != null) await accept.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { if (probe != null) await probe.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        lock (_sync)
        {
            _cts = null; _listener = null; _acceptLoop = null; _probeLoop = null;
        }
        Event("主控端已停止");
    }

    // ================= 接入与会话 =================

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Event($"接受连接异常：{ex.Message}"); continue; }
            _ = Task.Run(() => HandleClientAsync(tcp, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        ClientSession? session = null;
        var myTcp = tcp;
        try
        {
            tcp.NoDelay = true;
            var remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
            if (!IsAllowedIp(remote))
            {
                Event($"拒绝来源 {remote}：不在内网准入段（仅限城域网使用）");
                tcp.Close();
                return;
            }
            var stream = tcp.GetStream();

            var body = await GalaFrame.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (body == null) { tcp.Close(); return; }
            var hello = GalaFrame.Decode<HelloMsg>(body);
            if (hello == null || !string.Equals(hello.Type, "hello", StringComparison.Ordinal))
            {
                Event($"拒绝来源 {remote}：首帧不是 hello");
                tcp.Close();
                return;
            }
            var pairing = _cfg.Settings.PairingCode;
            if (pairing.Length > 0 && !string.Equals(hello.Pairing, pairing, StringComparison.Ordinal))
            {
                int fails;
                lock (_sync) { fails = ++_pairFails; }
                if (fails == 1 || fails % 5 == 0)
                    Event($"拒绝 {hello.Hostname}@{remote}：配对码不符（连续第 {fails} 次，已加 1.5 秒延迟防爆破）");
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { }
                tcp.Close();
                return;
            }
            lock (_sync) { _pairFails = 0; } // 配对成功即重置爆破计数

            string ip = GalaFrame.PickBestIp(hello.Ips, LocalIpv4s());
            if (ip.Length == 0) ip = remote;
            if (!IsAllowedIp(ip))
            {
                Event($"拒绝 {hello.Hostname}@{ip}：上报 IP 不在准入段");
                tcp.Close();
                return;
            }

            string clientId = $"{hello.Hostname}@{ip}";
            lock (_sync)
            {
                if (_sessions.TryGetValue(clientId, out var old))
                {
                    // 同一终端重连：复用会话对象（保住分配/优先级），旧连接由代际校验自然退出
                    old.Alive = false;
                    try { old.Tcp?.Close(); } catch { }
                    session = old;
                }
                else
                {
                    session = new ClientSession { ClientId = clientId, Hostname = hello.Hostname, Ip = ip };
                    _sessions[clientId] = session;
                }
                session.Tcp = tcp;
                session.Stream = stream;
                session.Alive = true;
                session.LastSeenUtc = DateTime.UtcNow;

                if (session.ServerId.Length == 0)
                {
                    // 初始分配：按段规则的第一条（后续由调度循环按压力调整）
                    var route = _cfg.MatchRoute(session.Ip);
                    if (route != null)
                    {
                        session.RouterId = route.ViaRouter;
                        session.ServerId = route.Servers.Count > 0 ? route.Servers[0] : "";
                        Event($"客户端 {clientId} 初始分配 {session.ServerId}（经 {session.RouterId}）");
                    }
                    else
                    {
                        Event($"客户端 {clientId} 上线（IP 段未配置调度规则，仅监控不调度）");
                    }
                }
            }
            Event($"客户端上线：{clientId}（版本 {string.IsNullOrWhiteSpace(hello.Version) ? "未知" : hello.Version}）");

            var sess = session!; // lock 块内已必赋值；局部非空别名消除可空警告
            await SendWelcomeAsync(sess).ConfigureAwait(false);

            TimeSpan idleTimeout = TimeSpan.FromSeconds(Math.Max(10, _cfg.Settings.ClientIdleTimeoutSeconds));
            while (!ct.IsCancellationRequested)
            {
                byte[]? frame;
                try
                {
                    // 心跳空闲超时：终端拔电/网线故障等半开连接（无 FIN/RST）不再永久占用会话，
                    // 超时按离线断开，避免虚占并发压力分导致调度失真
                    frame = await GalaFrame.ReadFrameAsync(stream, ct).WaitAsync(idleTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Event($"心跳超时：{sess.ClientId} 超过 {idleTimeout.TotalSeconds:F0} 秒未上报，按离线断开");
                    break;
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
                if (frame == null) break;

                string type = "";
                try
                {
                    var probeType = GalaFrame.Decode<HelloMsg>(frame);
                    type = probeType?.Type ?? "";
                    if (type == "metrics")
                    {
                        var m = GalaFrame.Decode<MetricsMsg>(frame);
                        if (m != null)
                        {
                            lock (_sync) { sess.LastMetrics = m; sess.LastSeenUtc = DateTime.UtcNow; }
                        }
                    }
                    else if (type == "ack")
                    {
                        var a = GalaFrame.Decode<AckMsg>(frame);
                        if (a != null)
                            Event($"回执 {sess.ClientId} · {a.CmdId}：{(a.Ok ? "成功" : "失败")} {a.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    Event($"坏消息（{sess.ClientId}）：{ex.Message}");
                    break; // 帧损坏一律断开重连
                }
                lock (_sync) { sess.LastSeenUtc = DateTime.UtcNow; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Event($"会话异常（{session?.ClientId ?? "?"}）：{ex.Message}");
        }
        finally
        {
            // 代际校验：只有当前连接（未被重连接管）才标记离线
            if (session != null)
            {
                bool isCurrent;
                lock (_sync) { isCurrent = ReferenceEquals(session.Tcp, myTcp); }
                if (isCurrent)
                {
                    session.Alive = false;
                    Event($"客户端离线：{session.ClientId}");
                    try { session.Tcp?.Close(); } catch { }
                }
            }
            else
            {
                try { tcp.Close(); } catch { }
            }
        }
    }

    private async Task SendWelcomeAsync(ClientSession s)
    {
        var welcome = new WelcomeMsg
        {
            RouterId = s.RouterId,
            ServerId = s.ServerId,
            ServerIp = _cfg.FindNode(s.ServerId)?.Ip ?? "",
            Priority = s.Priority,
            ThrottleKbps = s.ThrottleKbps,
            TickSeconds = _cfg.Settings.TickSeconds
        };
        await SendAsync(s, GalaFrame.Encode(welcome), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SendAsync(ClientSession s, byte[] frame, CancellationToken ct)
    {
        var stream = s.Stream;
        if (stream == null || !s.Alive) throw new InvalidOperationException("会话已断开");
        await s.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await GalaFrame.WriteFrameAsync(stream, frame, ct).ConfigureAwait(false);
        }
        finally { s.SendLock.Release(); }
    }

    // ================= 探针与综合调度 =================

    private async Task ProbeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nodes = _cfg.Nodes
                    .Where(n => (n.Type == "router" || n.Type == "server") && n.Ip.Length > 0)
                    .ToList();

                // 并行探测：单轮耗时 ≈ 最慢一个节点（约 3 秒），不再随节点数线性放大，
                // 保证 tickSeconds=3 时大拓扑（几十节点）也不拖慢调度周期
                var results = await Task.WhenAll(nodes.Select(n => ProbeNodeAsync(n, ct))).ConfigureAwait(false);
                foreach (var (node, alive, rtt) in results)
                {
                    PressureProbe probe;
                    lock (_sync)
                    {
                        if (!_probes.TryGetValue(node.Id, out probe!)) _probes[node.Id] = probe = new PressureProbe();
                    }
                    probe.Feed(!alive, rtt);
                }

                await DispatchPassAsync(ct).ConfigureAwait(false);
                CleanupStaleSessions();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Event($"探测/调度循环异常：{ex.Message}"); }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _cfg.Settings.TickSeconds)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 单节点探测：ICMP Ping +（配置 probePort 的节点，不限类型）TCP 端口握手；Ping 失败但 TCP 通即视为存活
    /// （禁 ICMP 的网段给路由/服务器配 probePort 即可恢复探测，RTT 取 TCP 握手耗时）。Ping 实例不可并发复用，故每任务独立创建。
    /// </summary>
    private static async Task<(GalaNodeConfig Node, bool Alive, double RttMs)> ProbeNodeAsync(GalaNodeConfig node, CancellationToken ct)
    {
        double rtt = -1;
        bool alive = false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(node.Ip, 1500).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                rtt = reply.RoundtripTime;
                alive = true;
            }
        }
        catch { }
        if (node.ProbePort > 0)
        {
            var sw = Stopwatch.StartNew();
            bool tcpOk = await ProbeTcpAsync(node.Ip, node.ProbePort, ct).ConfigureAwait(false);
            sw.Stop();
            if (tcpOk)
            {
                alive = true;
                rtt = rtt < 0 ? sw.Elapsed.TotalMilliseconds : Math.Max(rtt, sw.Elapsed.TotalMilliseconds);
            }
        }
        return (node, alive, rtt);
    }

    /// <summary>
    /// 清理离线超过 30 分钟的会话（重连保分配的语义保留 30 分钟，防现场临时终端无限积累）。
    /// </summary>
    private void CleanupStaleSessions()
    {
        List<string> gone = new();
        lock (_sync)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var kv in _sessions)
            {
                if (!kv.Value.Alive && kv.Value.LastSeenUtc < cutoff) gone.Add(kv.Key);
            }
            foreach (var id in gone) _sessions.Remove(id);
        }
        if (gone.Count > 0) Event($"已清理离线超 30 分钟的会话：{string.Join("、", gone)}");
    }

    private static async Task<bool> ProbeTcpAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, ct).WaitAsync(TimeSpan.FromMilliseconds(1500)).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>一轮综合调度：算节点压力 → 对每个在线客户端做滞回决策 → 下发切换。</summary>
    private async Task DispatchPassAsync(CancellationToken ct)
    {
        List<ClientSession> sessions;
        lock (_sync) { sessions = _sessions.Values.ToList(); }

        var served = CountServed(sessions, DateTime.UtcNow);
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            foreach (var node in _cfg.Nodes)
            {
                if (node.Type != "router" && node.Type != "server") continue;
                _probes.TryGetValue(node.Id, out var probe);
                var (cnt, kbps) = served.TryGetValue(node.Id, out var v) ? v : (0, 0.0);
                scores[node.Id] = GalaPressure.Score(new PressureInput
                {
                    RttMs = probe?.EmaRtt ?? -1,
                    LossPct = probe?.LossPct ?? 0,
                    ServedClients = cnt,
                    ServedMbps = kbps / 1000.0,
                    CapacityMbps = MaxCapacityOf(node.Id)
                }, _cfg.Settings);
            }
            foreach (var kv in scores) _scores[kv.Key] = kv.Value;
        }
        RecordHistory(scores);

        foreach (var s in sessions)
        {
            if (ct.IsCancellationRequested) break;
            if (!s.Alive || s.Ip.Length == 0) continue;
            if (s.Pinned) continue; // 手动锁定：操作员指定线路，不参与自动调度（恢复自动后重新参与）
            TimeSpan sinceLast = s.LastSwitchUtc == DateTime.MinValue
                ? TimeSpan.Zero
                : DateTime.UtcNow - s.LastSwitchUtc;
            var decision = GalaDispatcher.Decide(
                s.ClientId, s.Ip,
                s.RouterId.Length > 0 ? s.RouterId : null,
                s.ServerId.Length > 0 ? s.ServerId : null,
                s.Priority, _cfg.Routes, scores, _cfg.Settings, sinceLast);
            if (decision == null) continue;

            var cmd = new CommandMsg
            {
                CmdId = $"sw-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
                Action = "switch",
                RouterId = decision.NewRouterId,
                ServerId = decision.NewServerId,
                ServerIp = _cfg.FindNode(decision.NewServerId)?.Ip ?? ""
            };
            try
            {
                await SendAsync(s, GalaFrame.Encode(cmd), ct).ConfigureAwait(false);
                s.RouterId = decision.NewRouterId;
                s.ServerId = decision.NewServerId;
                s.LastSwitchUtc = DateTime.UtcNow;
                Event($"综合调度 {s.ClientId}：{decision.Reason}");
            }
            catch (Exception ex)
            {
                Event($"下发切换失败（{s.ClientId}）：{ex.Message}");
            }
        }
    }

    /// <summary>记录本轮各节点压力到环形历史（最多 90 点 ≈ 近 5 分钟 @3s tick），供面板趋势线。</summary>
    private void RecordHistory(IReadOnlyDictionary<string, double> scores)
    {
        lock (_sync)
        {
            foreach (var kv in scores)
            {
                if (!_history.TryGetValue(kv.Key, out var q)) _history[kv.Key] = q = new Queue<double>();
                q.Enqueue(kv.Value);
                while (q.Count > 90) q.Dequeue();
            }
        }
    }

    private static Dictionary<string, (int Count, double Kbps)> CountServed(List<ClientSession> sessions, DateTime nowUtc)
    {
        var map = new Dictionary<string, (int, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (!IsOnline(s, nowUtc)) continue; // 与界面在线口径一致：心跳新颗才算数，半开僵尸不虚增负载分
            double kbps = (s.LastMetrics?.RxKbps ?? 0) + (s.LastMetrics?.TxKbps ?? 0);
            foreach (var nodeId in new[] { s.RouterId, s.ServerId })
            {
                if (nodeId.Length == 0) continue;
                var cur = map.TryGetValue(nodeId, out var v) ? v : (0, 0.0);
                map[nodeId] = (cur.Item1 + 1, cur.Item2 + kbps);
            }
        }
        return map;
    }

    private double MaxCapacityOf(string nodeId)
    {
        double cap = 0;
        foreach (var l in _cfg.Links)
        {
            if (string.Equals(l.From, nodeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(l.To, nodeId, StringComparison.OrdinalIgnoreCase))
            {
                cap = Math.Max(cap, l.CapacityMbps);
            }
        }
        return cap;
    }

    // ================= 远程操控（面板调用，fire-and-forget + 事件留痕） =================

    public bool TryAssignServer(string clientId, string serverId)
    {
        var session = FindAlive(clientId);
        if (session == null) return false;
        var route = _cfg.MatchRoute(session.Ip);
        string routerId = route != null && route.Servers.Contains(serverId, StringComparer.OrdinalIgnoreCase)
            ? route.ViaRouter
            : _cfg.Routes.FirstOrDefault(r => r.Servers.Contains(serverId, StringComparer.OrdinalIgnoreCase))?.ViaRouter ?? "";
        var cmd = new CommandMsg
        {
            CmdId = $"as-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Action = "switch", RouterId = routerId, ServerId = serverId,
            ServerIp = _cfg.FindNode(serverId)?.Ip ?? ""
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(session, GalaFrame.Encode(cmd), CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    session.RouterId = routerId;
                    session.ServerId = serverId;
                    session.LastSwitchUtc = DateTime.UtcNow;
                    session.Pinned = true; // 手动切换即锁定：自动调度不再覆盖操作员意图
                }
                Event($"手动切换 {clientId} → {serverId}（经 {routerId}），已锁定不参与自动调度（恢复自动用 TryUnpin/面板按钮）");
            }
            catch (Exception ex) { Event($"手动切换失败（{clientId}）：{ex.Message}"); }
        });
        return true;
    }

    public bool TrySetPriority(string clientId, int priority)
    {
        var session = FindAlive(clientId);
        if (session == null) return false;
        priority = Math.Clamp(priority, 0, 2);
        var cmd = new CommandMsg
        {
            CmdId = $"pr-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Action = "priority", Value = priority
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(session, GalaFrame.Encode(cmd), CancellationToken.None).ConfigureAwait(false);
                session.Priority = priority;
                Event($"优先级调整 {clientId} → {GalaDispatcher.PriorityNames[priority]}");
            }
            catch (Exception ex) { Event($"优先级调整失败（{clientId}）：{ex.Message}"); }
        });
        return true;
    }

    public bool TryPingNow(string clientId)
    {
        var session = FindAlive(clientId);
        if (session == null) return false;
        var cmd = new CommandMsg
        {
            CmdId = $"pn-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Action = "probe"
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(session, GalaFrame.Encode(cmd), CancellationToken.None).ConfigureAwait(false);
                Event($"已请求 {clientId} 即时上报指标");
            }
            catch (Exception ex) { Event($"即时上报请求失败（{clientId}）：{ex.Message}"); }
        });
        return true;
    }

    /// <summary>远程限速标记：0 = 解除。客户端落盘 assignment.json 供本机执行器消费。</summary>
    public bool TrySetThrottle(string clientId, int kbps)
    {
        var session = FindAlive(clientId);
        if (session == null) return false;
        kbps = Math.Max(0, kbps);
        var cmd = new CommandMsg
        {
            CmdId = $"th-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Action = "throttle", Value = kbps
        };
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(session, GalaFrame.Encode(cmd), CancellationToken.None).ConfigureAwait(false);
                session.ThrottleKbps = kbps;
                Event($"限速标记 {clientId} → {(kbps > 0 ? kbps + " kbps" : "解除")}");
            }
            catch (Exception ex) { Event($"限速标记失败（{clientId}）：{ex.Message}"); }
        });
        return true;
    }

    /// <summary>
    /// 剔除会话：断开连接并立即移除（不保留 30 分钟重连保分配），用于误连/故障终端的即时清理；
    /// 终端再重连将按新终端重新分配。
    /// </summary>
    public bool TryKick(string clientId)
    {
        ClientSession? session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(clientId, out session)) return false;
            _sessions.Remove(clientId);
        }
        var tcp = session.Tcp;
        lock (_sync) { session.Tcp = null; session.Stream = null; } // 摘除连接引用：读循环代际校验判非当前，不再重复报离线
        session.Alive = false;
        try { tcp?.Close(); } catch { }
        Event($"已剔除会话：{clientId}（连接断开并移除，重连将按新终端重新分配）");
        return true;
    }

    /// <summary>
    /// 解除手动锁定：恢复自动调度（下一探测周期起该客户端重新参与综合调度）。
    /// 手动切换（TryAssignServer）会置锁定；本方法还原为全自动。
    /// </summary>
    public bool TryUnpin(string clientId)
    {
        var session = FindAlive(clientId);
        if (session == null) return false;
        bool was;
        lock (_sync) { was = session.Pinned; session.Pinned = false; }
        Event(was
            ? $"已恢复自动调度：{clientId}（解除手动锁定，下一探测周期重新参与）"
            : $"{clientId} 本就未锁定（处于自动调度）");
        return true;
    }

    private ClientSession? FindAlive(string clientId)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(clientId, out var s) && s.Alive ? s : null;
        }
    }

    // ================= 快照（UI 线程拉取） =================

    public MasterSnapshot Snapshot()
    {
        var snap = new MasterSnapshot
        {
            Running = _running,
            Port = _cfg.Settings.ListenPort,
            Nodes = new List<NodeSnap>(),
            Clients = new List<ClientSnap>(),
            Events = new List<string>()
        };
        lock (_sync)
        {
            foreach (var node in _cfg.Nodes)
            {
                _probes.TryGetValue(node.Id, out var probe);
                var nowUtc = DateTime.UtcNow;
                double served = _sessions.Values.Count(c => IsOnline(c, nowUtc)
                    && (string.Equals(c.RouterId, node.Id, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ServerId, node.Id, StringComparison.OrdinalIgnoreCase)));
                bool pressureNode = node.Type == "router" || node.Type == "server";
                snap.Nodes.Add(new NodeSnap
                {
                    Id = node.Id,
                    Label = node.Label,
                    Type = node.Type,
                    Ip = node.Ip,
                    Pressure = pressureNode && _scores.TryGetValue(node.Id, out var sc) ? sc : -1,
                    RttMs = probe?.EmaRtt ?? -1,
                    LossPct = probe?.LossPct ?? 0,
                    ServedClients = (int)served,
                    Down = pressureNode && (probe?.Down ?? false)
                });
            }
            var now = DateTime.UtcNow;
            foreach (var s in _sessions.Values)
            {
                snap.Clients.Add(new ClientSnap
                {
                    ClientId = s.ClientId,
                    Hostname = s.Hostname,
                    Ip = s.Ip,
                    RouterId = s.RouterId,
                    ServerId = s.ServerId,
                    Priority = s.Priority,
                    ThrottleKbps = s.ThrottleKbps,
                    Pinned = s.Pinned,
                    Online = IsOnline(s, now),
                    RttMasterMs = s.LastMetrics?.RttMasterMs ?? -1,
                    RxKbps = s.LastMetrics?.RxKbps ?? 0,
                    TxKbps = s.LastMetrics?.TxKbps ?? 0,
                    LastAgeSec = (now - s.LastSeenUtc).TotalSeconds
                });
            }
            snap.Clients.Sort((a, b) => string.Compare(a.ClientId, b.ClientId, StringComparison.Ordinal));
            foreach (var kv in _history)
            {
                snap.PressureHistory[kv.Key] = new List<double>(kv.Value);
            }
            var evFrom = Math.Max(0, _events.Count - 120);
            snap.Events.AddRange(_events.GetRange(evFrom, _events.Count - evFrom));
        }
        return snap;
    }

    // ================= 内部部件 =================

    /// <summary>会话是否在线（Alive 且 15 秒内有心跳）：调度计压与界面展示统一口径。</summary>
    private static bool IsOnline(ClientSession s, DateTime nowUtc)
        => s.Alive && nowUtc - s.LastSeenUtc < TimeSpan.FromSeconds(15);

    private bool IsAllowedIp(string ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        if (IPAddress.TryParse(ip, out var addr) && IPAddress.IsLoopback(addr)) return true; // 本机自测
        return _cfg.Settings.AllowedPrefixes.Any(p => GalaNet.MatchesPrefix(ip, p));
    }

    private static List<string> LocalIpv4s()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToList();
        }
        catch { return new List<string>(); }
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

    private sealed class PressureProbe
    {
        private const int WindowSize = 12;
        private readonly Queue<bool> _window = new();
        public double EmaRtt = -1;
        public double LossPct;
        public bool Down;

        public void Feed(bool failed, double rttMs)
        {
            _window.Enqueue(!failed);
            while (_window.Count > WindowSize) _window.Dequeue();
            LossPct = _window.Count(x => !x) * 100.0 / _window.Count;
            Down = _window.All(x => !x);
            if (!failed && rttMs >= 0)
                EmaRtt = EmaRtt < 0 ? rttMs : EmaRtt * 0.6 + rttMs * 0.4;
        }
    }

    private sealed class ClientSession
    {
        public string ClientId = "";
        public string Hostname = "";
        public string Ip = "";
        public TcpClient? Tcp;
        public NetworkStream? Stream;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public MetricsMsg? LastMetrics;
        public string RouterId = "";
        public string ServerId = "";
        public int Priority = 1;
        public int ThrottleKbps;
        public bool Pinned; // 手动锁定：跳过自动调度，直到 TryUnpin/面板「恢复自动」
        public volatile bool Alive;
        public DateTime LastSwitchUtc = DateTime.MinValue;
    }
}
