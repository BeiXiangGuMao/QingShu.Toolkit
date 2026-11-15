using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QingShu.Plugin.UsbTool;

// ============================================================================
// 「U盘已识别但资源管理器不显示」可见性诊断（v0.2.0 新增，全程只读）：
// 覆盖六类根因 —— ①磁盘脱机 ②有分区无盘符（最常见）③无分区/分区表损坏
// ④RAW/未识别文件系统 ⑤NoDrives/NoViewOnDrive 策略隐藏（机房管理常见）
// ⑥存储类 UpperFilters/LowerFilters 第三方遗留（虚拟光驱/加密软件）。
// 诊断结论以 UsbCheckOutcome 呈现在「检测结论」区，修复动作由 UsbRepairService
// 按诊断结果按需生成（不脱机不生成联机动作、无策略不生成清策略动作）。
// ============================================================================

/// <summary>单盘可见性诊断快照（只读）。PartitionCount = -1 表示分区映射查询失败（未知）。</summary>
internal sealed record UsbVisibilityResult(
    bool Offline,
    bool DiskReadOnly,
    int PartitionCount,
    int LetterlessPartitions,
    List<string> Letters,
    List<string> RawFileSystems,
    uint NoDrivesMask,
    uint NoViewMask,
    List<string> PolicyHiddenLetters,
    bool HasDiskFilters,
    bool HasVolumeFilters,
    List<string> FilterTexts,
    List<string> Notes)
{
    public bool HasPolicyHide => NoDrivesMask != 0 || NoViewMask != 0;
    public bool HasFilters => HasDiskFilters || HasVolumeFilters;
}

/// <summary>可见性诊断 + 策略清理（当前用户级无需管理员）+ 结果呈现。</summary>
internal static class UsbVisibilityDiag
{
    // 磁盘类 / 卷类设备安装类 GUID（Windows 文档化标准 GUID）
    private const string DiskClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E967-E325-11CE-BFC1-08002BE10318}";
    private const string VolumeClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{71A27CDD-812A-11D0-BEC7-08002BE2092F}";
    private const string PoliciesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public static async Task<UsbVisibilityResult> DiagnoseAsync(UsbDrive drive)
    {
        var notes = new List<string>();
        bool offline = false, diskRo = false;

        // ---- ① 磁盘联机/只读状态（MSFT_Disk，Storage 命名空间；查询失败按在线处理） ----
        try
        {
            var disks = await WmiHelper.QueryAsync(@"root\Microsoft\Windows\Storage",
                $"SELECT Number, IsOffline, IsReadOnly FROM MSFT_Disk WHERE Number={drive.DiskIndex}",
                mo => (Offline: ToBool(mo["IsOffline"]), Ro: ToBool(mo["IsReadOnly"]))).ConfigureAwait(false);
            if (disks.Count > 0) { offline = disks[0].Offline; diskRo = disks[0].Ro; }
        }
        catch (Exception ex) { notes.Add($"磁盘状态查询失败（按在线处理）：{ex.Message}"); }

        // ---- ② 分区 → 盘符映射（标准 CIMV2 关联查询；有分区无逻辑盘 = 无盘符分区） ----
        int partitions = -1;
        var letters = new List<string>();
        try
        {
            var escaped = $@"\\.\PHYSICALDRIVE{drive.DiskIndex}".Replace(@"\", @"\\");
            var parts = await WmiHelper.QueryAsync(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escaped}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition",
                mo => mo["DeviceID"]?.ToString() ?? "").ConfigureAwait(false);
            partitions = parts.Count;
            foreach (var partId in parts)
            {
                var escPart = partId.Replace(@"\", @"\\");
                var logicals = await WmiHelper.QueryAsync(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escPart}'}} WHERE AssocClass = Win32_LogicalDiskToPartition",
                    mo => mo["DeviceID"]?.ToString() ?? "").ConfigureAwait(false);
                foreach (var l in logicals)
                    if (l.Length >= 2 && char.IsLetter(l[0]) && !letters.Contains(l[0].ToString()))
                        letters.Add(l[0].ToString());
            }
        }
        catch (Exception ex) { notes.Add($"分区/盘符映射查询失败：{ex.Message}"); }
        int letterless = partitions > 0 ? Math.Max(0, partitions - letters.Count) : 0;

        // ---- ③ RAW / 未识别文件系统（有盘符但 FileSystem 为空） ----
        var raw = new List<string>();
        try
        {
            var lds = await WmiHelper.QueryAsync(
                "SELECT DeviceID, FileSystem FROM Win32_LogicalDisk",
                mo => (Id: mo["DeviceID"]?.ToString() ?? "", Fs: (mo["FileSystem"]?.ToString() ?? "").Trim()))
                .ConfigureAwait(false);
            foreach (var (id, fs) in lds)
                if (id.Length >= 2 && letters.Contains(id[0].ToString()) && fs.Length == 0 && !raw.Contains(id))
                    raw.Add(id);
        }
        catch (Exception ex) { notes.Add($"文件系统查询失败：{ex.Message}"); }

        // ---- ④ 盘符隐藏策略（HKCU + HKLM 只读均无需管理员；NoDrives/NoViewOnDrive 位掩码） ----
        uint noDrives = GetPolicyValue("NoDrives");
        uint noView = GetPolicyValue("NoViewOnDrive");
        var hiddenRelevant = new List<string>();
        foreach (var l in letters)
            if (IsBitSet(noDrives, l[0]) || IsBitSet(noView, l[0]))
                hiddenRelevant.Add(l + ":");

        // ---- ⑤ 存储类过滤器（只读探测 + 值内容留痕，供深度修复与人工判断） ----
        var filterTexts = new List<string>();
        bool diskFilters = ReadFilters(DiskClassKey, filterTexts, "磁盘类");
        bool volumeFilters = ReadFilters(VolumeClassKey, filterTexts, "卷类");

        return new UsbVisibilityResult(offline, diskRo, partitions, letterless, letters, raw,
            noDrives, noView, hiddenRelevant, diskFilters, volumeFilters, filterTexts, notes);
    }

    /// <summary>诊断结论 → 检测结论区呈现（按严重度排序，全正常时给一条 Pass 收尾）。</summary>
    public static List<UsbCheckOutcome> ToOutcomes(UsbVisibilityResult d)
    {
        var list = new List<UsbCheckOutcome>();

        if (d.PartitionCount < 0)
            list.Add(UsbCheckOutcome.Warn("可见性诊断（分区映射）",
                "分区/盘符映射查询失败，以下结论可能不完整。",
                "重插U盘后重新诊断。", d.Notes.Take(3).ToArray()));
        if (d.Offline)
            list.Add(UsbCheckOutcome.Fail("磁盘处于脱机状态",
                "磁盘管理中该盘为「脱机」：资源管理器不会显示它的任何卷。",
                "执行修复清单中的「联机磁盘」（需管理员）后刷新设备。"));
        if (d.PartitionCount == 0)
            list.Add(UsbCheckOutcome.Warn("未检测到任何分区",
                "设备已识别但没有分区：可能是全新U盘，或分区表已损坏。",
                "盘上有数据时先试「重置U盘设备栈」；确认数据可丢弃时用「格式化重建分区表」（清空数据）。"));
        if (d.PartitionCount > 0 && d.LetterlessPartitions > 0)
            list.Add(UsbCheckOutcome.Fail($"有 {d.LetterlessPartitions} 个分区未分配盘符",
                $"Windows 识别到了 {d.PartitionCount} 个分区，其中 {d.LetterlessPartitions} 个没有盘符——这正是「资源管理器看不到U盘」的最常见原因。",
                "执行修复清单中的「重建盘符映射」（需管理员）后刷新设备。"));
        if (d.RawFileSystems.Count > 0)
            list.Add(UsbCheckOutcome.Warn("卷文件系统无法识别（RAW）",
                $"{string.Join("、", d.RawFileSystems)} 的文件系统为空或未识别：分区和盘符在，但文件系统异常。",
                "先试「文件系统查错修复」；无效则「格式化重建分区表」（会清空数据）。"));
        if (d.PolicyHiddenLetters.Count > 0)
            list.Add(UsbCheckOutcome.Fail("组策略隐藏了盘符 " + string.Join("、", d.PolicyHiddenLetters),
                "本机配置了 NoDrives / NoViewOnDrive 策略（机房管理常用），资源管理器按策略不显示这些盘符。",
                "执行「清除盘符隐藏策略（当前用户/全机）」+「重启资源管理器」。"));
        else if (d.HasPolicyHide && d.Letters.Count == 0)
            list.Add(UsbCheckOutcome.Warn("本机配置了盘符隐藏策略",
                "NoDrives/NoViewOnDrive 策略存在（本盘暂无盘符，但重建盘符后可能立即被策略隐藏）。",
                "建议同时执行「清除盘符隐藏策略」后再重建盘符。"));
        if (d.HasFilters)
            list.Add(UsbCheckOutcome.Warn("存储类过滤器存在第三方条目",
                string.Join("；", d.FilterTexts.Take(2)),
                "第三方过滤器（虚拟光驱/加密软件遗留）是「设备识别但无法访问」的经典原因。可用深度修复项「清理存储类过滤器」（默认不勾选，自动备份注册表，重启后生效）。"));
        if (d.DiskReadOnly)
            list.Add(UsbCheckOutcome.Warn("磁盘级只读属性开启",
                "系统为该盘设置了只读策略：写入会被拒绝。",
                "执行「清除软件写保护」（需管理员）。"));

        if (list.Count == 0)
            list.Add(UsbCheckOutcome.Pass("盘符与挂载状态正常",
                d.Letters.Count > 0
                    ? $"已挂载盘符 {string.Join("、", d.Letters.Select(l => l + ":"))}；无脱机 / 无盘符缺失 / 无策略隐藏 / 无第三方过滤器。"
                    : "设备在线且分区映射正常（本盘无卷属正常形态，如读卡器空卡）。"));

        // 查询备注附在末尾（不改变严重度）
        if (d.Notes.Count > 0)
            list.Add(UsbCheckOutcome.Skip("可见性诊断备注", string.Join("；", d.Notes.Take(3))));
        return list;
    }

    // ================= 盘符隐藏策略（读 + 用户级清除） =================

    /// <summary>读 NoDrives / NoViewOnDrive 位掩码（HKCU | HKLM 合并；读不到为 0）。</summary>
    private static uint GetPolicyValue(string valueName)
    {
        uint mask = 0;
        mask |= ReadPolicyMask(Registry.CurrentUser, valueName);
        mask |= ReadPolicyMask(Registry.LocalMachine, valueName);
        return mask;
    }

    private static uint ReadPolicyMask(RegistryKey hive, string valueName)
    {
        try
        {
            using var k = hive.OpenSubKey(PoliciesKey);
            if (k == null) return 0;
            return ToUint(k.GetValue(valueName));
        }
        catch { return 0; }
    }

    /// <summary>清除当前用户的盘符隐藏策略（HKCU 无需管理员）。返回 false 表示清除失败。</summary>
    public static bool ClearUserPolicy()
    {
        bool anyFailed = false;
        foreach (var name in new[] { "NoDrives", "NoViewOnDrive" })
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(PoliciesKey, writable: true);
                if (k == null) continue;          // 策略键不存在 = 无策略，视为成功
                if (k.GetValue(name) != null) k.DeleteValue(name);
            }
            catch { anyFailed = true; }
        }
        return !anyFailed;
    }

    // ================= 存储类过滤器（读） =================

    private static bool ReadFilters(string classKey, List<string> texts, string label)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(classKey);
            if (k == null) return false;
            bool found = false;
            foreach (var name in new[] { "UpperFilters", "LowerFilters" })
            {
                if (k.GetValue(name) is not string[] vals || vals.Length == 0) continue;
                var valid = vals.Where(v => v.Trim().Length > 0).ToList();
                if (valid.Count == 0) continue;
                found = true;
                texts.Add($"{label} {name}: {string.Join("、", valid)}");
            }
            return found;
        }
        catch { return false; }
    }

    // ================= 小工具 =================

    private static bool ToBool(object? v) =>
        v is bool b ? b : bool.TryParse(v?.ToString(), out var r) && r;

    private static uint ToUint(object? v) =>
        v switch
        {
            int i => unchecked((uint)i),
            long l => unchecked((uint)l),
            uint u => u,
            _ => uint.TryParse(v?.ToString(), out var r) ? r : 0
        };

    private static bool IsBitSet(uint mask, char letter)
    {
        int bit = char.ToUpperInvariant(letter) - 'A';
        return bit >= 0 && bit < 26 && (mask & (1u << bit)) != 0;
    }
}

/// <summary>Shell 通知：强制资源管理器刷新驱动器列表（修复动作后盘符不出现时的轻量兜底）。</summary>
internal static class NativeShell
{
    // SHCNE_ASSOCCHANGED(0x08000000) + SHCNF_FLUSH(0x1000)：文档化的「让 Shell 重读设备/盘符」信号
    private const int SHCNE_ASSOCCHANGED = unchecked((int)0x08000000);
    private const uint SHCNF_FLUSH = 0x1000;

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    /// <summary>通知资源管理器重读驱动器列表（best-effort，无需管理员）。</summary>
    public static void NotifyShellDriveChange()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }
}
