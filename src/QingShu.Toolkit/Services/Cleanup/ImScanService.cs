using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Cleanup;

public interface IImScanService
{
    /// <summary>扫描微信久远文件（默认阈值来自设置，可覆盖）。</summary>
    Task<List<ImFileItem>> ScanWeChatAsync(int oldDays, CancellationToken ct);
    /// <summary>扫描 QQ 久远文件。</summary>
    Task<List<ImFileItem>> ScanQQAsync(int oldDays, CancellationToken ct);
    /// <summary>删除所选（回收站或永久，按设置）。</summary>
    Task<(int Ok, int Failed, long Bytes)> DeleteAsync(IEnumerable<string> paths, bool toRecycleBin, CancellationToken ct);
}

/// <summary>
/// 微信 / QQ 久远文件扫描器（询问式清理）：
///  · 兼容旧版（WeChat Files / Tencent Files）与新版（xwechat_files / nt_qq）目录布局
///  · 绝不触碰聊天记录数据库（*.db / *.sqlite）与 config 目录 —— 只处理媒体与文件缓存
///  · 默认只列出“超过 N 天”的文件，勾选确认后才删除
/// </summary>
public sealed class ImScanService : IImScanService
{
    private readonly ILogService _log;
    public ImScanService(ILogService log) => _log = log;

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // 分类：目录名 → 展示类型
    private static readonly (string Key, string Kind)[] KindMap =
    {
        ("file", "文件"), ("filerecv", "接收文件"), ("attach", "附件"),
        ("image", "图片"), ("pic", "图片"), ("ori", "原图"),
        ("video", "视频"), ("thumb", "缩略图"),
        ("cache", "缓存"), ("msgattach", "会话附件"), ("emotion", "表情"), ("emoji", "表情"),
        ("pte", "表情")
    };

    private static string KindOf(string dirName)
    {
        var lower = dirName.ToLowerInvariant();
        foreach (var (key, kind) in KindMap)
            if (lower.Contains(key, StringComparison.Ordinal)) return kind;
        return "其他";
    }

    private static readonly string[] SkipDirNames =
        { "config", "db", "backup", "all users", "applet", "log", "logs", "message", "dbtemp" };

    public Task<List<ImFileItem>> ScanWeChatAsync(int oldDays, CancellationToken ct) => Task.Run(() =>
    {
        var result = new List<ImFileItem>();
        var roots = new[]
        {
            Path.Combine(Documents, "WeChat Files"),     // 微信 3.x
            Path.Combine(Documents, "xwechat_files")     // 微信 4.x
        };
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, oldDays));
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var account in Directory.EnumerateDirectories(root))
            {
                var accountName = Path.GetFileName(account);
                if (SkipDirNames.Contains(accountName.ToLowerInvariant())) continue;
                var files = FileOpsHelper.CollectFiles(account, maxFiles: 20000, maxDepth: 6, SkipDirNames, ct);
                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) break;
                    // 保护聊天记录数据库
                    var ext = f.Extension.ToLowerInvariant();
                    if (ext is ".db" or ".sqlite" or ".sqlite-wal" or ".sqlite-shm" or ".ini" or ".config") continue;
                    if (f.LastWriteTime > cutoff) continue;
                    var kind = KindOf(Path.GetFileName(Path.GetDirectoryName(f.FullName)) ?? "");
                    result.Add(new ImFileItem
                    {
                        From = "微信",
                        Account = accountName,
                        Kind = kind,
                        Path = f.FullName,
                        SizeBytes = f.Length,
                        LastWrite = f.LastWriteTime
                    });
                }
            }
        }
        result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        _log.App(LogLevel.Info, "cleanup", $"微信扫描完成：{result.Count} 个久远文件（>{oldDays} 天）");
        return result;
    }, ct);

    public Task<List<ImFileItem>> ScanQQAsync(int oldDays, CancellationToken ct) => Task.Run(() =>
    {
        var result = new List<ImFileItem>();
        var root = Path.Combine(Documents, "Tencent Files");
        if (!Directory.Exists(root)) return result;
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, oldDays));
        foreach (var account in Directory.EnumerateDirectories(root))
        {
            var accountName = Path.GetFileName(account);
            var files = FileOpsHelper.CollectFiles(account, maxFiles: 20000, maxDepth: 7, SkipDirNames, ct);
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) break;
                var ext = f.Extension.ToLowerInvariant();
                if (ext is ".db" or ".sqlite" or ".sqlite-wal" or ".sqlite-shm" or ".ini" or ".config") continue;
                if (f.LastWriteTime > cutoff) continue;
                var kind = KindOf(Path.GetFileName(Path.GetDirectoryName(f.FullName)) ?? "");
                result.Add(new ImFileItem
                {
                    From = "QQ",
                    Account = accountName,
                    Kind = kind,
                    Path = f.FullName,
                    SizeBytes = f.Length,
                    LastWrite = f.LastWriteTime
                });
            }
        }
        result.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        _log.App(LogLevel.Info, "cleanup", $"QQ 扫描完成：{result.Count} 个久远文件（>{oldDays} 天）");
        return result;
    }, ct);

    public async Task<(int Ok, int Failed, long Bytes)> DeleteAsync(IEnumerable<string> paths, bool toRecycleBin, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        long bytes = 0;
        await Task.Run(() =>
        {
            foreach (var p in paths)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (!File.Exists(p)) continue;
                    var len = new FileInfo(p).Length;
                    if (toRecycleBin)
                    {
                        if (NativeMethods.DeleteToRecycleBin(p) == 0) { ok++; bytes += len; } else failed++;
                    }
                    else
                    {
                        File.SetAttributes(p, FileAttributes.Normal);
                        File.Delete(p);
                        ok++;
                        bytes += len;
                    }
                }
                catch { failed++; }
            }
        }).ConfigureAwait(true);
        _log.App(LogLevel.Info, "cleanup", $"IM 文件删除完成：成功 {ok}，失败 {failed}，释放 {FormatHelper.Bytes(bytes)}（{(toRecycleBin ? "回收站" : "永久")}）");
        return (ok, failed, bytes);
    }
}
