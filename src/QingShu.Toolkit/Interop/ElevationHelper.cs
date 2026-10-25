using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Interop;

/// <summary>
/// 提权辅助：按需 UAC 策略。日常普通权限运行；
/// 管理类修复动作由 NetworkRepairService 合并为单个脚本一次性提升执行。
/// </summary>
public static class ElevationHelper
{
    /// <summary>当前进程是否已以管理员运行。</summary>
    public static bool IsCurrentUserAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 以管理员身份运行一个 cmd 批处理脚本（触发一次 UAC）。
    /// 脚本内部应输出 ##OK##/##FAIL## 标记到 stdout，stdout 被重定向到 outLog。
    /// </summary>
    public static Task<(int ExitCode, string Output)> RunElevatedScriptAsync(string scriptPath, string outLog, int timeoutMs = 300_000)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c \"\"{scriptPath}\" > \"{outLog}\" 2>&1\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return (-1, "##FAIL## 提权脚本执行超时");
                }
                var output = File.Exists(outLog) ? File.ReadAllText(outLog) : "";
                return (p.ExitCode, output ?? "");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC 弹窗被用户取消：历史 bug 此处抛异常直接中断整轮清理（普通目标已清完但后续流程全部中断）
                return (-1, "##UAC_CANCELLED##");
            }
            catch (Exception ex)
            {
                return (-1, $"##FAIL## 无法启动提权脚本：{ex.Message}");
            }
        });
    }

    /// <summary>以普通（非提权）方式静默运行 cmd 命令并捕获输出。</summary>
    public static Task<(int ExitCode, string Output)> RunCmdAsync(string command, int timeoutMs = 120_000)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return (-1, "##FAIL## 命令执行超时");
            }
            return (p.ExitCode, p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd());
        });
    }
}
