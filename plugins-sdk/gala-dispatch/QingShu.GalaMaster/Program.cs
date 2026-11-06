using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Gala;

namespace QingShu.GalaMaster;

/// <summary>
/// 元旦晚会智能分流 · 独立主控端（控制台版）。
/// 与工具箱内插件共用同一份 GalaMaster 核心；适合把主控端放在固定机器上常驻
/// （远程桌面 / 计划任务均可）。拓扑配置与插件互通（gala-topology.json，同格式同语义）。
/// 用法：QingShu.GalaMaster [--config gala-topology.json] [--watch]
/// </summary>
internal static class Program
{
    private static readonly TaskCompletionSource ExitSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly CancellationTokenSource QuitCts = new();

    private static GalaTopologyConfig _cfg = new();
    private static GalaMaster _master = null!;
    private static string _configPath = "";
    private static volatile bool _watch;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { } // 输出重定向时可能失败，忽略
        try { Console.Title = "元旦晚会智能分流 · 主控端"; } catch { }

        string configArg = "gala-topology.json"; // 默认与 exe 同目录
        bool watchArg = false;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length) configArg = args[++i];
            else if (args[i] == "--watch" || args[i] == "-w") watchArg = true;
        }
        _configPath = Path.IsPathRooted(configArg)
            ? configArg
            : Path.Combine(AppContext.BaseDirectory, configArg);

        Console.WriteLine("================================================");
        Console.WriteLine("   元旦晚会智能分流 · 主控端（独立部署版）");
        Console.WriteLine("================================================");

        // 拓扑配置：首次运行生成模板并退出，等真实网络资料填入；解析失败给出明确指引
        if (!File.Exists(_configPath))
        {
            try
            {
                var created = GalaTopologyConfig.CreateTemplate();
                GalaTopologyConfig.Normalize(created);
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                File.WriteAllText(_configPath,
                    JsonSerializer.Serialize(created, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 模板配置生成失败：{ex.Message}");
                PauseIfInteractive();
                return 1;
            }
            Console.WriteLine($"[提示] 已生成拓扑配置模板：{_configPath}");
            Console.WriteLine("请编辑 nodes / links / routes 填入真实网络资料后，重新运行本程序。");
            Console.WriteLine("字段说明与插件面板「打开配置目录」中的 gala-topology.json 完全一致。");
            PauseIfInteractive();
            return 0;
        }

        try
        {
            var cfg = JsonSerializer.Deserialize<GalaTopologyConfig>(File.ReadAllText(_configPath))
                      ?? throw new InvalidDataException("配置文件格式不正确（反序列化为空）");
            GalaTopologyConfig.Normalize(cfg);
            _cfg = cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 拓扑配置解析失败：{ex.Message}");
            Console.WriteLine($"配置文件：{_configPath}（可对照模板修正 JSON 格式）");
            PauseIfInteractive();
            return 1;
        }

        Console.WriteLine($"配置文件：{_configPath}");
        Console.WriteLine($"拓扑规模：节点 {_cfg.Nodes.Count} · 链路 {_cfg.Links.Count} · 路由规则 {_cfg.Routes.Count}");
        Console.WriteLine($"监听端口：{_cfg.Settings.ListenPort} · 调度周期 {_cfg.Settings.TickSeconds}s" +
                          $" · 准入段 {string.Join(" / ", _cfg.Settings.AllowedPrefixes)}" +
                          (_cfg.Settings.PairingCode.Length > 0 ? " · 配对码已启用" : " · 未设配对码"));
        Console.WriteLine();

        var logDir = Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory;
        _master = new GalaMaster(_cfg, msg =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            GalaEventLog.Append(logDir, "gala-master.log", msg); // 事件留痕落盘（1MB 轮转，复盘不依赖进程存活）
        });
        try
        {
            _master.Start(); // 端口被占等异常由核心直接抛出，在此可见
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 主控端启动失败：{ex.Message}（常见原因：端口 {_cfg.Settings.ListenPort} 被占用）");
            PauseIfInteractive();
            return 1;
        }

        Console.WriteLine("主控端已启动。输入 help 查看命令，Ctrl+C 或 quit 退出。");
        Console.WriteLine();
        if (watchArg)
        {
            _watch = true;
            Console.WriteLine("[提示] 已开启 --watch：每 10 秒输出一行态势摘要（重定向输出时同样生效）。");
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            QuitCts.Cancel();
            ExitSignal.TrySetResult();
        };
        _ = Task.Run(CommandLoop);
        _ = Task.Run(WatchLoopAsync);

        await ExitSignal.Task.ConfigureAwait(false);
        Console.WriteLine("正在停止主控端…");
        try { await _master.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { }
        return 0;
    }

    // ================= 交互命令 =================

    private static void CommandLoop()
    {
        // 计划任务 / 服务方式运行（无交互输入）：保持常驻，直到 Ctrl+C 或结束进程
        if (Console.IsInputRedirected) return;
        while (!QuitCts.IsCancellationRequested)
        {
            string? line;
            try { line = Console.ReadLine(); }
            catch { return; }
            if (line == null) return; // 输入流关闭：继续常驻等待退出信号
            if (line.Trim().Length == 0) continue;
            try { HandleCommand(line.Trim()); }
            catch (Exception ex) { Console.WriteLine($"[错误] 命令执行失败：{ex.Message}"); }
        }
    }

    private static void HandleCommand(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "help":
            case "?":
                Console.WriteLine("status                      查看节点与客户端全景（= nodes + clients）");
                Console.WriteLine("nodes | clients             只看节点 / 只看客户端");
                Console.WriteLine("events                      查看最近主控事件");
                Console.WriteLine("watch on|off                每 10 秒输出一行态势摘要（默认 off）");
                Console.WriteLine("assign <客户端> <服务器>     手动切换某客户端线路");
                Console.WriteLine("priority <客户端> <0|1|2>    调整优先级（0 高 / 1 中 / 2 低）");
                Console.WriteLine("throttle <客户端> <kbps>     限速标记（0 = 解除，落盘 assignment.json）");
                Console.WriteLine("ping <客户端>                请求该客户端即时上报指标");
                Console.WriteLine("kick <客户端>                剔除会话（断开并移除，重连按新终端重新分配）");
                Console.WriteLine("params                      查看调度参数（周期/阈值/冷却/满载）");
                Console.WriteLine("setparam tick|threshold|cooldown|full <值>");
                Console.WriteLine("                            热调调度参数（下一探测周期生效并写回配置文件）");
                Console.WriteLine("quit                        停止主控并退出");
                break;

            case "status":
                PrintNodes();
                PrintClients();
                break;
            case "nodes":
                PrintNodes();
                break;
            case "clients":
                PrintClients();
                break;
            case "events":
                PrintEvents();
                break;

            case "watch":
                _watch = parts.Length > 1 && parts[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"态势摘要已{(_watch ? "开启（每 10 秒一行，watch off 关闭）" : "关闭")}");
                break;

            case "assign":
                if (parts.Length < 3) { Console.WriteLine("用法：assign <客户端> <服务器>"); break; }
                Console.WriteLine(TryCmd(parts[1], () => _master.TryAssignServer(parts[1], parts[2]),
                    $"已下发切换：{parts[1]} → {parts[2]}"));
                break;

            case "priority":
                if (parts.Length < 3 || !int.TryParse(parts[2], out int prio) || prio is < 0 or > 2)
                { Console.WriteLine("用法：priority <客户端> <0|1|2>（0 高 / 1 中 / 2 低）"); break; }
                Console.WriteLine(TryCmd(parts[1], () => _master.TrySetPriority(parts[1], prio),
                    $"已下发优先级：{parts[1]} → {GalaDispatcher.PriorityNames[Math.Clamp(prio, 0, 2)]}"));
                break;

            case "throttle":
                if (parts.Length < 3 || !int.TryParse(parts[2], out int kbps) || kbps < 0)
                { Console.WriteLine("用法：throttle <客户端> <kbps>（0 = 解除）"); break; }
                Console.WriteLine(TryCmd(parts[1], () => _master.TrySetThrottle(parts[1], kbps),
                    $"已下发限速标记：{parts[1]} → {(kbps > 0 ? kbps + " kbps" : "解除")}"));
                break;

            case "ping":
                if (parts.Length < 2) { Console.WriteLine("用法：ping <客户端>"); break; }
                Console.WriteLine(TryCmd(parts[1], () => _master.TryPingNow(parts[1]),
                    $"已请求 {parts[1]} 即时上报指标"));
                break;

            case "kick":
                if (parts.Length < 2) { Console.WriteLine("用法：kick <客户端>"); break; }
                Console.WriteLine(TryCmd(parts[1], () => _master.TryKick(parts[1]),
                    $"已剔除会话：{parts[1]}（重连将按新终端重新分配）"));
                break;

            case "params":
                Console.WriteLine($"调度参数：周期 {_cfg.Settings.TickSeconds}s · 阈值 {_cfg.Settings.SwitchThreshold:F0}" +
                                  $" · 冷却 {_cfg.Settings.SwitchCooldownSeconds}s · 满载 {_cfg.Settings.ClientsPerNodeFull} 台" +
                                  $" · 权重 RTT {_cfg.Settings.WeightRtt:F2} / 丢包 {_cfg.Settings.WeightLoss:F2}" +
                                  $" / 并发 {_cfg.Settings.WeightLoad:F2} / 带宽 {_cfg.Settings.WeightBandwidth:F2}");
                break;

            case "setparam":
                DoSetParam(parts);
                break;

            case "quit":
            case "exit":
                QuitCts.Cancel();
                ExitSignal.TrySetResult();
                break;

            default:
                Console.WriteLine($"未知命令 {parts[0]}，输入 help 查看可用命令");
                break;
        }
    }

    private static void DoSetParam(string[] parts)
    {
        if (parts.Length < 3 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
        {
            Console.WriteLine("用法：setparam <tick|threshold|cooldown|full> <数值>");
            return;
        }
        switch (parts[1].ToLowerInvariant())
        {
            case "tick": _cfg.Settings.TickSeconds = Math.Max(1, (int)val); break;
            case "threshold": _cfg.Settings.SwitchThreshold = val > 0 ? val : _cfg.Settings.SwitchThreshold; break;
            case "cooldown": _cfg.Settings.SwitchCooldownSeconds = Math.Max(0, (int)val); break;
            case "full": _cfg.Settings.ClientsPerNodeFull = Math.Max(1, (int)val); break;
            default:
                Console.WriteLine($"未知参数 {parts[1]}（可选 tick / threshold / cooldown / full）");
                return;
        }
        // 与插件面板热调同语义：改内存同一配置实例，调度循环每 tick 重读；写回配置文件持久化
        try
        {
            File.WriteAllText(_configPath,
                JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"调度参数已更新：周期 {_cfg.Settings.TickSeconds}s · 阈值 {_cfg.Settings.SwitchThreshold:F0}" +
                              $" · 冷却 {_cfg.Settings.SwitchCooldownSeconds}s · 满载 {_cfg.Settings.ClientsPerNodeFull} 台" +
                              "（下一探测周期自动生效，并已写回配置文件）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[警告] 参数已在内存生效，但写回配置文件失败：{ex.Message}");
        }
    }

    /// <summary>false = 客户端不在线或不存在；指令是否送达以事件留痕为准（发送失败另有失败事件）。</summary>
    private static string TryCmd(string clientId, Func<bool> action, string okText)
    {
        return action() ? okText : $"客户端 {clientId} 不在线或不存在（可先用 clients 查看在线列表）";
    }

    // ================= 展示 =================

    private static void PrintNodes()
    {
        var snap = _master.Snapshot();
        Console.WriteLine($"—— 节点（监听 {snap.Port} · 调度周期 {_cfg.Settings.TickSeconds}s）——");
        foreach (var n in snap.Nodes)
        {
            string pressure = n.Down ? "离线" : n.Pressure >= 0 ? $"压力 {n.Pressure,5:F1}" : "不计压力";
            Console.WriteLine($"  [{n.Type,-6}] {n.Id,-8} {n.Label,-10} {n.Ip,-15} {pressure}" +
                              $"  RTT {FmtMs(n.RttMs)}  丢包 {n.LossPct,4:F0}%  服务 {n.ServedClients}");
        }
    }

    private static void PrintClients()
    {
        var snap = _master.Snapshot();
        int online = snap.Clients.Count(c => c.Online);
        Console.WriteLine($"—— 客户端（在线 {online}/{snap.Clients.Count}）——");
        foreach (var c in snap.Clients)
        {
            Console.WriteLine($"  {c.ClientId,-16} {(c.Online ? "在线" : "离线")}  {c.Ip,-15} → {c.ServerId,-6}（经 {c.RouterId,-6}）" +
                              $"  优先级 {GalaDispatcher.PriorityNames[Math.Clamp(c.Priority, 0, 2)]}" +
                              $"  限速 {(c.ThrottleKbps > 0 ? c.ThrottleKbps + " kbps" : "-")}" +
                              $"  RTT {FmtMs(c.RttMasterMs)}  收/发 {c.RxKbps,6:F0}/{c.TxKbps,6:F0} kbps  主机 {c.Hostname}");
        }
    }

    private static void PrintEvents()
    {
        var snap = _master.Snapshot();
        Console.WriteLine($"—— 最近主控事件（{snap.Events.Count} 条）——");
        foreach (var e in snap.Events) Console.WriteLine($"  {e}");
    }

    private static async Task WatchLoopAsync()
    {
        while (!QuitCts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), QuitCts.Token).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
            if (!_watch) continue;
            try
            {
                var snap = _master.Snapshot();
                int okNodes = snap.Nodes.Count(n => n.Type is "router" or "server" && !n.Down);
                int allNodes = snap.Nodes.Count(n => n.Type is "router" or "server");
                var worst = snap.Nodes.Where(n => n.Pressure >= 0).OrderByDescending(n => n.Pressure).FirstOrDefault();
                int online = snap.Clients.Count(c => c.Online);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 态势：节点正常 {okNodes}/{allNodes} · 客户端在线 {online}/{snap.Clients.Count}" +
                                  (worst != null ? $" · 最高压力 {worst.Id}={worst.Pressure:F0}" : ""));
            }
            catch { } // 态势行尽力而为，不打断主流程
        }
    }

    private static string FmtMs(double ms) => ms < 0 ? "  -  " : $"{ms,4:F0}ms";

    private static void PauseIfInteractive()
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive) return;
        try { Console.WriteLine("按回车键退出…"); Console.ReadLine(); } catch { }
    }
}
