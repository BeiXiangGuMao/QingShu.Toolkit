using System;
using System.Collections.Generic;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Models;

// ================= 清理模型 =================

public enum CleanRisk { Low, Medium, High }

public static class CleanRiskText
{
    public static string Of(CleanRisk r) => r switch
    {
        CleanRisk.Low => "低风险",
        CleanRisk.Medium => "中风险",
        CleanRisk.High => "高风险",
        _ => ""
    };
}

/// <summary>常规清理目标定义（引擎持有），UI 包装为 CleanTargetVm。</summary>
public sealed class CleanTargetDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public CleanRisk Risk { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>展开目标路径（含通配符目录），空 = 无内容</summary>
    public Func<IEnumerable<string>> Paths { get; init; } = () => Array.Empty<string>();
    /// <summary>特殊目标：回收站</summary>
    public bool IsRecycleBin { get; init; }
    /// <summary>默认是否勾选</summary>
    public bool DefaultSelected { get; init; } = true;
}

/// <summary>清理结果。</summary>
public sealed class CleanupReport
{
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public long FreedBytes { get; set; }
    public int DeletedFiles { get; set; }
    public int FailedFiles { get; set; }
    public List<string> Details { get; set; } = new();
    public string ToSummary() => $"释放 {FormatHelper.Bytes(FreedBytes)} · 清理 {DeletedFiles} 个文件" + (FailedFiles > 0 ? $" · {FailedFiles} 个跳过/失败" : "");
}

/// <summary>微信 / QQ 久远文件条目。</summary>
public sealed class ImFileItem
{
    public required string From { get; init; }       // 微信 / QQ
    public required string Account { get; init; }    // 账号目录名
    public required string Kind { get; init; }       // 文件/图片/视频/缓存/表情
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWrite { get; init; }
    public int AgeDays => (int)(DateTime.Now - LastWrite).TotalDays;
}

/// <summary>大文件条目。</summary>
public sealed class LargeFileInfo
{
    public required string Path { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastWrite { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? Path;
    public string Ext => System.IO.Path.GetExtension(Path).ToUpperInvariant();
}
