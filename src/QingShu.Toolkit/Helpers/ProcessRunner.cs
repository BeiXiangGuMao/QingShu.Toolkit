using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>命令行执行结果。</summary>
public sealed record CmdResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, string Error)
{
    public string FullText => (StdOut + (string.IsNullOrEmpty(StdErr) ? "" : "\n" + StdErr)).Trim();
}

/// <summary>
/// 静默命令执行器：统一经 cmd /c chcp 65001 以 UTF-8 读取输出，
/// 规避中文系统 GBK 控制台编码问题（netsh/arp/route/sc 等）。
/// </summary>
public static class ProcessRunner
{
    public static Task<CmdResult> RunAsync(string command, int timeoutMs = 30_000)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c chcp 65001>nul & {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using var p = Process.Start(psi)!;
                // 异步读取避免管道填满死锁
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return new CmdResult(-1, "", "", true, "命令执行超时");
                }
                return new CmdResult(p.ExitCode, outTask.Result ?? "", errTask.Result ?? "", false, "");
            }
            catch (Exception ex)
            {
                return new CmdResult(-1, "", "", false, ex.Message);
            }
        });
    }

    public static Task<CmdResult> RunNetshAsync(string args, int timeoutMs = 30_000) => RunAsync($"netsh {args}", timeoutMs);
}
