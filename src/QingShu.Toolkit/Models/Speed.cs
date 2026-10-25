using System;
using System.Collections.Generic;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Models;

// ================= 网速与低速分析模型 =================

/// <summary>测速实时进度（约 400ms 一拍，UI 实时刷新）。</summary>
public sealed class SpeedProgress
{
    /// <summary>阶段描述（探测节点 / 正在测速：xxx）。</summary>
    public string Phase { get; init; } = "";
    /// <summary>瞬时速率（Mbps）。</summary>
    public double InstantMbps { get; init; }
    /// <summary>本次测速累计平均（Mbps）。</summary>
    public double AvgMbps { get; init; }
    /// <summary>已下载字节。</summary>
    public long BytesRead { get; init; }
    /// <summary>本端点已用时（ms）。</summary>
    public int ElapsedMs { get; init; }
    /// <summary>活动连接数。</summary>
    public int Connections { get; init; }
}

/// <summary>单个测速端点的结果。</summary>
public sealed class SpeedEndpointResult
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Success { get; set; }
    public double MbpsAvg { get; set; }
    public double MbpsPeak { get; set; }
    public long BytesTotal { get; set; }
    public int DurationMs { get; set; }
    public int Connections { get; set; }
    public string Error { get; set; } = "";

    public string SpeedText => Success ? $"{MbpsAvg:0.#} Mbps" : "不可用";
    public string DurationText => DurationMs > 0 ? $"{DurationMs / 1000.0:0.#} s" : "—";
    public string BytesText => FormatHelper.Bytes(BytesTotal);
}

/// <summary>一次下载吞吐实测的汇总。</summary>
public sealed class SpeedTestResult
{
    public bool Success { get; set; }
    /// <summary>代表带宽：多端点中最优平均（Mbps）。</summary>
    public double MbpsAvg { get; set; }
    /// <summary>峰值瞬时速率（Mbps）。</summary>
    public double MbpsPeak { get; set; }
    public long BytesTotal { get; set; }
    public int DurationMs { get; set; }
    /// <summary>代表带宽来源端点。</summary>
    public string EndpointLabel { get; set; } = "";
    /// <summary>链路协商速率（Mbps，对比实测效率用）。</summary>
    public double LinkMbps { get; set; }
    public List<SpeedEndpointResult> Endpoints { get; set; } = new();
    public string Error { get; set; } = "";

    /// <summary>实测占链路协商速率比例（0-1；共享/无线网络通常低于独占）。</summary>
    public double Efficiency => LinkMbps > 1 ? Math.Min(1.0, MbpsAvg / LinkMbps) : 0;

    public string SpeedText => Success ? $"{MbpsAvg:0.#} Mbps" : "未测得";
    public string PeakText => Success ? $"{MbpsPeak:0.#} Mbps" : "—";
    public string LinkText => LinkMbps >= 1000 ? $"{LinkMbps / 1000:0.#} Gbps" : LinkMbps > 0 ? $"{LinkMbps:0} Mbps" : "未知";
    public string EfficiencyText => LinkMbps > 1 ? $"{Efficiency * 100:0}%" : "—";

    /// <summary>体验档位（面向教师语义，不是实验室指标）。</summary>
    public string ExperienceBand
    {
        get
        {
            if (!Success) return "无法评估";
            return MbpsAvg switch
            {
                >= 90 => "优秀（4K 视频、大文件下载无压力）",
                >= 50 => "良好（1080P 网课与在线课堂流畅）",
                >= 20 => "一般（1080P 可看，多人共用可能缓冲）",
                >= 5 => "较差（网页与标清视频明显卡顿）",
                _ => "很差（网页图片加载都困难）"
            };
        }
    }
}

/// <summary>低速原因分析结论项：一条嫌疑原因 + 依据 + 处置措施。</summary>
public sealed class SlowCause
{
    public string Title { get; set; } = "";
    /// <summary>嫌疑等级：高 / 中 / 低。</summary>
    public string Level { get; set; } = "中";
    /// <summary>嫌疑权重（排序用，0-100）。</summary>
    public int Weight { get; set; }
    public List<string> Evidence { get; set; } = new();
    public List<string> Actions { get; set; } = new();

    public bool IsHigh => Level == "高";
    public bool IsMid => Level == "中";
    public bool IsLow => !IsHigh && !IsMid;
    public string EvidenceText => string.Join("；", Evidence);
    public string ActionText => string.Join("；", Actions);
}
