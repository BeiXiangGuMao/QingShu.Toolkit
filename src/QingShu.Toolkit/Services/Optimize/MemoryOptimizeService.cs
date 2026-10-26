using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;

namespace QingShu.Toolkit.Services.Optimize;

public sealed record ProcessEntry(string Name, int Pid, long WorkingSetBytes, long CpuTimeMs, bool IsEcoCapable);
public sealed record TrimResult(int Trimmed, int Skipped, long FreedBytes);
/// <summary>Detail：按失败原因分组的补充提示（空串 = 全部成功）。</summary>
public sealed record EcoResult(int Applied, int Skipped, string Detail);

/// <summary>
/// 内存整理服务 —— 明确的安全边界：
///  · 只做“工作集整理”（EmptyWorkingSet，等价于任务管理器里的“提交内存回收”提示，进程不会被结束）
///  · 不修改任何系统配置，不结束任何进程
///  · 对选中进程应用/恢复 EcoQoS（功耗遏制），回收 CPU 占用
/// </summary>
public interface IMemoryOptimizeService
{
    Task<List<ProcessEntry>> ListProcessesAsync(int top = 25);
    Task<TrimResult> TrimAsync(IEnumerable<int> pids);
    Task<EcoResult> SetEcoAsync(IEnumerable<int> pids, bool enable);
    /// <summary>本机是否支持 EcoQoS（版本 ≥ Win10 1709 且对自身进程设置成功）。结果进程内缓存。</summary>
    bool IsEcoSupported();
}

public sealed class MemoryOptimizeService : IMemoryOptimizeService
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memcompression", "csrss", "wininit", "winlogon",
        "services", "lsass", "smss", "dwm", "svchost", "fontdrvhost", "sihost",
        "taskhostw", "audiodg", "securityhealthservice", "securityhealthsystray",
        "explorer", "applicationframehost", "runtimebroker"
    };

    public Task<List<ProcessEntry>> ListProcessesAsync(int top = 25)
        => Task.Run(() =>
        {
            var list = new List<ProcessEntry>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (Protected.Contains(p.ProcessName)) continue;
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    long cpuMs = 0;
                    try { cpuMs = (long)(p.TotalProcessorTime.TotalMilliseconds); } catch { }
                    list.Add(new ProcessEntry(p.ProcessName, p.Id, ws, cpuMs, true));
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return list.OrderByDescending(x => x.WorkingSetBytes).Take(top).ToList();
        });

    public Task<TrimResult> TrimAsync(IEnumerable<int> pids)
        => Task.Run(() =>
        {
            int trimmed = 0, skipped = 0;
            long freed = 0;
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    long before = 0;
                    try { before = p.WorkingSet64; } catch { }
                    var r = NativeMethods.TryEmptyWorkingSet(pid);
                    if (r < 0) { skipped++; continue; }
                    p.Refresh();
                    long after = 0;
                    try { after = p.WorkingSet64; } catch { }
                    freed += Math.Max(0, before - after);
                    trimmed++;
                }
                catch { skipped++; }
            }
            return new TrimResult(trimmed, skipped, freed);
        });

    public Task<EcoResult> SetEcoAsync(IEnumerable<int> pids, bool enable)
        => Task.Run(() =>
        {
            int applied = 0, skipped = 0;
            int openFail = 0, setFail = 0, osFail = 0;
            foreach (var pid in pids)
            {
                if (NativeMethods.TrySetProcessEcoQos(pid, enable, out var reason)) applied++;
                else
                {
                    skipped++;
                    switch (reason)
                    {
                        case "os": osFail++; break;
                        case "set": setFail++; break;
                        default: openFail++; break;
                    }
                }
            }
            // 失败原因细分，给用户可解释的提示（历史反馈「进程节能不可用」无任何原因可看）
            var parts = new List<string>();
            if (openFail > 0) parts.Add($"{openFail} 个进程无法打开（受保护或权限更高）");
            if (setFail > 0) parts.Add($"{setFail} 个被系统拒绝（前台活跃进程会被自动豁免）");
            if (osFail > 0) parts.Add($"{osFail} 个因系统过旧不支持");
            return new EcoResult(applied, skipped, string.Join("；", parts));
        });

    private static int? _ecoSupported;

    /// <summary>EcoQoS 支持性预检：系统版本 ≥ Win10 1709 且能对自身进程应用/还原一次。
    /// 仅「系统过旧」判定为永久不支持并缓存；其余意外失败（权限/瞬时）不缓存，
    /// 下次进入页面重试 —— 避免一次瞬态失败把入口永久锁死。</summary>
    public bool IsEcoSupported()
    {
        if (_ecoSupported is int cached) return cached == 1;
        bool okSet = NativeMethods.TrySetProcessEcoQos(Environment.ProcessId, true, out var err);
        bool ok = okSet && NativeMethods.TrySetProcessEcoQos(Environment.ProcessId, false, out _);
        if (ok) _ecoSupported = 1;
        else if (err == "os") _ecoSupported = 0;
        return ok;
    }
}
