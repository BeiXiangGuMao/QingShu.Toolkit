using System.Collections.Generic;

namespace QingShu.Toolkit.Services.Usb;

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

    public string SizeText => Helpers.FormatHelper.Bytes(SizeBytes);

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
    public string SizeText => $"{Helpers.FormatHelper.Bytes(TotalBytes)}（剩余 {Helpers.FormatHelper.Bytes(FreeBytes)}）";
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

/// <summary>查杀发现的一条可疑项。Level: Malicious / Suspicious。</summary>
public sealed record UsbThreat(string FilePath, string Level, string Reason, string Detail);
