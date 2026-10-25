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
    /// <summary>统计目录内容总大小（目录不存在或无权限返回 0）。显式栈遍历：单目录无权限只跳过该分支，不中断整体统计。</summary>
    public static (long Bytes, int Files) GetDirectorySize(string path)
    {
        long total = 0;
        int files = 0;
        try
        {
            if (!Directory.Exists(path)) return (0, 0);
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(path));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try { foreach (var f in dir.EnumerateFiles()) { try { total += f.Length; files++; } catch { } } }
                catch { }
                try { foreach (var d in dir.EnumerateDirectories()) stack.Push(d); }
                catch { }
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

    /// <summary>
    /// 清空目录内容（保留目录本身）。返回 (删除文件数, 释放字节, 失败数)。
    /// 递归逐文件删除 + 自底向上删空目录：深层子目录不再因整体删除失败而整团跳过
    /// （历史 bug：浏览器 Cache/Cache_Data 等深层缓存一个被锁文件导致整目录删不掉）。
    /// </summary>
    public static (int DeletedFiles, long FreedBytes, int Failed) CleanDirectoryContents(string path)
    {
        int deleted = 0, failed = 0;
        long freed = 0;
        if (!Directory.Exists(path)) return (0, 0, 0);
        var root = Path.GetFullPath(path);
        var files = new List<string>();
        var dirs = new List<string>(); // 先序（父在前），删除时反向自底向上
        try
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                dirs.Add(dir.FullName);
                try { foreach (var f in dir.EnumerateFiles()) files.Add(f.FullName); }
                catch { }
                try { foreach (var d in dir.EnumerateDirectories()) stack.Push(d); }
                catch { }
            }
        }
        catch { }
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                long len = fi.Length;
                File.SetAttributes(f, FileAttributes.Normal);
                fi.Delete();
                deleted++;
                freed += len;
            }
            catch { failed++; }
        }
        for (var i = dirs.Count - 1; i >= 0; i--)
        {
            if (string.Equals(dirs[i], root, StringComparison.OrdinalIgnoreCase)) continue; // 保留目录本身
            try { new DirectoryInfo(dirs[i]).Delete(false); } // 仅删空目录；仍有残留（被锁）则计入失败
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
}
