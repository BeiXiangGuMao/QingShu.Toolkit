using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace QingShu.Toolkit.Helpers;

/// <summary>
/// 下载吞吐实测引擎（低速专项核心）：
/// 1) 顺序探测候选大文件端点（腾讯/微软 CDN 与教育镜像优先，命中校园网白名单概率高）；
/// 2) 取可达的前 2 个端点，各以多连接 Range 限时下载（默认 8 秒、4 连接）；
/// 3) 汇总每端点平均/峰值速率，取最优端点作为代表带宽，并给出体验档位。
/// 全程只读下载、不落盘、不运行任何脚本；单点命令可逆，测速流量有总量上限。
/// </summary>
public static class SpeedTestHelper
{
    public sealed record Candidate(string Url, string Label);

    /// <summary>内置候选端点（按校园网可达概率排序；均为版本冻结、常年常驻的大文件）。</summary>
    public static readonly Candidate[] DefaultCandidates =
    {
        // 腾讯 CDN：微信安装包为多年不变的固定直链，校内几乎必然可达
        new("https://dldir1.qq.com/weixin/Windows/WeChatSetup.exe", "腾讯微信安装包"),
        // 微软更新 CDN：历史补丁文件永久保留
        new("https://download.windowsupdate.com/c/msdownload/update/software/secu/2023/10/windows10.0-kb5031354-x64_9854b36fa7b9e1cd66e942d5c9e51cfca36b0b71.msu", "微软更新 CDN"),
        // 教育网镜像：centos-vault 为冻结归档，文件名永久不变（约 10GB，仅 Range 读取前段）
        new("https://mirrors.tuna.tsinghua.edu.cn/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "清华 TUNA 镜像"),
        new("https://mirrors.ustc.edu.cn/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "中科大镜像"),
        new("https://dldir1.qq.com/qqfile/qq/PCQQ9.7.17/QQ9.7.17.29225.exe", "腾讯 QQ 安装包"),
        new("https://mirrors.aliyun.com/centos-vault/7.9.2009/isos/x86_64/CentOS-7-x86_64-Everything-2009.iso", "阿里云镜像")
    };

    private const int ProbeTimeoutMs = 6000;

    /// <summary>
    /// 执行下载测速。
    /// </summary>
    /// <param name="customUrl">自定义端点（留空用内置候选；自定义端点排最前）。</param>
    /// <param name="budgetSeconds">每端点下载限时（3-30 秒）。</param>
    /// <param name="connections">并发连接数（1-8）。</param>
    /// <param name="maxBytes">单端点下载总量上限（字节）。</param>
    /// <param name="progress">实时进度（约 400ms 一拍）。</param>
    public static async Task<SpeedTestResult> MeasureAsync(
        string? customUrl, int budgetSeconds, int connections, long maxBytes,
        IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        budgetSeconds = Math.Clamp(budgetSeconds, 3, 30);
        connections = Math.Clamp(connections, 1, 8);
        if (maxBytes < (8L << 20)) maxBytes = 8L << 20; // 下限 8MB，避免过早上限导致误判

        var candidates = new List<Candidate>();
        if (!string.IsNullOrWhiteSpace(customUrl) && Uri.TryCreate(customUrl, UriKind.Absolute, out var cu)
            && (cu.Scheme == Uri.UriSchemeHttp || cu.Scheme == Uri.UriSchemeHttps))
            candidates.Add(new Candidate(customUrl, "自定义测速节点"));
        candidates.AddRange(DefaultCandidates);

        // ---- 阶段 1：探测可达端点 ----
        progress?.Report(new SpeedProgress { Phase = "正在探测测速节点…" });
        var alive = new List<(Candidate C, bool Range, long Size)>();
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(c.Url).ConfigureAwait(false);
            if (probe.Ok)
            {
                alive.Add((c, probe.SupportsRange, probe.Size));
                if (alive.Count >= 2) break;
            }
        }

        var result = new SpeedTestResult();
        if (alive.Count == 0)
        {
            result.Error = "所有测速节点均不可达（可能被校园网白名单拦截、出口拥塞或当前无外网）";
            return result;
        }

        // ---- 阶段 2：逐端点多连接限时下载 ----
        long bytesAll = 0;
        var swAll = Stopwatch.StartNew();
        double peak = 0;
        foreach (var (c, range, size) in alive)
        {
            ct.ThrowIfCancellationRequested();
            var r = await MeasureEndpointAsync(c, range, size, budgetSeconds, connections, maxBytes, progress, ct)
                .ConfigureAwait(false);
            result.Endpoints.Add(r);
            if (r.Success)
            {
                bytesAll += r.BytesTotal;
                peak = Math.Max(peak, r.MbpsPeak);
                if (r.MbpsAvg > result.MbpsAvg)
                {
                    result.MbpsAvg = r.MbpsAvg;
                    result.EndpointLabel = r.Label;
                }
            }
        }
        swAll.Stop();

        if (result.MbpsAvg <= 0)
        {
            result.Error = "测速节点可达但未测得有效数据（连接被重置或限速接近于 0），请结合延迟画像判断";
            return result;
        }

        result.Success = true;
        result.MbpsPeak = peak;
        result.BytesTotal = bytesAll;
        result.DurationMs = (int)swAll.ElapsedMilliseconds;
        return result;
    }

    // ---------------- 阶段 1：端点探测 ----------------

    /// <summary>GET Range 0-65535 探测：可达性 + 是否支持断点 + 文件总大小。</summary>
    private static async Task<(bool Ok, bool SupportsRange, long Size)> ProbeAsync(string url)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 65535);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, false, 0);
            // 206 时 ContentLength 是本段大小；文件总大小在 Content-Range 的 “/总长” 中
            long size = resp.Content.Headers.ContentRange?.Length
                        ?? resp.Content.Headers.ContentLength ?? 0;
            bool range = resp.StatusCode == HttpStatusCode.PartialContent;
            return (true, range, size);
        }
        catch { return (false, false, 0); }
    }

    // ---------------- 阶段 2：单端点多连接限时下载 ----------------

    private static async Task<SpeedEndpointResult> MeasureEndpointAsync(
        Candidate c, bool supportsRange, long fileSize, int budgetSeconds, int connections,
        long maxBytes, IProgress<SpeedProgress>? progress, CancellationToken ct)
    {
        var r = new SpeedEndpointResult { Label = c.Label, Url = c.Url, Connections = connections };
        var sw = Stopwatch.StartNew();
        try
        {
            // 不支持 Range 的大文件改单连接；文件太小时避免多连接 416
            if (!supportsRange || fileSize <= 0) connections = 1;
            else if (fileSize < connections * (1L << 20)) connections = 1;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));

            long total = 0;
            long chunk = 32L << 20; // 每连接取 32MB 段，够 8 秒×百兆出口
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false });
            client.Timeout = Timeout.InfiniteTimeSpan;

            var tasks = Enumerable.Range(0, connections).Select(i => Task.Run(async () =>
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, c.Url);
                    if (supportsRange && connections > 1)
                        req.Headers.Range = new RangeHeaderValue(i * chunk, i * chunk + chunk - 1);
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                        .ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return;
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buf = new byte[64 * 1024];
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        Interlocked.Add(ref total, n);
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
                catch { /* 单连接失败不影响整体统计 */ }
            })).ToArray();

            // 采样循环：进度上报 + 峰值统计；预算耗尽/总量达上限/全部完成即退出
            long lastBytes = 0;
            long lastMs = 0;
            double peak = 0;
            while (true)
            {
                if (tasks.All(t => t.IsCompleted)) break;
                long b = Interlocked.Read(ref total);
                if (b >= maxBytes) { cts.Cancel(); break; }
                try { await Task.Delay(400, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long now = sw.ElapsedMilliseconds;
                long cur = Interlocked.Read(ref total);
                if (now - lastMs >= 300)
                {
                    double inst = (cur - lastBytes) * 8.0 / (now - lastMs) / 1000.0; // bytes*8/(ms*1000)=Mbps
                    if (inst > peak) peak = inst;
                    double avg = cur * 8.0 / Math.Max(1, now) / 1000.0;
                    progress?.Report(new SpeedProgress
                    {
                        Phase = $"正在测速：{c.Label}",
                        InstantMbps = inst,
                        AvgMbps = avg,
                        BytesRead = cur,
                        ElapsedMs = (int)now,
                        Connections = connections
                    });
                    lastBytes = cur;
                    lastMs = now;
                }
            }

            cts.Cancel(); // 无论如何终止残余连接
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* 已吞掉 */ }

            sw.Stop();
            long bytes = Interlocked.Read(ref total);
            r.BytesTotal = bytes;
            r.DurationMs = (int)sw.ElapsedMilliseconds;
            r.MbpsAvg = bytes * 8.0 / Math.Max(1, sw.ElapsedMilliseconds) / 1000.0;
            r.MbpsPeak = peak;
            r.Success = bytes >= (128L << 10) && sw.ElapsedMilliseconds >= 200; // 至少 128KB 才可信
            if (!r.Success)
                r.Error = $"仅下载 {FormatHelper.Bytes(bytes)}，样本不足";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            r.Error = ex.Message;
        }
        return r;
    }
}
