using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Plugin.DiskCleanup;

// ================= 底层互操作（宿主 Helpers/Interop 同源自包含移植） =================
// 仅保留磁盘清理域实际用到的成员：字节格式化 / 文件操作 / 回收站 Win32 / UAC 提权。
// 与宿主内置实现逐行对应，安全铁律原样（STA 回收站、逐文件 try/catch、UAC 1223 哨兵）。

/// <summary>展示格式化工具（宿主 Helpers/FormatHelper 同源移植，仅保留本域用到的 Bytes）。</summary>
internal static class FormatHelper
{
    public static string Bytes(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return $"{b:0.##} {units[u]}";
    }
}

/// <summary>
/// 文件操作工具（宿主 Helpers/FileOpsHelper 同源移植）：
/// 目录体积统计 / 递归扫描 / 删除——全部逐文件 try/catch 并记录失败计数，绝不整目录粗暴删除。
/// </summary>
internal static class FileOpsHelper
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

    /// <summary>删除后存在性复核（目录或文件任一存在即视为仍存在）。回收站删除返回 0 不代表删净，必须二次确认（与常规批量同口径）。</summary>
    public static bool PathStillExists(string p) => Directory.Exists(p) || File.Exists(p);

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
                        // ReparsePoint（junction / 符号链接）不入栈：AppData 下 Application Data 等自指 junction
                        // 会无限循环（仅靠 maxFiles/maxDepth 兕底）；系统符号链接指向的目标也不应被清理扫描穿透
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push((d.FullName, depth + 1));
                    }
                }
            }
            catch { /* 无权限目录跳过 */ }
        }
        return result;
    }
}

/// <summary>
/// 回收站 Win32 互操作（宿主 Interop/NativeMethods 回收站区段同源移植）：
/// SHQueryRecycleBin（逐分区查询）/ SHEmptyRecycleBin / SHFileOperation FO_DELETE（STA 线程）。
/// </summary>
internal static class NativeRecycleBin
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    /// <summary>
    /// 查询回收站占用（所有固定分区求和）。
    /// 历史 bug：pszRootPath 传 null 不被文档支持，部分系统返回失败 → 被误判“回收站是空的”，
    /// 回收站目标扫描恒为 0、清理时永远跳过。逐分区查询是文档明确支持的行为，结果稳定可靠。
    /// </summary>
    public static (long Bytes, long Items) QueryRecycleBin()
    {
        long bytes = 0, items = 0;
        var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (SHQueryRecycleBin(d.Name, ref info) == 0)
                {
                    bytes += info.i64Size;
                    items += info.i64NumItems;
                }
            }
            return (bytes, items);
        }
        catch { }
        // 兜底：盘符枚举异常时退回整体查询（旧行为）
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>清空回收站（不确认、无界面、无声音）。返回 HRESULT。</summary>
    public static int EmptyRecycleBin()
        => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public short fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    private const uint FO_DELETE = 3;
    private const short FOF_ALLOWUNDO = 0x40;
    private const short FOF_NOCONFIRMATION = 0x10;
    private const short FOF_SILENT = 0x4;
    private const short FOF_NOERRORUI = 0x400;

    /// <summary>
    /// SHFileOperation FO_DELETE 公共实现。pFrom 为以 \0 分隔、\0\0 结尾的多路径列表。
    /// 历史 bug（用户实测「移入回收站不可行」）：线程池是 MTA，SHFileOperation 属 shell API，
    /// 回收站（ALLOWUNDO）操作在 MTA 线程下 shell 上下文初始化不完整，可能静默失败或退化为
    /// 永久删除 —— 必须切 STA 线程执行。FOF_SILENT|FOF_NOERRORUI 下无 UI 阻塞点，同步等待安全。
    /// </summary>
    private static int ShDeleteToRecycleBin(string multiPathList)
    {
        int result = 0;
        var t = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCTW
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FO_DELETE,
                    pFrom = multiPathList,
                    pTo = "",
                    fFlags = (short)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
                    fAnyOperationsAborted = 0,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = ""
                };
                result = SHFileOperation(ref op);
            }
            catch { result = unchecked((int)0x8000FFFF); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join();
        return result;
    }

    /// <summary>将单个文件/目录移入回收站（安静模式）。0 = 成功。</summary>
    public static int DeleteToRecycleBin(string path)
        => ShDeleteToRecycleBin(path + "\0");

    /// <summary>
    /// 批量将多个文件/目录移入回收站：pFrom 支持多路径，单次调用完成整批删除。
    /// 历史 bug：SHFileOperation 的 pFrom 列表存在约 32K 字符的隐性上限，
    /// 按累计字符预算（16000 字符/批）切批，远低于上限且仍保持批量优势。
    /// 返回 0 = 所有批次调用成功（不代表每一项都删净）；非 0 = 存在失败，调用方应逐项复核。
    /// </summary>
    public static int DeleteToRecycleBinBatch(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;
        int result = 0;
        var buffer = new List<string>(64);
        int chars = 0;
        foreach (var p in paths)
        {
            if (chars + p.Length + 1 > 16000 && buffer.Count > 0)
            {
                var r = ShDeleteToRecycleBin(string.Join("\0", buffer) + "\0");
                if (r != 0) result = r;
                buffer.Clear();
                chars = 0;
            }
            buffer.Add(p);
            chars += p.Length + 1;
        }
        if (buffer.Count > 0)
        {
            var r = ShDeleteToRecycleBin(string.Join("\0", buffer) + "\0");
            if (r != 0) result = r;
        }
        return result;
    }
}

/// <summary>
/// 提权辅助（宿主 Interop/ElevationHelper 同源移植，仅保留本域用到的两个成员）：
/// 日常普通权限运行；管理类清理目标合并为单个脚本一次性提升执行。
/// </summary>
internal static class ElevationHelper
{
    /// <summary>当前进程是否已以管理员运行。</summary>
    public static bool IsCurrentUserAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 以管理员身份运行一个 cmd 批处理脚本（触发一次 UAC）。
    /// 脚本内部应输出 ##OK##/##FAIL## 标记到 stdout，stdout 被重定向到 outLog。
    /// </summary>
    public static Task<(int ExitCode, string Output)> RunElevatedScriptAsync(string scriptPath, string outLog, int timeoutMs = 300_000)
    {
        return Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/d /c \"\"{scriptPath}\" > \"{outLog}\" 2>&1\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi)!;
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return (-1, "##FAIL## 提权脚本执行超时");
                }
                var output = File.Exists(outLog) ? File.ReadAllText(outLog) : "";
                return (p.ExitCode, output ?? "");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC 弹窗被用户取消：历史 bug 此处抛异常直接中断整轮清理（普通目标已清完但后续流程全部中断）
                return (-1, "##UAC_CANCELLED##");
            }
            catch (Exception ex)
            {
                return (-1, $"##FAIL## 无法启动提权脚本：{ex.Message}");
            }
        });
    }
}
