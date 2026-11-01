using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;

namespace QingShu.Toolkit.Services.Usb;

public interface IUsbRepairService
{
    /// <summary>按设备当前状态生成修复清单（危险项默认不勾选）。</summary>
    Task<List<UsbFixAction>> BuildPlanAsync(UsbDrive drive);
    /// <summary>执行选中的修复：普通动作本进程完成，管理类合并为一次 UAC。</summary>
    Task RunAsync(UsbDrive drive, IReadOnlyList<UsbFixAction> actions, IProgress<UsbFixAction>? progress, CancellationToken ct);
    /// <summary>重建分区表并快速格式化（破坏性，调用方必须已完成二次确认）。</summary>
    Task<UsbFixAction> RebuildPartitionAsync(UsbDrive drive, string fileSystem, IProgress<UsbFixAction>? progress, CancellationToken ct);
}

/// <summary>U盘修复动作（XAML 可绑定行模型）。</summary>
public sealed partial class UsbFixAction : ObservableObject
{
    public UsbFixAction(string id, string title, string detail, bool needsAdmin, bool risky)
    {
        Id = id; Title = title; Detail = detail; NeedsAdmin = needsAdmin; Risky = risky;
    }

    public string Id { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool NeedsAdmin { get; }
    public bool Risky { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "Pending"; // Pending / Running / Ok / Fail
    [ObservableProperty] private string _output = "";

    public string AdminText => NeedsAdmin ? "需要管理员" : "普通权限";
    public string StatusText => Status switch
    {
        "Running" => "执行中", "Ok" => "成功", "Fail" => "失败", _ => "待执行"
    };
    public bool OkVis => Status == "Ok";
    public bool FailVis => Status == "Fail";
    public bool RunningVis => Status == "Running";
    public bool PendingVis => Status is "Pending" or "Fail";
}

/// <summary>
/// U盘修复（硬件级可达范围）：设备栈重置（PnP 重新枚举）/ 卷配置刷新 / 文件系统查错 /
/// 写保护策略清除 / 盘符重建 / 分区表重建。安全边界：绝不对系统卷执行任何动作；
/// 破坏性动作（重建分区）必须由 UI 二次确认后单独调用。
/// </summary>
public sealed class UsbRepairService : IUsbRepairService
{
    public async Task<List<UsbFixAction>> BuildPlanAsync(UsbDrive drive)
    {
        var plan = new List<UsbFixAction>
        {
            new("refresh_volume", "更新设备与卷配置",
                "刷新系统对该U盘的容量/分区缓存（IOCTL_DISK_UPDATE_PROPERTIES），修复容量显示异常与盘符残影。", false, false),
            new("device_reset", "重置U盘设备栈",
                "禁用并重新启用 USB 存储设备（PnP 级重新枚举），修复识别异常、盘符丢失、设备假死。", true, false)
        };

        var main = drive.Volumes.OrderByDescending(v => v.TotalBytes).FirstOrDefault();
        if (main != null)
        {
            plan.Add(new UsbFixAction("chkdsk_fix", $"文件系统查错修复（{main.Letter}:）",
                $"chkdsk {main.Letter}: /f 修复文件系统错误与坏簇记录，解决目录损坏/文件消失/拷贝报错。需要管理员。", true, false));
            plan.Add(new UsbFixAction("clear_readonly", "清除软件写保护",
                "diskpart 清除磁盘/卷级只读策略（针对误设只读导致无法写入的情况；硬件写保护开关无效）。需要管理员。", true, false));
        }
        else
        {
            plan.Add(new UsbFixAction("assign_letter", "重建盘符映射",
                "diskpart 对该盘第一分区重新分配盘符，修复「识别到设备但没有盘符」。需要管理员。", true, false));
        }

        foreach (var a in plan) a.IsSelected = !a.Risky;
        return await Task.FromResult(plan).ConfigureAwait(false);
    }

    public async Task RunAsync(UsbDrive drive, IReadOnlyList<UsbFixAction> actions, IProgress<UsbFixAction>? progress, CancellationToken ct)
    {
        var normal = actions.Where(a => !a.NeedsAdmin).ToList();
        var admin = actions.Where(a => a.NeedsAdmin).ToList();

        foreach (var a in normal)
        {
            ct.ThrowIfCancellationRequested();
            a.Status = "Running";
            progress?.Report(a);
            try
            {
                bool ok = a.Id switch
                {
                    "refresh_volume" => NativeStorageIo.TryUpdateDiskProperties(drive.PhysicalPath),
                    _ => false
                };
                a.Status = ok ? "Ok" : "Fail";
                a.Output = ok ? "已刷新设备配置。" : "刷新失败（设备可能已离线）。";
            }
            catch (Exception ex) { a.Status = "Fail"; a.Output = ex.Message; }
            progress?.Report(a);
        }

        if (admin.Count > 0)
        {
            foreach (var a in admin) { a.Status = "Running"; progress?.Report(a); }
            try
            {
                var (script, outLog) = BuildAdminScript(drive, admin);
                var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
                bool uacCancelled = output.Contains("##UAC_CANCELLED##");
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var a in admin)
                {
                    var okLine = lines.FirstOrDefault(l => l.Contains($"##OK##{a.Id}"));
                    var failLine = lines.FirstOrDefault(l => l.Contains($"##FAIL##{a.Id}"));
                    a.Status = okLine != null ? "Ok" : "Fail";
                    a.Output = uacCancelled ? "用户取消了 UAC 授权，未执行。"
                        : okLine != null ? "执行成功。"
                        : $"未取得成功标记（exit={exit}）{failLine}".Trim();
                    progress?.Report(a);
                }
            }
            catch (Exception ex)
            {
                foreach (var a in admin) { a.Status = "Fail"; a.Output = $"提权执行失败：{ex.Message}"; progress?.Report(a); }
            }
        }
    }

    public async Task<UsbFixAction> RebuildPartitionAsync(UsbDrive drive, string fileSystem, IProgress<UsbFixAction>? progress, CancellationToken ct)
    {
        // FAT32 只支持到 32GB，超出自动切 exFAT（与 Windows 磁盘管理一致）
        if (fileSystem == "fat32" && drive.SizeBytes > 32L * 1024 * 1024 * 1024)
            fileSystem = "exfat";

        var action = new UsbFixAction("rebuild_partition", $"重建分区表并格式化（{fileSystem.ToUpperInvariant()}）",
            "clean 重建分区表 + 快速格式化：U盘所有数据将被清空。用于分区表损坏、容量识别异常、无法格式化的设备。", true, true)
        { Status = "Running" };
        progress?.Report(action);

        try
        {
            var (script, outLog) = BuildAdminScript(drive, new[] { action }, fileSystemOverride: fileSystem);
            var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
            var ok = output.Contains($"##OK##{action.Id}");
            action.Status = ok ? "Ok" : "Fail";
            action.Output = ok ? "分区表已重建并完成格式化（新盘符可能需要重新插拔后出现）。"
                : $"执行失败（exit={exit}）：{Truncate(output, 400)}";
        }
        catch (Exception ex)
        {
            action.Status = "Fail";
            action.Output = $"提权执行失败：{ex.Message}";
        }
        progress?.Report(action);
        return action;
    }

    // ---------------- 提权脚本 ----------------

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n] + "…";

    private (string ScriptPath, string OutLog) BuildAdminScript(UsbDrive drive, IReadOnlyList<UsbFixAction> actions,
        string? fileSystemOverride = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var script = Path.Combine(dir, $"usb-fix-{ts}.cmd");
        var outLog = Path.Combine(dir, $"usb-fix-{ts}.log");

        var main = drive.Volumes.OrderByDescending(v => v.TotalBytes).FirstOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo [QingShu] 正在执行U盘修复，请保持窗口开启…");

        foreach (var a in actions)
        {
            switch (a.Id)
            {
                case "device_reset":
                {
                    var pnp = drive.PnpId.Replace("'", "");
                    sb.AppendLine($"powershell -NoProfile -Command \"$id='{pnp}'; try{{ Get-PnpDevice -InstanceId $id -ErrorAction Stop | Disable-PnpDevice -Confirm:$false -ErrorAction Stop; Start-Sleep 2; Get-PnpDevice -InstanceId $id -ErrorAction Stop | Enable-PnpDevice -Confirm:$false -ErrorAction Stop; exit 0 }}catch{{ exit 1 }}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "chkdsk_fix":
                    if (main != null)
                    {
                        // chkdsk 退出码 0-2 视为成功（1=已修复错误），>=3 视为失败
                        sb.AppendLine($"(chkdsk {main.Letter}: /f >nul 2>&1 & if errorlevel 3 (echo ##FAIL##{a.Id}) else (echo ##OK##{a.Id}))");
                    }
                    break;
                case "clear_readonly":
                {
                    var dp = WriteDiskPartScript(ts, "ro",
                        $"select disk {drive.DiskIndex}",
                        "attributes disk clear readonly",
                        main != null ? $"select volume {main.Letter}" : "",
                        main != null ? "attributes volume clear readonly" : "");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "assign_letter":
                {
                    var dp = WriteDiskPartScript(ts, "assign",
                        $"select disk {drive.DiskIndex}",
                        "select partition 1",
                        "assign");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "rebuild_partition":
                {
                    var fs = fileSystemOverride ?? "exfat";
                    var dp = WriteDiskPartScript(ts, "rebuild",
                        $"select disk {drive.DiskIndex}",
                        "clean",
                        "create partition primary",
                        $"format fs={fs} quick label=QINGSHU",
                        "assign");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                default:
                    sb.AppendLine($"echo ##FAIL##{a.Id} 未支持");
                    break;
            }
        }
        sb.AppendLine("echo ##DONE##");
        // cmd.exe 不容错 UTF-8 BOM（与网络修复/清理提权脚本同规格：无 BOM + chcp 65001）
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (script, outLog);
    }

    private static string WriteDiskPartScript(string ts, string tag, params string[] lines)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, $"usb-dp-{ts}-{tag}.txt");
        File.WriteAllText(p, string.Join("\r\n", lines.Where(l => l.Length > 0)) + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // 全 ASCII 指令，无 BOM 保证 diskpart 兼容
        return p;
    }
}
