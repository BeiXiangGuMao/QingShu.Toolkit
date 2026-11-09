using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QingShu.Plugin.UsbTool;

// ============================================================================
// U 盘四服务（宿主 Services/Usb 同源移植）：探查 / 健康检测 / 查杀 / 修复。
// 安全铁律不变：只对 USBSTOR 设备操作；系统卷绝不参与破坏性动作；
// 破坏性操作（扩容检测 / 重建分区）由 UI 二次确认后单独调用。
// 隔离区与提权脚本目录沿用宿主内置实现同一路径（回退兼容、人工可寻迹）。
// ============================================================================

// ============================ 探查 ============================

public interface IUsbProbeService
{
    /// <summary>枚举当前接入的 USB 存储盘（含卷映射）。按 PNPDeviceID=USBSTOR 过滤，系统盘不可能混入。</summary>
    Task<List<UsbDrive>> ListDrivesAsync();
}

/// <summary>USB 存储设备探查：WMI 物理盘 → 分区 → 逻辑卷三级关联 + 描述符补充。</summary>
public sealed class UsbProbeService : IUsbProbeService
{
    public async Task<List<UsbDrive>> ListDrivesAsync()
    {
        var drives = await WmiHelper.QueryAsync(
            "SELECT Index, Model, Size, SerialNumber, InterfaceType, PNPDeviceID FROM Win32_DiskDrive",
            mo => new
            {
                Index = ToInt(mo["Index"]),
                Model = (mo["Model"]?.ToString() ?? "").Trim(),
                Size = ToLong(mo["Size"]),
                Serial = (mo["SerialNumber"]?.ToString() ?? "").Trim(),
                IfType = (mo["InterfaceType"]?.ToString() ?? "").Trim(),
                Pnp = (mo["PNPDeviceID"]?.ToString() ?? "").Trim()
            }).ConfigureAwait(false);

        var result = new List<UsbDrive>();
        foreach (var d in drives.Where(d => d.Index >= 0))
        {
            // 只认 USBSTOR（U 盘 / USB 读卡器 / USB 移动硬盘桥）。系统盘是 ATA/NVMe，天然隔离。
            var isUsb = d.Pnp.Contains("USBSTOR", StringComparison.OrdinalIgnoreCase)
                        || d.IfType.Equals("USB", StringComparison.OrdinalIgnoreCase);
            if (!isUsb) continue;

            var volumes = await MapVolumesAsync(d.Index).ConfigureAwait(false);

            // 描述符补充：WMI 拿不到的序列号/总线类型从 STORAGE_DEVICE_DESCRIPTOR 兜底
            string busText = "USB";
            bool removable = true;
            try
            {
                var desc = NativeStorageIo.QueryDeviceDescriptor($@"\\.\PhysicalDrive{d.Index}");
                if (desc != null) { busText = desc.Value.BusType; removable = desc.Value.Removable; }
            }
            catch { }

            result.Add(new UsbDrive(d.Index, d.Pnp, d.Model, d.Serial, d.Size, busText, removable, volumes));
        }
        return result.OrderBy(x => x.DiskIndex).ToList();
    }

    private static async Task<List<UsbVolume>> MapVolumesAsync(int diskIndex)
    {
        var volumes = new List<UsbVolume>();
        var escaped = $@"\\.\PHYSICALDRIVE{diskIndex}".Replace(@"\", @"\\");
        var parts = await WmiHelper.QueryAsync(
            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escaped}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition",
            mo => mo["DeviceID"]?.ToString() ?? "").ConfigureAwait(false);

        foreach (var partId in parts)
        {
            var escPart = partId.Replace(@"\", @"\\");
            var logicals = await WmiHelper.QueryAsync(
                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escPart}'}} WHERE AssocClass = Win32_LogicalDiskToPartition",
                mo => new
                {
                    Id = mo["DeviceID"]?.ToString() ?? "",
                    Label = (mo["VolumeName"]?.ToString() ?? "").Trim(),
                    Fs = (mo["FileSystem"]?.ToString() ?? "").Trim().ToUpperInvariant(),
                    Total = ToLong(mo["Size"]),
                    Free = ToLong(mo["FreeSpace"])
                }).ConfigureAwait(false);

            foreach (var l in logicals.Where(l => l.Id.Length >= 2 && char.IsLetter(l.Id[0])))
                volumes.Add(new UsbVolume(l.Id[0].ToString(), string.IsNullOrEmpty(l.Label) ? "本地磁盘" : l.Label, l.Fs, l.Total, l.Free));
        }
        return volumes;
    }

    /// <summary>破坏性操作前的最后防线：确认盘符不是系统盘（Windows 所在卷）。</summary>
    public static bool IsSystemVolume(string letter)
    {
        var sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return string.Equals(letter + ":", sysRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
               || (letter + ":").StartsWith(sysRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static int ToInt(object? v) => int.TryParse(v?.ToString(), out var n) ? n : -1;

    private static long ToLong(object? v) => long.TryParse(v?.ToString(), out var n) ? n : 0;
}

// ============================ 健康检测 ============================

public interface IUsbHealthService
{
    /// <summary>只读健康检测：尾段探测 + 表面扫描 + 吞吐评估 + 写速度。</summary>
    Task<List<UsbCheckOutcome>> RunHealthCheckAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct);
    /// <summary>扩容盘写校验（破坏性：覆盖选定探针区域数据）。调用方必须已完成用户确认。</summary>
    Task<List<UsbCheckOutcome>> RunCapacityVerifyAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// 硬件级健康检测（用户态可达的最深层）：直接对 \\.\PhysicalDriveN 做原始扇区读写。
/// 说明：SMART 透传依赖 USB 桥芯片的 SAT 支持，兼容性差（多数U盘桥不支持或数据失真），
/// 故不提供 SMART 项，以「真实扇区读写表现」作为硬件健康依据——比 SMART 更直接。
/// </summary>
public sealed class UsbHealthService : IUsbHealthService
{
    private const int Chunk = 1 << 20; // 1 MiB
    private const int ReadTimeoutMs = 8000;
    private static readonly TimeSpan SurfaceBudget = TimeSpan.FromSeconds(150);

    public async Task<List<UsbCheckOutcome>> RunHealthCheckAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct)
    {
        var list = new List<UsbCheckOutcome>
        {
            UsbCheckOutcome.Pass("设备信息", $"型号 {drive.Model} · 容量 {drive.SizeText} · 总线 {drive.BusTypeText}",
                $"PnP: {drive.PnpId}", drive.Serial.Length > 0 ? $"序列号: {drive.Serial}" : "序列号: 设备未上报")
        };

        // ---- 尾段只读探测：假容量盘 / 尾部闪存衰减最先在末段暴露 ----
        var tail = await ProbeRegionAsync(drive, progress, ct,
            startFraction: 0.92, budgetBytes: Math.Min(96L << 20, Math.Max(8L << 20, drive.SizeBytes / 16)),
            timeBudgetMs: 60_000).ConfigureAwait(false);
        if (tail.ReadBytes == 0)
            list.Add(UsbCheckOutcome.Skip("尾段只读探测", "设备无法打开或尾段完全无响应（可能已被系统判为不可用）"));
        else if (tail.BadRegions == 0 && tail.SlowRegions == 0)
            list.Add(UsbCheckOutcome.Pass("尾段只读探测", $"末段 {FormatHelper.Bytes(tail.ReadBytes)} 读取正常"));
        else
            list.Add(UsbCheckOutcome.Fail("尾段只读探测",
                $"末段读取异常：坏区 {tail.BadRegions} 处 / 慢区 {tail.SlowRegions} 处",
                "末段坏区是扩容盘或闪存寿命末期的典型征兆，请立即备份文件并停用该U盘。",
                tail.Notes.Take(6).ToArray()));

        // ---- 表面读扫描（限时）：顺序读全盘，统计坏区/慢区 ----
        progress?.Report("表面读扫描中…");
        var surface = await ScanSurfaceAsync(drive, progress, ct).ConfigureAwait(false);
        if (surface.ReadBytes == 0)
            list.Add(UsbCheckOutcome.Skip("表面读扫描", "设备无法打开，跳过（请确认U盘可正常识别）"));
        else if (surface.BadRegions == 0 && surface.SlowRegions == 0)
            list.Add(UsbCheckOutcome.Pass("表面读扫描",
                $"{FormatHelper.Bytes(surface.ReadBytes)} 扫描完成 · 读吞吐 {surface.Mbps:0.#} MB/s"));
        else
            list.Add(UsbCheckOutcome.Fail("表面读扫描",
                $"发现坏区 {surface.BadRegions} 处 / 慢区 {surface.SlowRegions} 处（已扫 {FormatHelper.Bytes(surface.ReadBytes)}）",
                "存在物理坏块：重要文件请立即转移；若坏块持续增多，该U盘应报废。",
                surface.Notes.Take(6).ToArray()));

        // ---- 读吞吐评估 ----
        if (surface.ReadBytes > 0)
        {
            string verdict = surface.Mbps switch
            {
                >= 60 => "达到 USB 3.x 水平",
                >= 15 => "处于 USB 2.0 正常水平",
                >= 5 => "偏低（USB 2.0 正常约 25-35 MB/s）：接口降级、桥接芯片老化或闪存磨损",
                _ => "严重偏低：闪存磨损加剧或主控异常"
            };
            list.Add(surface.Mbps >= 15
                ? UsbCheckOutcome.Pass("读吞吐评估", $"顺序读 {surface.Mbps:0.#} MB/s —— {verdict}")
                : UsbCheckOutcome.Warn("读吞吐评估", $"顺序读 {surface.Mbps:0.#} MB/s —— {verdict}",
                    "可换 USB 口（优先机箱后置）/ 换数据线后复测对比。"));
        }

        // ---- 写速度测试（文件级，写穿模式测真实闪存速度）----
        var vol = drive.Volumes.FirstOrDefault(v => v.FreeBytes > (200L << 20));
        if (vol == null)
            list.Add(UsbCheckOutcome.Skip("写速度测试", "无可用卷（无盘符或剩余空间不足 200 MB），跳过"));
        else
        {
            progress?.Report("写速度测试中…");
            var ws = await WriteSpeedAsync(vol.Letter, ct).ConfigureAwait(false);
            if (ws == null)
                list.Add(UsbCheckOutcome.Skip("写速度测试", "写入测试失败（卷可能写保护或被占用）"));
            else if (ws.Value.Mbps >= 8)
                list.Add(UsbCheckOutcome.Pass("写速度测试", $"写穿模式 {ws.Value.Mbps:0.#} MB/s（实测 {FormatHelper.Bytes(ws.Value.Bytes)}）"));
            else
                list.Add(UsbCheckOutcome.Warn("写速度测试", $"写穿模式仅 {ws.Value.Mbps:0.#} MB/s（实测 {FormatHelper.Bytes(ws.Value.Bytes)}）",
                    "低于 8 MB/s 说明闪存写入偏慢：拷贝大文件卡顿与此相关；频繁掉速的U盘建议更换。"));
        }
        return list;
    }

    public async Task<List<UsbCheckOutcome>> RunCapacityVerifyAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct)
    {
        var list = new List<UsbCheckOutcome>();

        // 先把卷挤出缓存：卸载失败不阻断（探针区域数据本来就要覆盖）
        foreach (var v in drive.Volumes)
        {
            if (UsbProbeService.IsSystemVolume(v.Letter))
            {
                list.Add(UsbCheckOutcome.Fail("安全拦截", $"卷 {v.Letter}: 判定为系统卷，已拒绝破坏性检测", "系统卷绝不参与写校验。"));
                return list;
            }
            NativeStorageIo.TryLockAndDismountVolume(v.Letter);
        }

        long size = drive.SizeBytes;
        if (size <= 0)
        {
            list.Add(UsbCheckOutcome.Fail("扩容检测", "无法读取盘容量", "容量为 0，设备可能已损坏。"));
            return list;
        }

        // 探针点：8MB / 中点 / 95% / 末尾内缩 16MB（去重、与端点留距）
        var offsets = new[] { 8L << 20, size / 2, (long)(size * 0.95), size - (16L << 20) }
            .Where(o => o > 0 && o < size - (8L << 20))
            .Distinct().OrderBy(o => o).ToArray();

        var probes = new List<CapacityProbe>();
        foreach (var off in offsets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"扩容探针 {off / (1024 * 1024)} MB / {size / (1024 * 1024)} MB…");
            probes.Add(await ProbeOffsetAsync(drive, off, ct).ConfigureAwait(false));
        }

        NativeStorageIo.TryUpdateDiskProperties(drive.PhysicalPath);

        var bad = probes.Where(p => !p.WriteOk || !p.VerifyOk).ToList();
        string detail = string.Join("\n", probes.Select(p => p.Note));
        if (probes.Count == 0)
            list.Add(UsbCheckOutcome.Fail("扩容检测", "没有可下探的探针位置", "盘容量过小或已被系统收缩。"));
        else if (bad.Count == 0)
            list.Add(UsbCheckOutcome.Pass("扩容盘检测", $"{probes.Count} 个探针点写入+回读全部一致（抽样判定：容量真实）",
                "探针为抽样检测，可拦住绝大多数虚标盘；探针区域原数据已被覆盖，如需恢复请从备份还原。", detail));
        else
            list.Add(UsbCheckOutcome.Fail("扩容盘检测",
                $"{bad.Count}/{probes.Count} 个探针点异常 —— 高度疑似扩容盘（虚标容量）",
                "扩容盘超出真实容量的部分会静默丢数据：请立即备份数据、退货举报，并改用正品U盘。", detail));
        return list;
    }

    // ---------------- 区域读探测 ----------------

    private sealed record RegionStat(long ReadBytes, long BadRegions, long SlowRegions, double Mbps, List<string> Notes);

    private static async Task<RegionStat> ProbeRegionAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct,
        double startFraction, long budgetBytes, long? timeBudgetMs = null)
    {
        var notes = new List<string>();
        long read = 0, bad = 0, slow = 0;
        var sw = Stopwatch.StartNew();
        FileStream? fs = null;
        try
        {
            long start = (long)(drive.SizeBytes * startFraction);
            start -= start % 512;
            var buf = new byte[Chunk];
            for (long off = start; off < drive.SizeBytes && read < budgetBytes
                     && (timeBudgetMs is null || sw.ElapsedMilliseconds < timeBudgetMs); off += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                if (fs is null)
                {
                    var h = NativeStorageIo.OpenRaw(drive.PhysicalPath, write: false, exclusive: false);
                    if (h.IsInvalid)
                    {
                        bad++;
                        if (notes.Count < 12) notes.Add($"偏移 {FormatHelper.Bytes(off)}：设备重新打开失败");
                        break;
                    }
                    fs = new FileStream(h, FileAccess.Read, Chunk, isAsync: false);
                }
                var (n, ms, timedOut) = await ReadChunkAsync(fs, off, buf).ConfigureAwait(false);
                if (n <= 0)
                {
                    bad++;
                    if (notes.Count < 12)
                        notes.Add(timedOut
                            ? $"偏移 {FormatHelper.Bytes(off)}：读取超时（{ms} ms 无响应）"
                            : $"偏移 {FormatHelper.Bytes(off)}：读取失败");
                    if (timedOut)
                    {
                        // 超时的弃读任务可能仍挂在同一句柄上，与后续 Seek+Read 竞争同一文件指针
                        //（读到错位数据→误报坏/慢区）。弃用旧句柄，下一轮重新打开后再继续。
                        try { fs.Dispose(); } catch { }
                        fs = null;
                    }
                }
                else
                {
                    read += n;
                    if (ms > 2000) { slow++; if (notes.Count < 12) notes.Add($"偏移 {FormatHelper.Bytes(off)}：{ms} ms/MB"); }
                }
            }
            if (timeBudgetMs is not null && sw.ElapsedMilliseconds >= timeBudgetMs && read > 0)
                notes.Add("已按时间预算截断，未扫完全盘（结果仅代表已扫描区域）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { if (notes.Count < 12) notes.Add($"探测中断：{ex.Message}"); }
        finally { if (fs != null) { try { fs.Dispose(); } catch { } } }
        double mbps = sw.Elapsed.TotalSeconds > 0.2 ? read / (1024.0 * 1024) / sw.Elapsed.TotalSeconds : 0;
        return new RegionStat(read, bad, slow, mbps, notes);
    }

    private static Task<RegionStat> ScanSurfaceAsync(UsbDrive drive, IProgress<string>? progress, CancellationToken ct)
        => ProbeRegionAsync(drive, progress, ct, startFraction: 0.0, budgetBytes: long.MaxValue / 2, timeBudgetMs: (long)SurfaceBudget.TotalMilliseconds);

    /// <summary>返回 (读字节数, 耗时ms, 是否超时)。超时按坏区计；调用方超时后应重开句柄再继续。</summary>
    private static async Task<(int Read, long Ms, bool TimedOut)> ReadChunkAsync(FileStream fs, long offset, Memory<byte> buf)
    {
        fs.Seek(offset, SeekOrigin.Begin);
        var sw = Stopwatch.StartNew();
        var readTask = Task.Run(() =>
        {
            try { return fs.Read(buf.Span); }
            catch { return 0; }
        });
        var done = await Task.WhenAny(readTask, Task.Delay(ReadTimeoutMs)).ConfigureAwait(false);
        if (done != readTask) return (0, sw.ElapsedMilliseconds, true); // 超时按坏区计
        return (await readTask.ConfigureAwait(false), sw.ElapsedMilliseconds, false);
    }

    // ---------------- 写速度（文件级、写穿） ----------------

    private static async Task<(double Mbps, long Bytes)?> WriteSpeedAsync(string letter, CancellationToken ct)
    {
        var path = $"{letter}:\\__qingshu_speed_test.tmp";
        try
        {
            var buf = new byte[4 << 20];
            new Random(20260101).NextBytes(buf);
            long total = 0;
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.WriteThrough))
            {
                while (total < (128 << 20) && sw.ElapsedMilliseconds < 20_000)
                {
                    ct.ThrowIfCancellationRequested();
                    await fs.WriteAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                    total += buf.Length;
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            sw.Stop();
            return (total / (1024.0 * 1024) / Math.Max(0.05, sw.Elapsed.TotalSeconds), total);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---------------- 扩容探针（写+回读校验） ----------------

    private static async Task<CapacityProbe> ProbeOffsetAsync(UsbDrive drive, long offset, CancellationToken ct)
    {
        const int probeLen = 4 << 20; // 4 MiB
        try
        {
            // 写探针：以偏移量为标签填充，可识别「回卷假容量」
            var buf = new byte[probeLen];
            var tag = BitConverter.GetBytes(offset);
            for (int i = 0; i < buf.Length; i += tag.Length)
                Buffer.BlockCopy(tag, 0, buf, i, tag.Length);

            using (var h = NativeStorageIo.OpenRaw(drive.PhysicalPath, write: true, exclusive: false))
            {
                if (h.IsInvalid) return new CapacityProbe(offset, false, false, $"偏移 {offset / (1024 * 1024)} MB：设备打开失败");
                using var fs = new FileStream(h, FileAccess.Write, Chunk, isAsync: false);
                fs.Seek(offset, SeekOrigin.Begin);
                await fs.WriteAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            // 二次卸载卷：丢弃系统缓存，避免回读命中缓存而非介质（假盘伪装缓存的关键防线）
            foreach (var v in drive.Volumes) NativeStorageIo.TryLockAndDismountVolume(v.Letter);

            // 回读校验（独立句柄，绕开写缓存直读介质）
            var back = new byte[probeLen];
            using (var h = NativeStorageIo.OpenRaw(drive.PhysicalPath, write: false, exclusive: false))
            {
                if (h.IsInvalid) return new CapacityProbe(offset, true, false, $"偏移 {offset / (1024 * 1024)} MB：回读打开失败");
                using var fs = new FileStream(h, FileAccess.Read, Chunk, isAsync: false);
                fs.Seek(offset, SeekOrigin.Begin);
                int got = 0;
                while (got < probeLen)
                {
                    var (n, _, _) = await ReadChunkAsync(fs, offset + got, back.AsMemory(got)).ConfigureAwait(false);
                    if (n <= 0) return new CapacityProbe(offset, true, false, $"偏移 {offset / (1024 * 1024)} MB：回读失败（超出真实容量的典型表现）");
                    got += n;
                }
            }
            bool ok = back.AsSpan(0, probeLen).SequenceEqual(buf);
            return ok
                ? new CapacityProbe(offset, true, true, $"偏移 {offset / (1024 * 1024)} MB：写入+回读一致 ✓")
                : new CapacityProbe(offset, true, false, $"偏移 {offset / (1024 * 1024)} MB：回读内容不符（虚标容量的决定性证据）");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new CapacityProbe(offset, false, false, $"偏移 {offset / (1024 * 1024)} MB：探针异常 {ex.Message}"); }
    }
}

// ============================ 查杀 ============================

public interface IUsbScanService
{
    /// <summary>查杀扫描（启发式 + 特征，只读）。返回威胁清单。扫描在线程池执行，不占用调用方线程。</summary>
    Task<(List<UsbThreat> Threats, int FilesScanned, List<string> Notes)> ScanVolumeAsync(
        UsbVolume volume, IProgress<string>? progress, CancellationToken ct);
    /// <summary>隔离所选威胁文件（可恢复，附清单与成功清单）。</summary>
    Task<(int Ok, int Fail, List<string> Notes, List<string> Quarantined)> QuarantineAsync(IReadOnlyList<string> files, string letter);
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

    public Task<(List<UsbThreat>, int, List<string>)> ScanVolumeAsync(
        UsbVolume volume, IProgress<string>? progress, CancellationToken ct)
        // 递归扫描最长 90 秒 / 3 万文件：必须整体在线程池执行。此前方法体无真实 await，
        // 全部同步跑在调用方（UI）线程上——扫描期间界面完全冻结、进度文本永远不刷新。
        => Task.Run(() => ScanVolumeCore(volume, progress, ct), ct);

    private static (List<UsbThreat>, int, List<string>) ScanVolumeCore(
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
            // CheckAutorun 返回单条 UsbThreat?（非集合）：空即无 autorun 项，不得 AddRange
            var autorun = CheckAutorun(root);
            if (autorun != null) threats.Add(autorun);

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

        return (threats, scanned, notes);
    }

    // ---------------- 单文件判定 ----------------

    // static：仅使用静态成员（蠕虫名单/扩展名表/Ansi 解码器），被静态 ScanVolumeCore 调用
    private static UsbThreat? InspectFile(string root, string path, FileAttributes attrs)
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
                // open=/shellexecute= 之外，还必须检 shell\open\command= 形态——
                // 经典 SXS 蠕虫正是利用「资源管理器双击时优先执行 shell\open\command」落地的
                if (line.StartsWith("open=") || line.StartsWith("shellexecute=") || line.Contains("command="))
                {
                    var target = line[(line.IndexOf('=') + 1)..].Trim();
                    if (ExecExt.Any(e => target.Contains(e, StringComparison.Ordinal)) || target.Contains("\\", StringComparison.Ordinal))
                        return new UsbThreat(p, "Malicious", "autorun 自启动指向可执行体", $"autorun.inf 的 {line[..Math.Min(60, line.Length)]}… 会在插入/双击时拉起程序，判为恶意。");
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

    public Task<(int Ok, int Fail, List<string> Notes, List<string> Quarantined)> QuarantineAsync(IReadOnlyList<string> files, string letter)
    {
        return Task.Run(() =>
        {
            int ok = 0, fail = 0;
            var notes = new List<string>();
            var quarantined = new List<string>();
            // 隔离区沿用宿主内置实现同一路径：内置/插件两版共用一个隔离区，人工寻迹一致
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
                    quarantined.Add(f);
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
            return (ok, fail, notes, quarantined);
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
                    "此文件夹由青枢工具箱 U盘工具插件创建，用于阻止蠕虫写入 autorun.inf 自启动文件。\n请勿删除本目录。", Encoding.UTF8);
                File.SetAttributes(p, FileAttributes.ReadOnly);
                return (true, $"已创建免疫目录 {p}：蠕虫将无法写入同名的 autorun.inf 文件。");
            }
            catch (UnauthorizedAccessException) { return (false, "创建失败：U盘可能硬件写保护或权限不足。"); }
            catch (Exception ex) { return (false, $"创建失败：{ex.Message}"); }
        });
    }
}

// ============================ 修复 ============================

public interface IUsbRepairService
{
    /// <summary>按设备当前状态生成修复清单（危险项默认不勾选）。</summary>
    Task<List<UsbFixAction>> BuildPlanAsync(UsbDrive drive);
    /// <summary>执行选中的修复：普通动作本进程完成，管理类合并为一次 UAC。</summary>
    Task RunAsync(UsbDrive drive, IReadOnlyList<UsbFixAction> actions, IProgress<UsbFixAction>? progress, CancellationToken ct);
    /// <summary>重建分区表并快速格式化（破坏性，调用方必须已完成二次确认）。</summary>
    Task<UsbFixAction> RebuildPartitionAsync(UsbDrive drive, string fileSystem, IProgress<UsbFixAction>? progress, CancellationToken ct);
}

/// <summary>
/// U盘修复（硬件级可达范围）：设备栈重置（PnP 重新枚举）/ 卷配置刷新 / 文件系统查错 /
/// 写保护策略清除 / 盘符重建 / 分区表重建。安全边界：绝不对系统卷执行任何动作；
/// 破坏性动作（重建分区）必须由 UI 二次确认后单独调用。
/// </summary>
public sealed class UsbRepairService : IUsbRepairService
{
    public async Task<List<UsbFixAction>> BuildPlanAsync(UsbDrive drive)
    {
        var plan = new List<UsbFixAction>
        {
            new("refresh_volume", "更新设备与卷配置",
                "刷新系统对该U盘的容量/分区缓存（IOCTL_DISK_UPDATE_PROPERTIES），修复容量显示异常与盘符残影。", false, false),
            new("device_reset", "重置U盘设备栈",
                "禁用并重新启用 USB 存储设备（PnP 级重新枚举），修复识别异常、盘符丢失、设备假死。", true, false)
        };

        var main = drive.Volumes.OrderByDescending(v => v.TotalBytes).FirstOrDefault();
        if (main != null)
        {
            plan.Add(new UsbFixAction("chkdsk_fix", $"文件系统查错修复（{main.Letter}:）",
                $"chkdsk {main.Letter}: /f 修复文件系统错误与坏簇记录，解决目录损坏/文件消失/拷贝报错。需要管理员。", true, false));
            plan.Add(new UsbFixAction("clear_readonly", "清除软件写保护",
                "diskpart 清除磁盘/卷级只读策略（针对误设只读导致无法写入的情况；硬件写保护开关无效）。需要管理员。", true, false));
        }
        else
        {
            plan.Add(new UsbFixAction("assign_letter", "重建盘符映射",
                "diskpart 对该盘第一分区重新分配盘符，修复「识别到设备但没有盘符」。需要管理员。", true, false));
        }

        foreach (var a in plan) a.IsSelected = !a.Risky;
        return await Task.FromResult(plan).ConfigureAwait(false);
    }

    public async Task RunAsync(UsbDrive drive, IReadOnlyList<UsbFixAction> actions, IProgress<UsbFixAction>? progress, CancellationToken ct)
    {
        var normal = actions.Where(a => !a.NeedsAdmin).ToList();
        var admin = actions.Where(a => a.NeedsAdmin).ToList();

        foreach (var a in normal)
        {
            ct.ThrowIfCancellationRequested();
            a.Status = "Running";
            progress?.Report(a);
            try
            {
                bool ok = a.Id switch
                {
                    "refresh_volume" => NativeStorageIo.TryUpdateDiskProperties(drive.PhysicalPath),
                    _ => false
                };
                a.Status = ok ? "Ok" : "Fail";
                a.Output = ok ? "已刷新设备配置。" : "刷新失败（设备可能已离线）。";
            }
            catch (Exception ex) { a.Status = "Fail"; a.Output = ex.Message; }
            progress?.Report(a);
        }

        if (admin.Count > 0)
        {
            foreach (var a in admin) { a.Status = "Running"; progress?.Report(a); }
            try
            {
                var (script, outLog) = BuildAdminScript(drive, admin);
                var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
                bool uacCancelled = output.Contains("##UAC_CANCELLED##");
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var a in admin)
                {
                    var okLine = lines.FirstOrDefault(l => l.Contains($"##OK##{a.Id}"));
                    var failLine = lines.FirstOrDefault(l => l.Contains($"##FAIL##{a.Id}"));
                    a.Status = okLine != null ? "Ok" : "Fail";
                    a.Output = uacCancelled ? "用户取消了 UAC 授权，未执行。"
                        : okLine != null ? "执行成功。"
                        : $"未取得成功标记（exit={exit}）{failLine}".Trim();
                    progress?.Report(a);
                }
            }
            catch (Exception ex)
            {
                foreach (var a in admin) { a.Status = "Fail"; a.Output = $"提权执行失败：{ex.Message}"; progress?.Report(a); }
            }
        }
    }

    public async Task<UsbFixAction> RebuildPartitionAsync(UsbDrive drive, string fileSystem, IProgress<UsbFixAction>? progress, CancellationToken ct)
    {
        // FAT32 只支持到 32GB，超出自动切 exFAT（与 Windows 磁盘管理一致）
        if (fileSystem == "fat32" && drive.SizeBytes > 32L * 1024 * 1024 * 1024)
            fileSystem = "exfat";

        var action = new UsbFixAction("rebuild_partition", $"重建分区表并格式化（{fileSystem.ToUpperInvariant()}）",
            "clean 重建分区表 + 快速格式化：U盘所有数据将被清空。用于分区表损坏、容量识别异常、无法格式化的设备。", true, true)
        { Status = "Running" };
        progress?.Report(action);

        try
        {
            var (script, outLog) = BuildAdminScript(drive, new[] { action }, fileSystemOverride: fileSystem);
            var (exit, output) = await ElevationHelper.RunElevatedScriptAsync(script, outLog).ConfigureAwait(true);
            var ok = output.Contains($"##OK##{action.Id}");
            action.Status = ok ? "Ok" : "Fail";
            action.Output = ok ? "分区表已重建并完成格式化（新盘符可能需要重新插拔后出现）。"
                : $"执行失败（exit={exit}）：{Truncate(output, 400)}";
        }
        catch (Exception ex)
        {
            action.Status = "Fail";
            action.Output = $"提权执行失败：{ex.Message}";
        }
        progress?.Report(action);
        return action;
    }

    // ---------------- 提权脚本 ----------------

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n] + "…";

    private (string ScriptPath, string OutLog) BuildAdminScript(UsbDrive drive, IReadOnlyList<UsbFixAction> actions,
        string? fileSystemOverride = null)
    {
        // 脚本目录沿用宿主内置实现同一 temp 路径（回退兼容，人工寻迹一致）
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var script = Path.Combine(dir, $"usb-fix-{ts}.cmd");
        var outLog = Path.Combine(dir, $"usb-fix-{ts}.log");

        var main = drive.Volumes.OrderByDescending(v => v.TotalBytes).FirstOrDefault();
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo [QingShu] 正在执行U盘修复，请保持窗口开启…");

        foreach (var a in actions)
        {
            switch (a.Id)
            {
                case "device_reset":
                {
                    var pnp = drive.PnpId.Replace("'", "");
                    sb.AppendLine($"powershell -NoProfile -Command \"$id='{pnp}'; try{{ Get-PnpDevice -InstanceId $id -ErrorAction Stop | Disable-PnpDevice -Confirm:$false -ErrorAction Stop; Start-Sleep 2; Get-PnpDevice -InstanceId $id -ErrorAction Stop | Enable-PnpDevice -Confirm:$false -ErrorAction Stop; exit 0 }}catch{{ exit 1 }}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "chkdsk_fix":
                    if (main != null)
                    {
                        // chkdsk 退出码 0-2 视为成功（1=已修复错误），>=3 视为失败
                        sb.AppendLine($"(chkdsk {main.Letter}: /f >nul 2>&1 & if errorlevel 3 (echo ##FAIL##{a.Id}) else (echo ##OK##{a.Id}))");
                    }
                    break;
                case "clear_readonly":
                {
                    var dp = WriteDiskPartScript(ts, "ro",
                        $"select disk {drive.DiskIndex}",
                        "attributes disk clear readonly",
                        main != null ? $"select volume {main.Letter}" : "",
                        main != null ? "attributes volume clear readonly" : "");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "assign_letter":
                {
                    var dp = WriteDiskPartScript(ts, "assign",
                        $"select disk {drive.DiskIndex}",
                        "select partition 1",
                        "assign");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                case "rebuild_partition":
                {
                    var fs = fileSystemOverride ?? "exfat";
                    var dp = WriteDiskPartScript(ts, "rebuild",
                        $"select disk {drive.DiskIndex}",
                        "clean",
                        "create partition primary",
                        $"format fs={fs} quick label=QINGSHU",
                        "assign");
                    sb.AppendLine($"diskpart /s \"{dp}\" >nul 2>&1 && echo ##OK##{a.Id} || echo ##FAIL##{a.Id}");
                    break;
                }
                default:
                    sb.AppendLine($"echo ##FAIL##{a.Id} 未支持");
                    break;
            }
        }
        sb.AppendLine("echo ##DONE##");
        // cmd.exe 不容错 UTF-8 BOM（与网络修复/清理提权脚本同规格：无 BOM + chcp 65001）
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (script, outLog);
    }

    private static string WriteDiskPartScript(string ts, string tag, params string[] lines)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "temp");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, $"usb-dp-{ts}-{tag}.txt");
        File.WriteAllText(p, string.Join("\r\n", lines.Where(l => l.Length > 0)) + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // 全 ASCII 指令，无 BOM 保证 diskpart 兼容
        return p;
    }
}
