using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Services.Usb;

public interface IUsbScanService
{
    /// <summary>查杀扫描（启发式 + 特征，只读）。返回威胁清单。</summary>
    Task<(List<UsbThreat> Threats, int FilesScanned, List<string> Notes)> ScanVolumeAsync(
        UsbVolume volume, IProgress<string>? progress, CancellationToken ct);
    /// <summary>隔离所选威胁文件（可恢复，附清单）。</summary>
    Task<(int Ok, int Fail, List<string> Notes)> QuarantineAsync(IReadOnlyList<string> files, string letter);
    /// <summary>解除蠕虫隐藏属性（文件夹可见化）。</summary>
    Task<bool> UnhideFoldersAsync(string letter);
    /// <summary>创建 autorun.inf 免疫目录（阻止蠕虫落盘自启动文件）。</summary>
    Task<(bool Ok, string Message)> CreateImmunityAsync(string letter);
}

/// <summary>
/// U盘查杀（底层硬件级视角）：以「U盘蠕虫的落盘行为特征」为检测面——
/// autorun 自启动、伪装文档的可执行体、蠕虫同名/双扩展名、隐藏属性恶意脚本、
/// .lnk 劫持（字节级解析目标命令）、回收站落体。不联网、不查注册表，全程只读扫描。
/// </summary>
public sealed class UsbScanService : IUsbScanService
{
    static UsbScanService()
    {
        // .NET Core 下 GBK/ANSI 需显式注册代码页提供器（程序集在共享框架内，仅缺注册）
        try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }

    private const int MaxFiles = 30_000;
    private const int MaxDepth = 6;
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(90);

    // 文档记录充分的经典 U 盘蠕虫落盘名（校园机房历史流行株），全小写比对
    private static readonly string[] KnownWormFiles =
        { "sxs.exe", "rose.exe", "copy.exe", "host.exe", "kavo.exe", "ravmone.exe", "autorun.vbs", "setup.pif" };

    private static readonly string[] ExecExt = { ".exe", ".vbs", ".bat", ".cmd", ".scr", ".js", ".com", ".pif" };
    private static readonly string[] DocExt =
        { ".jpg", ".jpeg", ".png", ".gif", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt" };
    private static readonly string[] ShellMarkers = { "cmd.exe", "powershell", "mshta", "wscript", "cscript", "rundll32" };

    private static readonly Encoding Ansi = ResolveAnsi();

    private static Encoding ResolveAnsi()
    {
        try { return Encoding.GetEncoding(936); }
        catch { return Encoding.Latin1; } // 提供器注册失败时退 Latin1：ASCII 检测标记不受影响
    }

    public async Task<(List<UsbThreat>, int, List<string>)> ScanVolumeAsync(
        UsbVolume volume, IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<UsbThreat>();
        var notes = new List<string>();
        int scanned = 0;
        var root = $"{volume.Letter}:\\";
        var sw = Stopwatch.StartNew();

        try
        {
            // ---- 1. autorun.inf：文件形态即自启动入口，指向可执行体判恶意 ----
            threats.AddRange(CheckAutorun(root));

            // ---- 2. 递归扫描（限深/限数/限时，跳过重解析点）----
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && scanned < MaxFiles && sw.Elapsed < TimeBudget)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = stack.Pop();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(dir); }
                catch (UnauthorizedAccessException) { notes.Add($"无权限：{dir}"); continue; }
                catch (IOException) { notes.Add($"读取失败：{dir}"); continue; }

                foreach (var path in entries)
                {
                    if (scanned >= MaxFiles) break;
                    try
                    {
                        var attrs = File.GetAttributes(path);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;
                        if ((attrs & FileAttributes.Directory) != 0)
                        {
                            if (depth < MaxDepth) stack.Push((path, depth + 1));
                            continue;
                        }
                        scanned++;
                        if (scanned % 500 == 0) progress?.Report($"已扫描 {scanned} 个文件…");
                        var t = InspectFile(root, path, attrs);
                        if (t != null) threats.Add(t);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (scanned >= MaxFiles) notes.Add($"达到文件数上限 {MaxFiles}，扫描提前结束");
            if (sw.Elapsed >= TimeBudget) notes.Add("达到时间预算 90 秒，扫描提前结束（结果仅代表已扫描部分）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { notes.Add($"扫描异常：{ex.Message}"); }

        return await Task.FromResult((threats, scanned, notes)).ConfigureAwait(false);
    }

    // ---------------- 单文件判定 ----------------

    private UsbThreat? InspectFile(string root, string path, FileAttributes attrs)
    {
        var name = Path.GetFileName(path);
        var lower = name.ToLowerInvariant();
        var ext = Path.GetExtension(lower);
        bool inRoot = path.Equals(root + name, StringComparison.OrdinalIgnoreCase);

        // 已知蠕虫名
        if (KnownWormFiles.Contains(lower))
            return new UsbThreat(path, "Malicious", "蠕虫落盘文件", $"命中已知U盘蠕虫文件名：{name}");

        // 双扩展名伪装（照片.jpg.exe）
        if (ext == ".exe" || ext == ".scr" || ext == ".com" || ext == ".pif")
        {
            var stem = lower[..^ext.Length];
            if (DocExt.Any(d => stem.EndsWith(d, StringComparison.Ordinal)))
                return new UsbThreat(path, "Malicious", "伪装成文档的可执行体", $"「{name}」以文档扩展名伪装（双扩展名），是U盘蠕虫的经典投放形态。");

            // 与同名文件夹并存（文件夹模仿）
            var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", lower[..^ext.Length]);
            if (Directory.Exists(folder))
                return new UsbThreat(path, "Malicious", "模仿文件夹的可执行体", $"「{name}」与同名文件夹并存，诱导双击替换真文件夹。");

            // 根目录/浅层的隐藏+系统可执行体
            if (depth2(path, root) <= 2 && attrs.HasFlag(FileAttributes.Hidden) && attrs.HasFlag(FileAttributes.System))
                return new UsbThreat(path, "Malicious", "隐藏+系统属性的可执行体", $"「{name}」带隐藏+系统属性且位于浅层，正常软件不会如此放置。");
        }

        // 文档扩展名但实为 PE（MZ 头）
        if (DocExt.Contains(ext) && IsPeFile(path))
            return new UsbThreat(path, "Malicious", "文档外壳可执行体", $"「{name}」扩展名是文档但文件头为 EXE（MZ），属于伪装。");

        // 根目录 .lnk：字节级检查目标是否指向命令解释器/脚本宿主
        if (inRoot && ext == ".lnk")
            return CheckShortcut(path, name);

        // 回收站目录内新投放的可执行体
        var dirName = (Path.GetDirectoryName(path) ?? "").TrimEnd('\\');
        if (dirName.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
            dirName.Contains("RECYCLER", StringComparison.OrdinalIgnoreCase))
        {
            if (ExecExt.Contains(ext))
                return new UsbThreat(path, "Suspicious", "回收站内可执行体", $"「{name}」藏于回收站目录，是蠕虫中转的常见位置。");
        }

        return null;
    }

    private static int depth2(string path, string root)
    {
        var rel = path[root.Length..];
        int n = 0;
        foreach (var c in rel) if (c == '\\' || c == '/') n++;
        return n;
    }

    private static UsbThreat? CheckAutorun(string root)
    {
        var p = root + "autorun.inf";
        try
        {
            if (Directory.Exists(p))
                return new UsbThreat(p, "Suspicious", "autorun.inf 目录（免疫/伪装）", "autorun.inf 以目录形态存在：若非本工具创建的免疫目录，请人工确认来源。");
            if (!File.Exists(p)) return null;
            var bytes = File.ReadAllBytes(p);
            if (bytes.Length > 1 << 20) return new UsbThreat(p, "Malicious", "autorun.inf 异常超长", "autorun.inf 超过 1 MB，正常 autorun 不会如此。");
            var text = Ansi.GetString(bytes); // 中文环境 autorun 常为 GBK/ANSI
            var lines = text.Split('\n');
            foreach (var raw in lines)
            {
                var line = raw.Trim().ToLowerInvariant();
                if (line.StartsWith("open=") || line.StartsWith("shellexecute="))
                {
                    var target = line[(line.IndexOf('=') + 1)..].Trim();
                    if (ExecExt.Any(e => target.Contains(e, StringComparison.Ordinal)) || target.Contains("\\", StringComparison.Ordinal))
                        return new UsbThreat(p, "Malicious", "autorun 自启动指向可执行体", $"autorun.inf 的 {line[..Math.Min(60, line.Length)]}… 会在插入时拉起程序，判为恶意。");
                }
            }
            return new UsbThreat(p, "Suspicious", "autorun.inf 存在", "autorun.inf 存在但未指向可执行体：蠕虫残留或历史遗留，建议隔离后重建免疫目录。");
        }
        catch (Exception ex) { return new UsbThreat(p, "Suspicious", "autorun.inf 读取失败", ex.Message); }
    }

    private static UsbThreat? CheckShortcut(string path, string name)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 1 << 16) return null;
            // .lnk 目标以 UTF-16LE 与 ANSI 两种形态存在，双编码查找命令解释器标记
            string utf16 = Encoding.Unicode.GetString(bytes);
            string ansi = Ansi.GetString(bytes);
            var hit = ShellMarkers.FirstOrDefault(m => utf16.Contains(m, StringComparison.OrdinalIgnoreCase) || ansi.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
                return new UsbThreat(path, "Malicious", "快捷方式劫持", $"「{name}」的目标命令包含 {hit}（.lnk 蠕虫特征：真文件被藏起，快捷方式拉起恶意命令）。");
            return new UsbThreat(path, "Suspicious", "根目录快捷方式", $"「{name}」位于U盘根目录：.lnk 蠕虫会把真实文件隐藏并留下同名快捷方式，请确认指向。");
        }
        catch { return new UsbThreat(path, "Suspicious", "快捷方式无法解析", $"「{name}」读取失败。"); }
    }

    private static bool IsPeFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return fs.Read(head) == 2 && head[0] == 0x4D && head[1] == 0x5A;
        }
        catch { return false; }
    }

    // ---------------- 处置动作 ----------------

    public Task<(int Ok, int Fail, List<string> Notes)> QuarantineAsync(IReadOnlyList<string> files, string letter)
    {
        return Task.Run(() =>
        {
            int ok = 0, fail = 0;
            var notes = new List<string>();
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingShu.Toolkit", "quarantine", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + letter);
            Directory.CreateDirectory(root);
            foreach (var f in files)
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    var dest = Path.Combine(root, f.Length >= 3 ? f[3..].Replace('\\', '_') : Path.GetFileName(f));
                    File.Move(f, dest, overwrite: false);
                    notes.Add($"已隔离：{f} → {dest}");
                    ok++;
                }
                catch (UnauthorizedAccessException)
                {
                    notes.Add($"拒绝访问（疑似硬件写保护或需管理员）：{f}");
                    fail++;
                }
                catch (Exception ex)
                {
                    notes.Add($"失败：{f}（{ex.Message}）");
                    fail++;
                }
            }
            File.WriteAllText(Path.Combine(root, "manifest.txt"),
                "隔离时间: " + DateTime.Now + "\n" + string.Join("\n", notes), Encoding.UTF8);
            return (ok, fail, notes);
        });
    }

    public async Task<bool> UnhideFoldersAsync(string letter)
    {
        // attrib /s /d 全盘递归：解除蠕虫对文件与文件夹的隐藏+系统属性
        var r = await ProcessRunner.RunAsync($@"attrib -s -h /s /d ""{letter}:\*""", timeoutMs: 120_000).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    public Task<(bool Ok, string Message)> CreateImmunityAsync(string letter)
    {
        return Task.Run(() =>
        {
            var p = $"{letter}:\\autorun.inf";
            try
            {
                if (File.Exists(p))
                {
                    // 普通文件形态的 autorun.inf 应先经查杀隔离，这里只做防复发
                    return (false, "已存在 autorun.inf 文件：请先在查杀列表中隔离它，再点免疫。");
                }
                if (!Directory.Exists(p))
                    Directory.CreateDirectory(p);
                File.WriteAllText(Path.Combine(p, "此目录为U盘免疫目录.txt"),
                    "此文件夹由青枢工具箱创建，用于阻止蠕虫写入 autorun.inf 自启动文件。\n请勿删除本目录。", Encoding.UTF8);
                File.SetAttributes(p, FileAttributes.ReadOnly);
                return (true, $"已创建免疫目录 {p}：蠕虫将无法写入同名的 autorun.inf 文件。");
            }
            catch (UnauthorizedAccessException) { return (false, "创建失败：U盘可能硬件写保护或权限不足。"); }
            catch (Exception ex) { return (false, $"创建失败：{ex.Message}"); }
        });
    }
}
