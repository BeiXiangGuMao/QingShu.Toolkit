using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Services.Optimize;

/// <summary>Origin：HKCU / HKLM = 注册表 Run 键；Folder = 启动文件夹。</summary>
public sealed record StartupEntry(string Name, string Command, string Source, string Origin, bool Enabled);

/// <summary>
/// 启动项管理服务：查看 + 应用内禁用/启用（历史版本只读，2026-10 应用户需求开放管理）。
/// 安全边界：只动「用户级 Run 键 / 公共 Run 键 / 启动文件夹」三类标准自启动位置，
/// 禁用均为可逆操作 —— 注册表值先备份到本地 JSON、启动文件夹文件移入备份目录，启用时原样恢复；
/// 不触碰计划任务 / 服务 / WMI 永久订阅。HKLM 写入需要管理员权限，权限不足时明确提示而不静默失败。
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
