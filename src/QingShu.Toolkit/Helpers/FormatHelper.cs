using System;

namespace QingShu.Toolkit.Helpers;

/// <summary>展示格式化工具。</summary>
public static class FormatHelper
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }

    public static string BytesNullable(long? bytes) => bytes is null or < 0 ? "—" : Bytes(bytes.Value);

    public static string Duration(long ms) => ms switch
    {
        < 1000 => $"{ms} ms",
        < 60_000 => $"{ms / 1000.0:0.#} s",
        _ => $"{ms / 60000} min {(ms % 60000) / 1000} s"
    };

    public static string Percent(double v) => $"{v:0.#}%";

    public static string TimeAgo(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        return t.ToString("yyyy-MM-dd HH:mm");
    }
}
