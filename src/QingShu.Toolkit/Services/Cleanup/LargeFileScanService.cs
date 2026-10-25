using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Cleanup;

public interface ILargeFileScanService
{
    Task<List<char>> GetFixedDrivesAsync();
    /// <summary>扫描指定分区中的大文件（跳过系统目录），按大小倒序。</summary>
    Task<List<LargeFileInfo>> ScanAsync(char drive, int minMb, CancellationToken ct);
    /// <summary>删除文件（回收站 / 永久）。</summary>
    Task<(int Ok, int Failed, long Bytes)> DeleteAsync(IEnumerable<string> paths, bool toRecycleBin, CancellationToken ct);
}

/// <summary>大文件扫描器：全盘兜底清理的“询问式”入口。</summary>
public sealed class LargeFileScanService : ILargeFileScanService
{
    private static readonly string[] ExcludedRoots =
        { "windows", "program files", "program files (x86)", "programdata", "$recycle.bin", "system volume information", "recovery" };

    private readonly ILogService _log;
    public LargeFileScanService(ILogService log) => _log = log;

    public Task<List<char>> GetFixedDrivesAsync() => Task.Run(() =>
        DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.Name[0]).ToList());

    public Task<List<LargeFileInfo>> ScanAsync(char drive, int minMb, CancellationToken ct) => Task.Run(() =>
    {
        var minBytes = (long)minMb * 1024 * 1024;
        var root = $"{drive}:\\";
        var results = new List<LargeFileInfo>();
        var stack = new Stack<string>();
        stack.Push(root);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var excluded = new HashSet<string>(ExcludedRoots, StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0 && sw.ElapsedMilliseconds < 120_000 && results.Count < 800)
        {
            if (ct.IsCancellationRequested) break;
            var dir = stack.Pop();
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                {
                    try
                    {
                        if (f.Length >= minBytes)
                            results.Add(new LargeFileInfo { Path = f.FullName, SizeBytes = f.Length, LastWrite = f.LastWriteTime });
                    }
                    catch { }
                }
                foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                {
                    var name = d.Name;
                    if (dir == root && excluded.Contains(name)) continue;
                    if (name.StartsWith("$", StringComparison.Ordinal)) continue;
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(d.FullName);
                }
            }
            catch { /* 无权限跳过 */ }
        }
        results = results.OrderByDescending(r => r.SizeBytes).ToList();
        _log.App(LogLevel.Info, "cleanup", $"大文件扫描完成：{drive}: 共 {results.Count} 个 ≥{minMb}MB（用时 {sw.ElapsedMilliseconds / 1000.0:0.#}s）");
        return results;
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
                        if (Interop.NativeMethods.DeleteToRecycleBin(p) == 0) { ok++; bytes += len; } else failed++;
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
        _log.App(LogLevel.Info, "cleanup", $"大文件删除完成：成功 {ok}，失败 {failed}，释放 {FormatHelper.Bytes(bytes)}");
        return (ok, failed, bytes);
    }
}
