using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Helpers;

namespace QingShu.Toolkit.Services.Usb;

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
