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
public sealed record EcoResult(int Applied, int Skipped);

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
            foreach (var pid in pids)
            {
                if (NativeMethods.SetProcessEcoQos(pid, enable)) applied++;
                else skipped++;
            }
            return new EcoResult(applied, skipped);
        });
}
