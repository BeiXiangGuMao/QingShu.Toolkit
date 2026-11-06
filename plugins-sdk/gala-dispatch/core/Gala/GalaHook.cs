using System;
using System.Diagnostics;

namespace QingShu.Gala;

/// <summary>
/// assignment.json 变更钩子执行器：命令串中可用 <c>{assignment}</c> 占位符指代分配文件完整路径
/// （建议给占位符加引号防空格断参）；不含占位符时路径会作为最后一个参数追加。
/// 钩子在本机信任域内执行（配置者即本机管理员），失败仅留痕、不重试、不阻塞主流程。
/// </summary>
public static class GalaHook
{
    public static void Run(string? command, string assignmentPath, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        try
        {
            string args = command.Contains("{assignment}", StringComparison.Ordinal)
                ? command.Replace("{assignment}", assignmentPath)
                : command.TrimEnd() + " \"" + assignmentPath + "\"";

            // 首个 token = 可执行文件（支持带引号路径），其余 = 参数串原样传递
            string file, arguments;
            var trimmed = args.TrimStart();
            if (trimmed.StartsWith('"'))
            {
                int end = trimmed.IndexOf('"', 1);
                if (end < 0) { file = trimmed.Trim('"'); arguments = ""; }
                else { file = trimmed[1..end]; arguments = trimmed[(end + 1)..].TrimStart(); }
            }
            else
            {
                int sp = trimmed.IndexOf(' ');
                if (sp < 0) { file = trimmed; arguments = ""; }
                else { file = trimmed[..sp]; arguments = trimmed[(sp + 1)..]; }
            }

            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            log?.Invoke($"分配变更钩子已触发：{command}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"分配变更钩子执行失败：{ex.Message}（命令：{command}）");
        }
    }
}
