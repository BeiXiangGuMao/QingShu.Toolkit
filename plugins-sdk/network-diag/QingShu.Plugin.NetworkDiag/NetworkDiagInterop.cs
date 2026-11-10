using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace QingShu.Plugin.NetworkDiag;

// ============================================================================
// 自包含底层互操作（宿主 Helpers / Interop 同源自包含移植，零宿主内部依赖）：
//   FormatHelper / ProcessRunner / ElevationHelper / NativeKernel32 / NativeWinINet
// 仅保留网络诊断域实际用到的成员；行为与内置版逐行对应，内置页回退后表现一致。
// 宿主侧调用点对账：NetworkRepairService 提权批处理（RunElevatedScriptAsync）与
// 代理复位刷新（NotifyProxySettingsChanged）、诊断检查的 netsh/route/arp/tracert/netstat
// 命令执行（ProcessRunner）、修复脚本与报告的字节/时长格式化（FormatHelper）、
// 设备内存探测（GetMemoryStatus）。
// ============================================================================

/// <summary>展示格式化工具（宿主 Helpers/FormatHelper 同源移植，仅保留本域用到的 Bytes/Duration）。</summary>
internal static class FormatHelper
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }

    public static string Duration(long ms) => ms switch
    {
        < 1000 => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:0.#} s",
        _ => $"{ms / 60000} min {(ms % 60000) / 1000} s"
    };
}

/// <summary>命令行执行结果（宿主 Helpers/ProcessRunner 的 CmdResult 同源移植）。</summary>
public sealed record CmdResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, string Error)
{
    public string FullText => (StdOut + (string.IsNullOrEmpty(StdErr) ? "" : "\n" + StdErr)).Trim();
}

/// <summary>
/// 静默命令执行器（宿主 Helpers/ProcessRunner 同源移植）：统一经 cmd /c chcp 65001 以 UTF-8
/// 读取输出，规避中文系统 GBK 控制台编码问题（netsh/arp/route/tracert/netstat 等）。
/// 超时杀进程仍带回部分输出（诊断挂死命令时有用），管道异步读取防填满死锁。
/// </summary>
internal static class ProcessRunner
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
                    // 超时也尽量带回已产出的部分输出（诊断挂死命令时有用）；读取任务最多再等 2 秒
                    return new CmdResult(-1, ReadDrained(outTask, 2000), ReadDrained(errTask, 2000), true, "命令执行超时");
                }
                // 进程已退出、管道必然 EOF；等待上限兜底防异常场景卡死
                return new CmdResult(p.ExitCode, ReadDrained(outTask, 5000), ReadDrained(errTask, 5000), false, "");
            }
            catch (Exception ex)
            {
                return new CmdResult(-1, "", "", false, ex.Message);
            }
        });
    }

    /// <summary>在时限内等读取任务完成并取全部输出；未完成/失败返回空串（不无限阻塞）。</summary>
    private static string ReadDrained(Task<string> readTask, int waitMs)
    {
        try { return readTask.Wait(waitMs) ? (readTask.Result ?? "") : ""; }
        catch { return ""; }
    }
}

/// <summary>
/// 提权辅助（宿主 Interop/ElevationHelper 同源移植，仅保留本域用到的两个成员）：
/// 日常普通权限运行；管理类修复动作由修复服务合并为单个脚本一次性提升执行。
/// </summary>
internal static class ElevationHelper
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
                // UAC 弹窗被用户取消：历史 bug 此处抛异常直接中断整轮修复（普通动作已执行完但后续流程全部中断）
                return (-1, "##UAC_CANCELLED##");
            }
            catch (Exception ex)
            {
                return (-1, $"##FAIL## 无法启动提权脚本：{ex.Message}");
            }
        });
    }
}

/// <summary>
/// 内存状态 Win32 查询（宿主 Interop/NativeMethods 内存区段同源移植，硬件探测专用）：
/// GlobalMemoryStatusEx 取物理内存总量，供 PDF 报告设备信息表使用。
/// </summary>
internal static class NativeKernel32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public static (uint LoadPercent, ulong TotalPhys, ulong AvailPhys) GetMemoryStatus()
    {
        var m = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(m)) return (m.dwMemoryLoad, m.ullTotalPhys, m.ullAvailPhys);
        return (0, 0, 0);
    }
}

/// <summary>
/// IP Helper ARP 主动探测（IP 地址冲突检测专用，宿主无对应物——冲突检测为插件版新增）：
/// SendARP 向目标 IPv4 发 ARP 请求并取回应答 MAC。探测本机 IP 时，若应答 MAC 不属于
/// 本机任何适配器，即网内有另一台主机占用本机 IP（冲突实锤）；无应答（非零返回码）
/// 视为无人冒用。IP 转 uint 沿用 BitConverter.ToUInt32 惯例（与小端内存布局下的
/// inet_addr 返回值一致，PInvoke.net 标准写法）。
/// </summary>
internal static class NativeIphlpapi
{
    private const uint NO_ERROR = 0;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    /// <summary>
    /// 向目标 IPv4 发一次 ARP 请求。返回应答 MAC（"AA-BB-CC-DD-EE-FF" 大写）；
    /// null = 无应答或探测失败（网络不可达/权限异常），调用方按「无人冒用」与
    /// 「探测受限」两类结合其他证据判定。
    /// </summary>
    public static string? ProbeMac(System.Net.IPAddress ip)
    {
        try
        {
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return null;
            uint dest = BitConverter.ToUInt32(bytes, 0);
            var mac = new byte[6];
            uint len = 6;
            if (SendARP(dest, 0, mac, ref len) != NO_ERROR || len < 6) return null;
            return string.Join("-", mac.Take(6).Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }
}

/// <summary>
/// WinINet 代理设置刷新（宿主 Interop/NativeMethods WinINet 区段同源移植，仅保留通知项）：
/// 关闭用户级系统代理后立即通知系统刷新，避免“已改注册表但浏览器仍走旧代理”。
/// </summary>
internal static class NativeWinINet
{
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    public static void NotifyProxySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
