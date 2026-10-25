using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Models;

/// <summary>本机硬件/设备事实（WMI + 注册表，只读取）。</summary>
public sealed class DeviceFacts
{
    public string HostName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string CpuName { get; set; } = "";
    public ulong TotalRamBytes { get; set; }
    /// <summary>BIOS 序列号（SMBIOS）</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>Windows MachineGuid（设备唯一标识）</summary>
    public string MachineGuid { get; set; } = "";
    /// <summary>主网卡型号</summary>
    public string NicModel { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public bool Loaded { get; set; }
}

// ================= PDF 报告模型 =================

/// <summary>诊断 PDF 报告元数据。</summary>
public sealed class ReportMeta
{
    /// <summary>报告编号：QS-DIAG-yyyyMMdd-NNN（按日累计次数）</summary>
    public string ReportId { get; set; } = "";
    /// <summary>学校名称</summary>
    public string School { get; set; } = "";
    /// <summary>年级（生成时必填）</summary>
    public string Grade { get; set; } = "";
    /// <summary>班级（生成时必填）</summary>
    public string ClassName { get; set; } = "";
    /// <summary>报告人</summary>
    public string Reporter { get; set; } = "";
    /// <summary>设备编号（默认主机名，可自定义）</summary>
    public string DeviceTag { get; set; } = "";
    /// <summary>设备序列号（SMBIOS）</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>系统 MachineGuid</summary>
    public string MachineGuid { get; set; } = "";
    public string HostName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string CpuName { get; set; } = "";
    public string TotalRamText { get; set; } = "";
    public string NicModel { get; set; } = "";
    public string IpV4 { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string ToolVersion { get; set; } = "";
    public string ModeText { get; set; } = "";
}

/// <summary>PDF 报告完整数据。</summary>
public sealed class NetworkReportData
{
    public ReportMeta Meta { get; set; } = new();
    public List<CheckItem> Results { get; set; } = new();
    public List<RepairAction> Repairs { get; set; } = new();
    /// <summary>分优先级建议（1=立即处理 2=今日处理 3=持续观察）</summary>
    public List<(int Priority, string Text)> Recommendations { get; set; } = new();
}

/// <summary>自动更新信息（GitHub Releases）。</summary>
public sealed class ReleaseInfo
{
    public string Tag { get; set; } = "";
    public string VersionText => Tag.TrimStart('v', 'V');
    public string Notes { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string AssetUrl { get; set; } = "";
    public long AssetSize { get; set; }
    public string HtmlUrl { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public bool IsPrerelease { get; set; }
}
