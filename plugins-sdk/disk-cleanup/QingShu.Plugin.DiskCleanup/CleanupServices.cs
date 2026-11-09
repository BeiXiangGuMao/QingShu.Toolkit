using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Plugin.DiskCleanup;

// ================= 清理服务（宿主 Services/Cleanup 三服务同源移植） =================
// 日志适配：宿主日志服务 → 插件 Action<string,string>（level, message）→ IPluginContext.Log。
// 清理报告落盘：宿主 SaveCleanupReport → 插件私有 DataFolder\reports\yyyy-MM-dd\cleanup-*.json
// （与宿主日志目录结构同口径，插件卸载不残留宿主目录垃圾）。

public sealed record CleanScanTarget(CleanTargetDef Def, long SizeBytes, int FileCount);

public interface ICleanupService
{
    /// <summary>常规清理目标定义（含回收站）。</summary>
    IReadOnlyList<CleanTargetDef> Targets { get; }
    Task<List<CleanScanTarget>> ScanAsync(IReadOnlyList<CleanTargetDef> targets, IProgress<CleanScanTarget>? progress, CancellationToken ct);
    /// <summary>执行清理（回收站/永久删除按设置；需要管理员的目标若当前非管理员则自动走提权脚本）。</summary>
    Task<CleanupReport> CleanAsync(IReadOnlyList<CleanScanTarget> targets, bool toRecycleBin, IProgress<string>? log, CancellationToken ct);
}

/// <summary>常规磁盘清理引擎：扫描 → 询问式勾选 → 逐文件安全删除（含提权目标合并 UAC）。</summary>
public sealed class CleanupService : ICleanupService
{
    private readonly Action<string, string> _log;
    private readonly string _reportRoot; // 插件私有 DataFolder（null/空 = 不落盘）
    public IReadOnlyList<CleanTargetDef> Targets { get; } = BuildTargets();

    public CleanupService(Action<string, string> log, string reportRoot)
    {
        _log = log;
        _reportRoot = reportRoot;
    }

    private static string L(string p) => Environment.ExpandEnvironmentVariables(p);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static IReadOnlyList<CleanTargetDef> BuildTargets() => new List<CleanTargetDef>
    {
        new()
        {
            Id = "temp.user", Name = "用户临时文件", Risk = CleanRisk.Low,
            Description = "当前用户 %TEMP% 目录的临时文件，安全可清理。",
            Paths = () => new[] { Path.Combine(LocalAppData, "Temp") }
        },
        new()
        {
            Id = "temp.windows", Name = "系统临时文件", Risk = CleanRisk.Low, NeedsAdmin = true,
            Description = "C:\\Windows\\Temp 安装残留临时文件。",
            Paths = () => new[] { @"C:\Windows\Temp" }
        },
        new()
        {
            Id = "windows.update_cache", Name = "Windows 更新下载缓存", Risk = CleanRisk.Medium, NeedsAdmin = true,
            Description = "SoftwareDistribution\\Download 已下载的更新包；若系统正在安装更新会自动跳过被占用文件。",
            Paths = () => new[] { @"C:\Windows\SoftwareDistribution\Download" }
        },
        new()
        {
            Id = "delivery.optimization", Name = "传递优化缓存", Risk = CleanRisk.Medium, NeedsAdmin = true,
            Description = "Windows 更新 P2P 分发缓存。",
            Paths = () => new[] { @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache" }
        },
        new()
        {
            Id = "thumbnails", Name = "缩略图缓存", Risk = CleanRisk.Low,
            Description = "资源管理器缩略图/图标缓存，清理后首次打开文件夹会重建（略慢一次）。",
            Paths = () => new[] { Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer") }
        },
        new()
        {
            Id = "edge.cache", Name = "Edge 浏览器缓存", Risk = CleanRisk.Low,
            Description = "Microsoft Edge 各用户配置的 Cache / Code Cache / GPUCache（不影响收藏与密码）。",
            Paths = () => EnumProfiles(Path.Combine(LocalAppData, @"Microsoft\Edge\User Data"))
        },
        new()
        {
            Id = "chrome.cache", Name = "Chrome 浏览器缓存", Risk = CleanRisk.Low,
            Description = "Google Chrome 各用户配置的缓存目录（不影响收藏与密码）。",
            Paths = () => EnumProfiles(Path.Combine(LocalAppData, @"Google\Chrome\User Data"))
        },
        new()
        {
            Id = "firefox.cache", Name = "Firefox 缓存", Risk = CleanRisk.Low,
            Description = "Firefox 各 Profile 的 cache2 缓存。",
            Paths = () => EnumFirefox(Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles"))
        },
        new()
        {
            Id = "wer", Name = "系统错误报告", Risk = CleanRisk.Low,
            Description = "Windows 错误报告（WER）队列与存档。",
            Paths = () => new[] { Path.Combine(LocalAppData, @"Microsoft\Windows\WER"), @"C:\ProgramData\Microsoft\Windows\WER" }
        },
        new()
        {
            Id = "d3dcache", Name = "DirectX 着色器缓存", Risk = CleanRisk.Low,
            Description = "D3DSCache 图形着色器缓存，清理后首次打开应用会重建。",
            Paths = () => new[] { Path.Combine(LocalAppData, "D3DSCache") }
        },
        new()
        {
            Id = "crashdumps", Name = "崩溃转储文件", Risk = CleanRisk.Medium, NeedsAdmin = true,
            Description = "应用崩溃 dump 与系统 Minidump，仅用于排错，可安全清理。",
            Paths = () => new[] { Path.Combine(LocalAppData, "CrashDumps"), @"C:\Windows\Minidump" }
        },
        new()
        {
            Id = "office.cache", Name = "Office 文档缓存", Risk = CleanRisk.Medium,
            Description = "Office 上传中心文件缓存，默认不勾选；正在编辑云端文档时请勿清理。",
            DefaultSelected = false,
            Paths = () => new[] { Path.Combine(LocalAppData, @"Microsoft\Office\16.0\OfficeFileCache"), Path.Combine(LocalAppData, @"Microsoft\Office\17.0\OfficeFileCache") }
        },
        new()
        {
            Id = "windows.old", Name = "旧系统备份 Windows.old", Risk = CleanRisk.High, NeedsAdmin = true,
            Description = "系统升级保留的旧系统目录（体积巨大）。删除后无法回退旧系统，请谨慎勾选！",
            DefaultSelected = false,
            Paths = () => Directory.Exists(@"C:\Windows.old") ? new[] { @"C:\Windows.old" } : Array.Empty<string>()
        },
        new()
        {
            Id = "recyclebin", Name = "回收站（所有分区）", Risk = CleanRisk.Medium, IsRecycleBin = true,
            Description = "清空所有分区的回收站，删除后不可恢复。",
            Paths = () => Array.Empty<string>()
        }
    };

    private static IEnumerable<string> EnumProfiles(string userDataDir)
    {
        if (!Directory.Exists(userDataDir)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(userDataDir))
        {
            var name = Path.GetFileName(profile);
            if (name is "System Profile" or "Crashpad" or "Guest Profile") continue;
            foreach (var sub in new[] { "Cache", "Cache\\Cache_Data", "Code Cache", "GPUCache" })
            {
                var p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) yield return p;
            }
        }
    }

    private static IEnumerable<string> EnumFirefox(string profilesDir)
    {
        if (!Directory.Exists(profilesDir)) yield break;
        foreach (var profile in Directory.EnumerateDirectories(profilesDir))
        {
            var p = Path.Combine(profile, "cache2");
            if (Directory.Exists(p)) yield return p;
        }
    }

    public Task<List<CleanScanTarget>> ScanAsync(IReadOnlyList<CleanTargetDef> targets, IProgress<CleanScanTarget>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var list = new List<CleanScanTarget>();
            foreach (var def in targets)
            {
                ct.ThrowIfCancellationRequested();
                long size = 0;
                int files = 0;
                try
                {
                    if (def.IsRecycleBin)
                    {
                        var (bytes, items) = NativeRecycleBin.QueryRecycleBin();
                        size = bytes;
                        files = (int)Math.Min(items, int.MaxValue);
                    }
                    else
                    {
                        foreach (var path in def.Paths())
                        {
                            if (Directory.Exists(path))
                            {
                                var (b, f) = FileOpsHelper.GetDirectorySize(path);
                                size += b;
                                files += f;
                            }
                            else if (def.Id == "thumbnails" && Directory.Exists(Path.GetDirectoryName(path)))
                            {
                                var dir = Path.GetDirectoryName(path)!;
                                var (b, f) = FileOpsHelper.GetPatternSize(dir, "thumbcache_*.db");
                                size += b; files += f;
                                var (b2, f2) = FileOpsHelper.GetPatternSize(dir, "iconcache_*.db");
                                size += b2; files += f2;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 逐目标故障隔离：单个坏路径/权限问题只影响自身统计，不再让整次扫描报错
                    _log("warn", $"扫描目标 {def.Id} 统计失败（部分目录不可访问）：{ex.Message}");
                }
                var target = new CleanScanTarget(def, size, files);
                list.Add(target);
                progress?.Report(target);
            }
            return list;
        });

    public async Task<CleanupReport> CleanAsync(IReadOnlyList<CleanScanTarget> targets, bool toRecycleBin, IProgress<string>? log, CancellationToken ct)
    {
        var report = new CleanupReport { StartedAt = DateTime.Now };
        var normal = targets.Where(t => !t.Def.NeedsAdmin).ToList();
        var admin = targets.Where(t => t.Def.NeedsAdmin).ToList();

        foreach (var t in normal)
        {
            ct.ThrowIfCancellationRequested();
            await CleanOneAsync(t, toRecycleBin, report, log).ConfigureAwait(true);
        }

        if (admin.Count > 0)
        {
            if (ElevationHelper.IsCurrentUserAdmin())
            {
                foreach (var t in admin)
                {
                    ct.ThrowIfCancellationRequested();
                    await CleanOneAsync(t, toRecycleBin, report, log).ConfigureAwait(true);
                }
            }
            else
            {
                // 提权批处理：cmd 删除目录内容（不需要逐文件精细记录）
                var (script, outLog) = BuildAdminCleanScript(admin, toRecycleBin);
                log?.Report($"需要管理员权限的目标（{admin.Count} 项）将合并为一次 UAC 执行…");
                var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
                int okCount = 0, failCount = 0;
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("##OK##", StringComparison.Ordinal))
                    {
                        var id = line["##OK##".Length..].Trim();
                        var def = Targets.FirstOrDefault(x => x.Id == id);
                        report.Details.Add($"[提权] {def?.Name ?? id} 清理完成");
                        log?.Report($"✓ [提权] {def?.Name ?? id} 清理完成");
                        okCount++;
                    }
                    else if (line.StartsWith("##FAIL##", StringComparison.Ordinal))
                    {
                        // 历史 bug：失败标记被静默丢弃，用户看到“清理完成”实际没删
                        var id = line["##FAIL##".Length..].Trim();
                        var def = Targets.FirstOrDefault(x => x.Id == id);
                        var scanT = admin.FirstOrDefault(x => x.Def.Id == id);
                        report.FailedFiles += Math.Max(1, scanT?.FileCount ?? 1);
                        report.Details.Add($"[提权] {def?.Name ?? id} 清理失败（目录被占用或权限不足）");
                        log?.Report($"✗ [提权] {def?.Name ?? id} 清理失败（可能被系统占用，稍后重试）");
                        failCount++;
                    }
                }
                if (output.Contains("##UAC_CANCELLED##") || (okCount == 0 && failCount == 0 && admin.Count > 0))
                    log?.Report("已取消 UAC 或脚本未执行，管理员目标本次未清理（其余目标不受影响）。");
                else if (failCount > 0)
                    log?.Report($"[提权] 完成但 {failCount} 项失败，详见上方提示。");
            }
        }

        report.FinishedAt = DateTime.Now;
        SaveReport(report);
        _log("info", $"清理完成：{report.ToSummary()}");
        return report;
    }

    private async Task CleanOneAsync(CleanScanTarget t, bool toRecycleBin, CleanupReport report, IProgress<string>? log)
    {
        if (t.Def.IsRecycleBin)
        {
            // 实时复查而非信任扫描时快照：勾选到执行之间回收站可能已变化，
            // 且历史 bug（清理传零值）曾导致永远走“已是空的”分支
            var (liveBytes, liveItems) = await Task.Run(() => NativeRecycleBin.QueryRecycleBin()).ConfigureAwait(true);
            if (liveBytes <= 0 && liveItems <= 0) { log?.Report("回收站已是空的。"); return; }
            var hr = await Task.Run(() => NativeRecycleBin.EmptyRecycleBin()).ConfigureAwait(true);
            if (hr == 0)
            {
                report.FreedBytes += liveBytes;
                report.DeletedFiles += (int)Math.Min(liveItems, int.MaxValue);
                report.Details.Add($"{t.Def.Name}: 清空 {liveItems} 项，释放 {FormatHelper.Bytes(liveBytes)}");
                log?.Report($"✓ {t.Def.Name}: 清空 {liveItems} 项，释放 {FormatHelper.Bytes(liveBytes)}");
            }
            else
            {
                report.FailedFiles += (int)Math.Max(1, liveItems);
                log?.Report($"✗ {t.Def.Name}: 清空失败 (HRESULT 0x{hr:X8})");
            }
            return;
        }

        foreach (var path in t.Def.Paths())
        {
            if (!Directory.Exists(path)) continue;
            log?.Report($"清理 {t.Def.Name}：{path}");
            var (deleted, freed, failed, batchNote) = await Task.Run(() =>
            {
                if (toRecycleBin)
                    return CleanToRecycleBinBatched(path);
                var (d, f, fl) = FileOpsHelper.CleanDirectoryContents(path);
                return (d, f, fl, (string?)null);
            }).ConfigureAwait(true);

            report.DeletedFiles += deleted;
            report.FreedBytes += freed;
            report.FailedFiles += failed;
            report.Details.Add($"{t.Def.Name}: 删除 {deleted} 项，释放 {FormatHelper.Bytes(freed)}{(failed > 0 ? $"，跳过 {failed} 项（被占用）" : "")}");
            log?.Report($"  → 删除 {deleted} 项，释放 {FormatHelper.Bytes(freed)}{(failed > 0 ? $"，跳过 {failed} 项（被占用）" : "")}");
            if (batchNote is { Length: > 0 })
                log?.Report($"  · {batchNote}");
            if (failed > 0 && freed == 0)
                log?.Report("  · 内容均被占用：相关程序（浏览器/资源管理器等）正在运行，关闭后重扫重清效果更佳。");
        }
    }

    /// <summary>回收站批量清理：整批 SHFileOperation（STA 线程 + 按字符预算分批），
    /// 再逐项复核存在性拿准确计数，批内残留项单独重试一次。
    /// 返回 (成功数, 释放字节, 失败数, 批量调用错误提示)。</summary>
    private static (int Ok, long Bytes, int Failed, string? BatchNote) CleanToRecycleBinBatched(string path)
    {
        var entries = new List<(string Path, long Bytes)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                try { entries.Add((f, new FileInfo(f).Length)); } catch { }
            }
            foreach (var d in Directory.EnumerateDirectories(path).ToList())
            {
                try { entries.Add((d, FileOpsHelper.GetDirectorySize(d).Bytes)); } catch { }
            }
        }
        catch { }

        int ok = 0, failed = 0;
        long bytes = 0;
        int? batchHr = null;
        foreach (var chunk in Chunk(entries, 400))
        {
            var hr = NativeRecycleBin.DeleteToRecycleBinBatch(chunk.Select(e => e.Path).ToList());
            if (hr != 0) batchHr = hr;
            foreach (var (p, len) in chunk)
            {
                if (!PathStillExists(p)) { ok++; bytes += len; continue; }
                // 批内个别残留（被占用）：单独重试一次拿准确结果
                try
                {
                    if (NativeRecycleBin.DeleteToRecycleBin(p) == 0 && !PathStillExists(p)) { ok++; bytes += len; }
                    else failed++;
                }
                catch { failed++; }
            }
        }
        // 批量调用返回非 0 但逐项复核全部删净：文件系统已生效，不再提示；
        // 反之带出 HRESULT 帮助远程定位（如 0x78 权限、0x71 路径无效）。
        string? note = batchHr is int code && failed > 0
            ? $"回收站批量删除返回错误 0x{code:X8}，其中 {failed} 项重试后仍残留（可能被占用或超长路径）"
            : null;
        return (ok, bytes, failed, note);
    }

    private static bool PathStillExists(string p) => Directory.Exists(p) || File.Exists(p);

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var buffer = new List<T>(size);
        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count >= size)
            {
                yield return buffer;
                buffer = new List<T>(size);
            }
        }
        if (buffer.Count > 0) yield return buffer;
    }

    private static (string Script, string OutLog) BuildAdminCleanScript(IReadOnlyList<CleanScanTarget> targets, bool toRecycleBin)
    {
        var dir = Path.Combine(LocalAppData, "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var script = Path.Combine(dir, $"clean-{ts}.cmd");
        var outLog = Path.Combine(dir, $"clean-{ts}.log");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo [QingShu] 正在以管理员权限清理…");
        foreach (var t in targets)
        {
            var paths = t.Def.Paths().Where(p => Directory.Exists(p)).ToList();
            if (paths.Count == 0) continue;
            // 单目标单标记：任一路径删后仍存在即 FAIL，全部消失才 OK（目录已被外部清掉也视为达成）。
            bool isWinOld = t.Def.Id == "windows.old";
            if (isWinOld)
            {
                foreach (var path in paths)
                {
                    // Windows.old 含只读系统属性，先清除属性再删（整目录删除，不重建）
                    sb.AppendLine($"takeown /f \"{path}\" /r /d y >nul 2>&1");
                    sb.AppendLine($"icacls \"{path}\" /grant administrators:F /t >nul 2>&1");
                }
            }
            sb.AppendLine("set QSTOK=1");
            foreach (var path in paths)
            {
                sb.AppendLine($"rd /s /q \"{path}\" >nul 2>&1");
                sb.AppendLine($"if exist \"{path}\" set QSTOK=0");
                if (!isWinOld)
                {
                    // 历史 bug：裸 rd /s /q 连目录本身一起删除（SoftwareDistribution\Download、DeliveryOptimization\Cache
                    // 等系统目录被移除后依赖系统自愈）。改为删除后立即重建空目录，保持目录存在。
                    // cmd 无法直接进回收站：提权目标退化为永久删除（UI 已明确告知）
                    sb.AppendLine($"md \"{path}\" >nul 2>&1");
                }
            }
            sb.AppendLine($"if \"%QSTOK%\"==\"1\" (echo ##OK##{t.Def.Id}) else (echo ##FAIL##{t.Def.Id})");
        }
        sb.AppendLine("echo ##DONE##");
        // 无 BOM 的 UTF-8：cmd 对带 BOM 的批处理首行会解析出乱码字符（'\ufeff@echo' 不是内部或外部命令）
        System.IO.File.WriteAllText(script, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (script, outLog);
    }

    /// <summary>清理报告落盘（插件私有 DataFolder\reports\，结构与宿主 SaveCleanupReport 同口径）。</summary>
    private void SaveReport(CleanupReport report)
    {
        try
        {
            if (string.IsNullOrEmpty(_reportRoot)) return;
            var dir = Path.Combine(_reportRoot, "reports", DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"cleanup-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), System.Text.Encoding.UTF8);
        }
        catch { }
    }
}

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
    private readonly Action<string, string> _log;
    public ImScanService(Action<string, string> log) => _log = log;

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    // 分类：目录名 → 展示类型。匹配规则为 Contains 顺序取先命中，
    // 故长键必须排在短键前：否则 filerecv 命中 file（「接收文件」显示成「文件」）、
    // msgattach 命中 attach（「会话附件」显示成「附件」）
    private static readonly (string Key, string Kind)[] KindMap =
    {
        ("msgattach", "会话附件"), ("filerecv", "接收文件"),
        ("attach", "附件"), ("file", "文件"),
        ("image", "图片"), ("pic", "图片"), ("ori", "原图"),
        ("video", "视频"), ("thumb", "缩略图"),
        ("cache", "缓存"), ("emotion", "表情"), ("emoji", "表情"),
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
        _log("info", $"微信扫描完成：{result.Count} 个久远文件（>{oldDays} 天）");
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
        _log("info", $"QQ 扫描完成：{result.Count} 个久远文件（>{oldDays} 天）");
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
                        if (NativeRecycleBin.DeleteToRecycleBin(p) == 0) { ok++; bytes += len; } else failed++;
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
        _log("info", $"IM 文件删除完成：成功 {ok}，失败 {failed}，释放 {FormatHelper.Bytes(bytes)}（{(toRecycleBin ? "回收站" : "永久")}）");
        return (ok, failed, bytes);
    }
}

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

    private readonly Action<string, string> _log;
    public LargeFileScanService(Action<string, string> log) => _log = log;

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
        _log("info", $"大文件扫描完成：{drive}: 共 {results.Count} 个 ≥{minMb}MB（用时 {sw.ElapsedMilliseconds / 1000.0:0.#}s）");
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
                        if (NativeRecycleBin.DeleteToRecycleBin(p) == 0) { ok++; bytes += len; } else failed++;
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
        _log("info", $"大文件删除完成：成功 {ok}，失败 {failed}，释放 {FormatHelper.Bytes(bytes)}");
        return (ok, failed, bytes);
    }
}
