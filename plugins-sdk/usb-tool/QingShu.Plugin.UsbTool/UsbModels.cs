using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QingShu.Plugin.UsbTool;

// ============================================================================
// U 盘工具数据模型（宿主 Services/Usb/UsbModels.cs + UsbFixAction 同源移植）。
// ============================================================================

/// <summary>一个 USB 物理盘及其卷映射（只读快照）。</summary>
public sealed record UsbDrive(
    int DiskIndex,
    string PnpId,
    string Model,
    string Serial,
    long SizeBytes,
    string BusTypeText,
    bool BusRemovable,
    IReadOnlyList<UsbVolume> Volumes)
{
    /// <summary>原始物理盘路径（\\.\PhysicalDriveN）。</summary>
    public string PhysicalPath => $@"\\.\PhysicalDrive{DiskIndex}";

    public string SizeText => FormatHelper.Bytes(SizeBytes);

    public string LettersText
    {
        get
        {
            var l = new List<string>();
            foreach (var v in Volumes) l.Add(v.Letter + ":");
            return l.Count > 0 ? string.Join(" ", l) : "无盘符";
        }
    }
}

/// <summary>USB 盘上的一个卷。</summary>
public sealed record UsbVolume(string Letter, string Label, string FileSystem, long TotalBytes, long FreeBytes)
{
    public string SizeText => $"{FormatHelper.Bytes(TotalBytes)}（剩余 {FormatHelper.Bytes(FreeBytes)}）";
}

/// <summary>健康/查杀检测的单项结论。Status: Pass / Warning / Fail / Skipped。</summary>
public sealed record UsbCheckOutcome(string Title, string Status, string Summary, string Suggestion, string[] Details)
{
    public static UsbCheckOutcome Pass(string title, string summary, params string[] details) => new(title, "Pass", summary, "", details);
    public static UsbCheckOutcome Warn(string title, string summary, string suggestion, params string[] details) => new(title, "Warning", summary, suggestion, details);
    public static UsbCheckOutcome Fail(string title, string summary, string suggestion, params string[] details) => new(title, "Fail", summary, suggestion, details);
    public static UsbCheckOutcome Skip(string title, string summary) => new(title, "Skipped", summary, "", System.Array.Empty<string>());
}

/// <summary>扩容盘写校验的单点结果。</summary>
public sealed record CapacityProbe(long Offset, bool WriteOk, bool VerifyOk, string Note);

/// <summary>
/// 修复计划（v0.2.0）：动作清单 + 可见性诊断结论。
/// 「已识别但资源管理器不显示」的六类根因（脱机/无盘符分区/无分区/RAW/策略隐藏/过滤器遗留）
/// 以 UsbCheckOutcome 形式同步呈现，动作按诊断结果按需生成（不脱机不生成联机动作等）。
/// </summary>
public sealed record UsbRepairPlan(
    IReadOnlyList<UsbFixAction> Actions,
    IReadOnlyList<UsbCheckOutcome> Diagnosis);

/// <summary>查杀发现的一条可疑项。Level: Malicious / Suspicious。</summary>
public sealed record UsbThreat(string FilePath, string Level, string Reason, string Detail);

/// <summary>U盘修复动作（可绑定行模型，宿主 UsbRepairService 内嵌类同源移植）。</summary>
public sealed partial class UsbFixAction : ObservableObject
{
    public UsbFixAction(string id, string title, string detail, bool needsAdmin, bool risky, bool? defaultSelected = null)
    {
        Id = id; Title = title; Detail = detail; NeedsAdmin = needsAdmin; Risky = risky;
        // 默认勾选 = 非危险项；可用 defaultSelected 显式覆盖（如「重启资源管理器」默认不勾）
        IsSelected = defaultSelected ?? !risky;
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
