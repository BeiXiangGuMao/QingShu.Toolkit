using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QingShu.Plugin.Optimize;

// ================= 优化服务（宿主 Services/Optimize 两服务同源移植） =================
// 启动项禁用备份沿用宿主同一路径（%LocalAppData%\QingShu.Toolkit\startup_disabled.json 与
// startup_disabled_files\）——内置/插件共用，回退兼容人工寻迹一致（usb 批同款口径）。

public sealed record ProcessEntry(string Name, int Pid, long WorkingSetBytes, long CpuTimeMs, bool IsEcoCapable);
public sealed record TrimResult(int Trimmed, int Skipped, long FreedBytes);
/// <summary>内存整理逐进程进度（Done/Total 驱动确定性进度条，Name 实时显示）。</summary>
public sealed record TrimProgress(int Done, int Total, string Name);
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
    /// <summary>逐进程整理工作集。progress 实时上报进度条；名单内受保护进程二次拦截计数。</summary>
    Task<TrimResult> TrimAsync(IEnumerable<int> pids, IProgress<TrimProgress>? progress);
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
        "explorer", "applicationframehost", "runtimebroker",
        // 插件版新增：清工作集会触发重载/重扫反而更耗资源或影响系统稳定的关键服务
        "msmpeng",          // Defender：清空工作集触发全量重新扫描
        "wudfhost",         // 用户态驱动框架宿主
        "searchindexer",    // Windows 搜索索引：清空后重新爬盘索引
        "spoolsv"           // 打印服务（打印队列中后台服务，不稳定因素）
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

    public Task<TrimResult> TrimAsync(IEnumerable<int> pids, IProgress<TrimProgress>? progress)
        => Task.Run(() =>
        {
            var list = (pids as IList<int>) ?? pids.ToList();
            int trimmed = 0, skipped = 0, done = 0;
            long freed = 0;
            foreach (var pid in list)
            {
                var name = "";
                try { name = Process.GetProcessById(pid).ProcessName; } catch { }
                progress?.Report(new TrimProgress(done, list.Count, name));
                try
                {
                    using var p = Process.GetProcessById(pid);
                    // 二次拦截：列表是进入页面时快照，执行时可能已有进程换成受保护名单（或名单新增后旧列表残留）
                    if (Protected.Contains(p.ProcessName)) { skipped++; done++; continue; }
                    long before = 0;
                    try { before = p.WorkingSet64; } catch { }
                    var r = NativeOptimize.TryEmptyWorkingSet(pid);
                    if (r < 0) { skipped++; done++; continue; }
                    p.Refresh();
                    long after = 0;
                    try { after = p.WorkingSet64; } catch { }
                    freed += Math.Max(0, before - after);
                    trimmed++;
                }
                catch { skipped++; }
                done++;
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
                if (NativeOptimize.TrySetProcessEcoQos(pid, enable, out var reason)) applied++;
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
        bool okSet = NativeOptimize.TrySetProcessEcoQos(Environment.ProcessId, true, out var err);
        bool ok = okSet && NativeOptimize.TrySetProcessEcoQos(Environment.ProcessId, false, out _);
        if (ok) _ecoSupported = 1;
        else if (err == "os") _ecoSupported = 0;
        return ok;
    }
}

/// <summary>Origin：HKCU / HKLM = 注册表 Run 键；Folder = 启动文件夹。</summary>
public sealed record StartupEntry(string Name, string Command, string Source, string Origin, bool Enabled);

/// <summary>
/// 启动项管理服务（宿主 Services/Optimize/StartupListService 同源移植）：
/// 查看 + 应用内禁用/启用。安全边界：只动「用户级 Run 键 / 公共 Run 键 / 启动文件夹」
/// 三类标准自启动位置，禁用均为可逆操作 —— 注册表值先备份到本地 JSON、启动文件夹文件移入
/// 备份目录，启用时原样恢复；不触碰计划任务 / 服务 / WMI 永久订阅。
/// HKLM 写入需要管理员权限，权限不足时明确提示而不静默失败。
/// 备份路径沿用宿主同路径（%LocalAppData%\QingShu.Toolkit\）——插件卸载后内置页接管同一份备份记录。
/// </summary>
public interface IStartupListService
{
    Task<List<StartupEntry>> GetAsync();
    /// <summary>禁用启动项（先备份再移除）。返回 (是否成功, 失败原因)。</summary>
    Task<(bool Ok, string Error)> DisableAsync(StartupEntry entry);
    /// <summary>启用启动项（从备份恢复原值/原位置）。</summary>
    Task<(bool Ok, string Error)> EnableAsync(StartupEntry entry);
}

public sealed class StartupListService : IStartupListService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit");
    private static string BackupJsonPath => Path.Combine(StoreDir, "startup_disabled.json");
    private static string BackupFileDir => Path.Combine(StoreDir, "startup_disabled_files");

    private static readonly object Sync = new();

    /// <summary>禁用记录（JSON 持久化）。internal：System.Text.Json 反序列化要求类型可访问。</summary>
    internal sealed record DisabledItem(string Name, string Command, string Origin);

    public Task<List<StartupEntry>> GetAsync() => Task.Run(() =>
    {
        var list = new List<StartupEntry>();
        lock (Sync)
        {
            void ReadRun(RegistryKey baseKey, string source, string origin)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(RunKeyPath);
                    if (key == null) return;
                    foreach (var name in key.GetValueNames())
                    {
                        var cmd = key.GetValue(name)?.ToString() ?? "";
                        if (cmd.Length > 0) list.Add(new StartupEntry(name, cmd, source, origin, true));
                    }
                }
                catch { }
            }
            ReadRun(Registry.CurrentUser, "HKCU\\...\\Run", "HKCU");
            ReadRun(Registry.LocalMachine, "HKLM\\...\\Run", "HKLM");

            try
            {
                foreach (var folder in new[]
                         {
                             Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                             Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                         })
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (var f in Directory.EnumerateFiles(folder))
                        list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f), f, "启动文件夹", "Folder", true));
                }
            }
            catch { }

            // 已禁用项：注册表值备份 + 启动文件夹备份文件，统一标注「已禁用」
            try
            {
                foreach (var item in ReadBackupJson())
                    list.Add(new StartupEntry(item.Name, item.Command,
                        item.Origin == "Folder" ? "启动文件夹（已移出）" : $"{(item.Origin == "HKLM" ? "HKLM" : "HKCU")}\\...\\Run（已禁用）",
                        item.Origin, false));
            }
            catch { }

            try
            {
                if (Directory.Exists(BackupFileDir))
                    foreach (var f in Directory.EnumerateFiles(BackupFileDir))
                        list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(f), f,
                            "启动文件夹（已移出）", "Folder", false));
            }
            catch { }
        }
        return list.OrderBy(x => x.Source).ThenBy(x => x.Name).ToList();
    });

    public Task<(bool Ok, string Error)> DisableAsync(StartupEntry entry) => Task.Run(() =>
    {
        lock (Sync)
        {
            try
            {
                switch (entry.Origin)
                {
                    case "HKCU":
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                        if (key == null) return (false, "打开 HKCU Run 键失败。");
                        var cmd = key.GetValue(entry.Name)?.ToString() ?? "";
                        if (cmd.Length == 0) return (false, $"未找到启动项「{entry.Name}」（可能已被移除，请刷新）。");
                        AddBackupJson(new DisabledItem(entry.Name, cmd, "HKCU"));
                        key.DeleteValue(entry.Name, throwOnMissingValue: false);
                        return (true, "");
                    }
                    case "HKLM":
                    {
                        if (!ElevationHelper.IsCurrentUserAdmin())
                            return (false, "系统级启动项（HKLM）需要管理员权限：请以管理员身份运行本工具后重试。");
                        using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: true);
                        if (key == null) return (false, "打开 HKLM Run 键失败（权限不足或键不存在）。");
                        var cmd = key.GetValue(entry.Name)?.ToString() ?? "";
                        if (cmd.Length == 0) return (false, $"未找到启动项「{entry.Name}」（可能已被移除，请刷新）。");
                        AddBackupJson(new DisabledItem(entry.Name, cmd, "HKLM"));
                        key.DeleteValue(entry.Name, throwOnMissingValue: false);
                        return (true, "");
                    }
                    case "Folder":
                    {
                        if (!File.Exists(entry.Command)) return (false, "启动文件不存在（可能已被移除，请刷新）。");
                        Directory.CreateDirectory(BackupFileDir);
                        var target = Path.Combine(BackupFileDir, Path.GetFileName(entry.Command));
                        File.Move(entry.Command, target, overwrite: true);
                        return (true, "");
                    }
                    default:
                        return (false, "未知来源，无法操作。");
                }
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "权限不足：该启动项需要管理员权限才能禁用。");
            }
            catch (Exception ex)
            {
                return (false, $"操作失败：{ex.Message}");
            }
        }
    });

    public Task<(bool Ok, string Error)> EnableAsync(StartupEntry entry) => Task.Run(() =>
    {
        lock (Sync)
        {
            try
            {
                switch (entry.Origin)
                {
                    case "HKCU":
                    {
                        var item = ReadBackupJson().FirstOrDefault(x => x.Name == entry.Name && x.Origin == "HKCU");
                        if (item == null) return (false, "未找到备份记录（请刷新列表）。");
                        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                        key.SetValue(item.Name, item.Command);
                        RemoveBackupJson(item);
                        return (true, "");
                    }
                    case "HKLM":
                    {
                        if (!ElevationHelper.IsCurrentUserAdmin())
                            return (false, "系统级启动项（HKLM）需要管理员权限：请以管理员身份运行本工具后重试。");
                        var item = ReadBackupJson().FirstOrDefault(x => x.Name == entry.Name && x.Origin == "HKLM");
                        if (item == null) return (false, "未找到备份记录（请刷新列表）。");
                        using var key = Registry.LocalMachine.OpenSubKey(RunKeyPath, writable: true)
                                        ?? Registry.LocalMachine.CreateSubKey(RunKeyPath);
                        key.SetValue(item.Name, item.Command);
                        RemoveBackupJson(item);
                        return (true, "");
                    }
                    case "Folder":
                    {
                        if (!File.Exists(entry.Command)) return (false, "备份文件不存在（请刷新列表）。");
                        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                        Directory.CreateDirectory(folder);
                        var target = Path.Combine(folder, Path.GetFileName(entry.Command));
                        File.Move(entry.Command, target, overwrite: true);
                        return (true, "");
                    }
                    default:
                        return (false, "未知来源，无法操作。");
                }
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "权限不足：该启动项需要管理员权限才能启用。");
            }
            catch (Exception ex)
            {
                return (false, $"操作失败：{ex.Message}");
            }
        }
    });

    // ================= 备份 JSON（注册表值禁用记录） =================

    private static List<DisabledItem> ReadBackupJson()
    {
        if (!File.Exists(BackupJsonPath)) return new List<DisabledItem>();
        var json = File.ReadAllText(BackupJsonPath);
        if (string.IsNullOrWhiteSpace(json)) return new List<DisabledItem>();
        return JsonSerializer.Deserialize<List<DisabledItem>>(json) ?? new List<DisabledItem>();
    }

    private static void WriteBackupJson(List<DisabledItem> items)
        => File.WriteAllText(BackupJsonPath, JsonSerializer.Serialize(items,
            new JsonSerializerOptions { WriteIndented = true }));

    private static void AddBackupJson(DisabledItem item)
    {
        var items = ReadBackupJson();
        items.RemoveAll(x => x.Name == item.Name && x.Origin == item.Origin);
        items.Add(item);
        Directory.CreateDirectory(StoreDir);
        WriteBackupJson(items);
    }

    private static void RemoveBackupJson(DisabledItem item)
    {
        var items = ReadBackupJson();
        items.RemoveAll(x => x.Name == item.Name && x.Origin == item.Origin);
        WriteBackupJson(items);
    }
}
