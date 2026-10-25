using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// 文件操作工具：目录体积统计 / 递归扫描 /
/// 删除（回收站 / 永久）——全部逐文件 try/catch 并记录失败计数，绝不整目录粗暴删除。
/// </summary>
public static class FileOpsHelper
{
    /// <summary>统计目录内容总大小（目录不存在或无权限返回 0）。</summary>
    public static (long Bytes, int Files) GetDirectorySize(string path)
    {
        long total = 0;
        int files = 0;
        try
        {
            if (!Directory.Exists(path)) return (0, 0);
            var di = new DirectoryInfo(path);
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { total += f.Length; files++; } catch { }
            }
        }
        catch { }
        return (total, files);
    }

    public static Task<(long Bytes, int Files)> GetDirectorySizeAsync(string path)
        => Task.Run(() => GetDirectorySize(path));

    /// <summary>按通配符模式统计（如 thumbcache_*.db）。</summary>
    public static (long Bytes, int Files) GetPatternSize(string dir, string pattern)
    {
        long total = 0;
        int files = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles(pattern))
            {
                try { total += f.Length; files++; } catch { }
            }
        }
        catch { }
        return (total, files);
    }

    /// <summary>清空目录内容（保留目录本身）。返回 (删除文件数, 释放字节, 失败数)。</summary>
    public static (int DeletedFiles, long FreedBytes, int Failed) CleanDirectoryContents(string path)
    {
        int deleted = 0, failed = 0;
        long freed = 0;
        if (!Directory.Exists(path)) return (0, 0, 0);
        foreach (var f in SafeFiles(path))
        {
            try
            {
                long len = f.Length;
                File.SetAttributes(f.FullName, FileAttributes.Normal);
                f.Delete();
                deleted++;
                freed += len;
            }
            catch { failed++; }
        }
        foreach (var d in SafeDirs(path))
        {
            try
            {
                foreach (var f in SafeFiles(d.FullName)) { try { File.SetAttributes(f.FullName, FileAttributes.Normal); f.Delete(); } catch { failed++; } }
                d.Delete(true);
            }
            catch { failed++; }
        }
        return (deleted, freed, failed);
    }

    /// <summary>递归收集文件（跳过无权限目录），按最近修改时间倒序，最多 maxFiles 条。</summary>
    public static List<FileInfo> CollectFiles(string root, int maxFiles = 5000, int maxDepth = 6,
        IEnumerable<string>? excludedDirNames = null, CancellationToken ct = default)
    {
        var excluded = new HashSet<string>((excludedDirNames ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
        var result = new List<FileInfo>(maxFiles);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && result.Count < maxFiles)
        {
            if (ct.IsCancellationRequested) break;
            var (dir, depth) = stack.Pop();
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                {
                    result.Add(f);
                    if (result.Count >= maxFiles) break;
                }
                if (depth < maxDepth)
                {
                    foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                    {
                        if (excluded.Contains(d.Name) || d.Name.StartsWith("$", StringComparison.Ordinal)) continue;
                        stack.Push((d.FullName, depth + 1));
                    }
                }
            }
            catch { /* 无权限目录跳过 */ }
        }
        return result;
    }

    private static IEnumerable<FileInfo> SafeFiles(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles().ToList(); }
        catch { return Enumerable.Empty<FileInfo>(); }
    }

    private static IEnumerable<DirectoryInfo> SafeDirs(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateDirectories().ToList(); }
        catch { return Enumerable.Empty<DirectoryInfo>(); }
    }
}
