using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Gala;

namespace QingShu.GalaAgent;

/// <summary>
/// 元旦晚会智能分流 · 独立服务端（被控端，控制台常驻）。
/// 即插件面板中的「客户端」：部署在各节点机（舞台 / 投票 / 直播终端等），向主控端
/// 周期上报链路指标并执行指令，把当前分配落盘 assignment.json 供本机执行器消费。
/// 首选配置方式：exe 同目录 gala-agent.json（masterHost / port / pairingCode / dataFolder），
/// 命令行参数优先：QingShu.GalaAgent [--master 主控IP[:端口]] [--port 端口] [--pairing 配对码]
///                [--data 数据目录] [--config gala-agent.json]
/// </summary>
internal static class Program
{
    private const string DefaultConfigName = "gala-agent.json";

    private static readonly TaskCompletionSource ExitSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly CancellationTokenSource QuitCts = new();

    private static GalaAgent _agent = null!;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { } // 输出重定向时可能失败，忽略
        try { Console.Title = "元旦晚会智能分流 · 服务端"; } catch { }

        // —— 参数解析（命令行 > 配置文件 > 默认值） ——
        string configPath = DefaultConfigName;
        string masterArg = "", pairingArg = "", dataArg = "", hookArg = "";
        int? portArg = null;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length) configPath = args[++i];
            else if ((args[i] == "--master" || args[i] == "-m") && i + 1 < args.Length) masterArg = args[++i];
            else if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length
                     && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) portArg = p;
            else if ((args[i] == "--pairing" || args[i] == "-k") && i + 1 < args.Length) pairingArg = args[++i];
            else if ((args[i] == "--data" || args[i] == "-d") && i + 1 < args.Length) dataArg = args[++i];
            else if ((args[i] == "--hook" || args[i] == "-H") && i + 1 < args.Length) hookArg = args[++i];
        }
        configPath = Path.IsPathRooted(configPath)
            ? configPath
            : Path.Combine(AppContext.BaseDirectory, configPath);

        Console.WriteLine("================================================");
        Console.WriteLine("   元旦晚会智能分流 · 服务端（独立部署版）");
        Console.WriteLine("   （即插件面板中的「客户端」，被控端常驻程序）");
        Console.WriteLine("================================================");

        // —— 读取/生成配置文件 ——
        string masterHost = masterArg;
        int port = portArg ?? 0;
        string pairing = pairingArg;
        string dataFolder = dataArg;
        string hook = hookArg;
        if (File.Exists(configPath))
        {
            try
            {
                var file = JsonSerializer.Deserialize<AgentFileConfig>(File.ReadAllText(configPath));
                if (file != null)
                {
                    if (masterHost.Length == 0) masterHost = (file.MasterHost ?? "").Trim();
                    if (port == 0) port = file.Port;
                    if (pairing.Length == 0) pairing = (file.PairingCode ?? "").Trim();
                    if (dataFolder.Length == 0) dataFolder = (file.DataFolder ?? "").Trim();
                    if (hook.Length == 0) hook = (file.OnAssignmentChange ?? "").Trim();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 配置文件解析失败：{ex.Message}");
                Console.WriteLine($"配置文件：{configPath}");
                PauseIfInteractive();
                return 1;
            }
        }
        else if (masterArg.Length == 0)
        {
            // 首次运行且未给参数：写模板并退出（双击运行的用户能看到明确指引）
            try
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(new AgentFileConfig(),
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 配置模板生成失败：{ex.Message}");
            }
            Console.WriteLine($"[提示] 已生成配置模板：{configPath}");
            Console.WriteLine("请填写 masterHost（主控机 IP）后重新运行；也可直接用参数：");
            Console.WriteLine("  QingShu.GalaAgent --master 192.168.1.10 --pairing 配对码");
            PauseIfInteractive();
            return 0;
        }

        if (masterHost.Length == 0)
        {
            Console.WriteLine("[错误] 未指定主控地址：请在 gala-agent.json 填 masterHost，或加 --master 主控IP[:端口]");
            PauseIfInteractive();
            return 1;
        }
        if (masterHost.Contains(':'))
        {
            var seg = masterHost.Split(':', 2);
            masterHost = seg[0];
            if (port == 0 && int.TryParse(seg[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p2)) port = p2;
        }
        if (port is < 1 or > 65535) port = 47816;
        if (dataFolder.Length == 0) dataFolder = Path.Combine(AppContext.BaseDirectory, "gala-data");

        Console.WriteLine($"目标主控：{masterHost}:{port}" + (pairing.Length > 0 ? "（已填配对码）" : "（未填配对码）"));
        Console.WriteLine($"数据目录：{dataFolder}");
        if (hook.Length > 0) Console.WriteLine($"分配变更钩子：{hook}");
        Console.WriteLine();

        _agent = new GalaAgent(msg =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            GalaEventLog.Append(dataFolder, "gala-agent.log", msg); // 事件留痕落盘（1MB 轮转，复盘不依赖进程存活）
        }, dataFolder);
        // assignment.json 每次落盘 → 触发 --hook / onAssignmentChange 命令（本机执行器事件化对接）
        _agent.AssignmentChanged = path => GalaHook.Run(hook, path,
            msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));
        try
        {
            _agent.Start(masterHost, port, pairing); // 内部含断线 3 秒重连，主控未开时持续重试
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 服务端启动失败：{ex.Message}");
            PauseIfInteractive();
            return 1;
        }

        Console.WriteLine($"服务端已启动，分配文件：{_agent.AssignmentFile}");
        Console.WriteLine("（assign-executor.ps1 / 本机路由脚本按 assignment.json 消费；输入 help 查看命令）");
        Console.WriteLine();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            QuitCts.Cancel();
            ExitSignal.TrySetResult();
        };
        _ = Task.Run(CommandLoop);

        await ExitSignal.Task.ConfigureAwait(false);
        Console.WriteLine("正在停止服务端…");
        try { await _agent.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
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
                Console.WriteLine("status   查看连接与当前分配（服务器/路由/优先级/限速/指标）");
                Console.WriteLine("events   查看最近本机事件");
                Console.WriteLine("quit     停止服务端并退出");
                break;

            case "status":
                PrintStatus();
                break;

            case "events":
                foreach (var e in _agent.Snapshot().Events) Console.WriteLine($"  {e}");
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

    private static void PrintStatus()
    {
        var s = _agent.Snapshot();
        Console.WriteLine($"  连接：{(s.Running ? (s.Connected ? "已连接主控" : "等待重连（每 3 秒重试）") : "已停止")}  主控 {s.MasterHost}:{s.Port}");
        Console.WriteLine($"  分配：{s.ServerId}{(s.ServerIp.Length > 0 ? $"（{s.ServerIp}）" : "")}  经 {s.RouterId}" +
                          $"  优先级 {GalaDispatcher.PriorityNames[Math.Clamp(s.Priority, 0, 2)]}" +
                          $"  限速 {(s.ThrottleKbps > 0 ? s.ThrottleKbps + " kbps" : "-")}");
        Console.WriteLine($"  指标：RTT 主控 {FmtMs(s.RttMasterMs)} / 服务器 {FmtMs(s.RttServerMs)}  丢包 {s.LossPct:F0}%" +
                          $"  收/发 {s.RxKbps:F0}/{s.TxKbps:F0} kbps  进程 CPU {s.AppCpu:F0}%");
        Console.WriteLine($"  分配文件：{s.AssignmentFile}");
    }

    private static string FmtMs(double ms) => ms < 0 ? "-" : $"{ms:F0}ms";

    private static void PauseIfInteractive()
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive) return;
        try { Console.WriteLine("按回车键退出…"); Console.ReadLine(); } catch { }
    }

    /// <summary>gala-agent.json 结构（首次运行自动生成模板）。</summary>
    private sealed class AgentFileConfig
    {
        [JsonPropertyName("masterHost")] public string MasterHost { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; } = 47816;
        [JsonPropertyName("pairingCode")] public string PairingCode { get; set; } = "";
        [JsonPropertyName("dataFolder")] public string DataFolder { get; set; } = "";

        /// <summary>assignment.json 每次变更后执行的本机命令（{assignment} 占位符 = 分配文件路径；空 = 不启用钩子）。</summary>
        [JsonPropertyName("onAssignmentChange")] public string OnAssignmentChange { get; set; } = "";
    }
}
