using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>单次诊断会话日志（分日期 / 分次数 / 多文件）。</summary>
public interface ISessionLog : IDisposable
{
    string JsonPath { get; }
    string TextPath { get; }
    void Write(LogLevel level, string category, string message, object? data = null);
}

public interface ILogService
{
    string Root { get; }
    ISessionLog BeginDiagSession();
    void App(LogLevel level, string category, string message, object? data = null);
    void SaveCleanupReport(CleanupReport report);
    void PruneOldLogs();
    // 日志查看器支持
    List<(string DirPath, string DateText, int FileCount)> GetDateDirs();
    List<(string FullPath, string FileName, long SizeBytes)> GetDateFiles(string dateDir);
    string ReadTail(string filePath, int maxLines);
    int GetDiagSessionCount(DateTime date);
}

/// <summary>
/// 日志服务（完全覆盖“分日期、次数、多文件”要求）：
///   logs/
///     app-yyyyMM.log / app-yyyyMM.jsonl        —— 应用运行日志（按月）
///     yyyy-MM-dd/
///       diag-yyyyMMdd-HHmmss-NNN.log|jsonl    —— 第 NNN 次诊断会话（双文件）
///       cleanup-yyyyMMdd-HHmmss.json          —— 清理报告
/// </summary>
public sealed class LogService : ILogService
{
    private static readonly object AppLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public string Root { get; }

    public LogService()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingShu.Toolkit", "logs");
        try { Directory.CreateDirectory(Root); } catch { }
    }

    #region 诊断会话

    public ISessionLog BeginDiagSession()
    {
        var now = DateTime.Now;
        var dir = Path.Combine(Root, now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dir);
        int seq = GetDiagSessionCount(now) + 1;
        string baseName = $"diag-{now:yyyyMMdd-HHmmss}-{seq:D3}";
        return new SessionLog(Path.Combine(dir, baseName + ".jsonl"), Path.Combine(dir, baseName + ".log"));
    }

    public int GetDiagSessionCount(DateTime date)
    {
        try
        {
            var dir = Path.Combine(Root, date.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(dir)) return 0;
            return Directory.GetFiles(dir, "diag-*.jsonl").Length;
        }
        catch { return 0; }
    }

    private sealed class SessionLog : ISessionLog
    {
        private readonly object _lock = new();
        public string JsonPath { get; }
        public string TextPath { get; }

        public SessionLog(string jsonPath, string textPath)
        {
            JsonPath = jsonPath;
            TextPath = textPath;
        }

        public void Write(LogLevel level, string category, string message, object? data = null)
        {
            var now = DateTime.Now;
            lock (_lock)
            {
                try
                {
                    var line = JsonSerializer.Serialize(new
                    {
                        ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        level = level.ToString().ToLowerInvariant(),
                        cat = category,
                        msg = message,
                        data
                    }, JsonOpts);
                    File.AppendAllText(JsonPath, line + "\n", Encoding.UTF8);

                    var sb = new StringBuilder();
                    sb.Append('[').Append(now.ToString("HH:mm:ss.fff")).Append("][")
                      .Append(level.ToString().ToUpperInvariant()).Append("][")
                      .Append(category).Append("] ").Append(message);
                    if (data != null)
                    {
                        var d = JsonSerializer.Serialize(data, JsonOpts);
                        if (d.Length > 600) d = d[..600] + "…";
                        sb.Append(" | ").Append(d);
                    }
                    File.AppendAllText(TextPath, sb.ToString() + "\n", Encoding.UTF8);
                }
                catch { }
            }
        }

        public void Dispose() { /* 双文件均为追加式，无需刷新 */ }
    }

    #endregion

    #region 应用日志

    public void App(LogLevel level, string category, string message, object? data = null)
    {
        var now = DateTime.Now;
        lock (AppLock)
        {
            try
            {
                var textPath = Path.Combine(Root, $"app-{now:yyyyMM}.log");
                var jsonPath = Path.Combine(Root, $"app-{now:yyyyMM}.jsonl");
                var line = JsonSerializer.Serialize(new
                {
                    ts = now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    level = level.ToString().ToLowerInvariant(),
                    cat = category,
                    msg = message,
                    data
                }, JsonOpts);
                File.AppendAllText(jsonPath, line + "\n", Encoding.UTF8);
                File.AppendAllText(textPath,
                    $"[{now:yyyy-MM-dd HH:mm:ss.fff}][{level.ToString().ToUpperInvariant()}][{category}] {message}\n", Encoding.UTF8);
            }
            catch { }
        }
    }

    public void SaveCleanupReport(CleanupReport report)
    {
        try
        {
            var dir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOpts), Encoding.UTF8);
        }
        catch { }
    }

    #endregion

    #region 保留期清理

    public void PruneOldLogs()
    {
        try
        {
            var retention = Math.Max(7, new SettingsService().Current.LogRetentionDays);
            var cutoff = DateTime.Now.AddDays(-retention);
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 10 && DateTime.TryParse(name, out var d) && d < cutoff)
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            // 月度应用日志保留 6 个月
            foreach (var f in Directory.EnumerateFiles(Root, "app-*"))
            {
                var digits = new string(Path.GetFileNameWithoutExtension(f).Skip(4).Take(6).ToArray());
                if (digits.Length == 6 && DateTime.TryParseExact(digits, "yyyyMM", null, System.Globalization.DateTimeStyles.None, out var m)
                    && m < DateTime.Now.AddMonths(-6))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    #endregion

    #region 日志查看器

    public List<(string DirPath, string DateText, int FileCount)> GetDateDirs()
    {
        var list = new List<(string, string, int)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root).OrderByDescending(d => d))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 10 && DateTime.TryParse(name, out _))
                    list.Add((dir, name, Directory.GetFiles(dir).Length));
            }
        }
        catch { }
        return list;
    }

    public List<(string FullPath, string FileName, long SizeBytes)> GetDateFiles(string dateDir)
    {
        var list = new List<(string, string, long)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dateDir).OrderByDescending(f => f))
                list.Add((f, Path.GetFileName(f), new FileInfo(f).Length));
        }
        catch { }
        return list;
    }

    public string ReadTail(string filePath, int maxLines)
    {
        try
        {
            if (!File.Exists(filePath)) return "";
            var all = File.ReadAllLines(filePath, Encoding.UTF8);
            if (all.Length <= maxLines) return string.Join("\n", all);
            return string.Join("\n", all[^maxLines..]);
        }
        catch (Exception ex) { return $"读取失败: {ex.Message}"; }
    }

    #endregion
}
