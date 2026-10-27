using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.Services.Update;

public enum UpdatePhase { Idle, Checking, Available, UpToDate, Downloading, ReadyToInstall, Error }

public sealed class UpdateStatus
{
    public UpdatePhase Phase { get; init; }
    public ReleaseInfo? Release { get; init; }
    public string Message { get; init; } = "";
    public double ProgressPercent { get; init; }
    public string DownloadedFile { get; init; } = "";
}

public interface IUpdateService
{
    event Action<UpdateStatus>? StatusChanged;
    UpdateStatus Current { get; }
    Task CheckAsync(bool manual);
    /// <summary>下载新版本压缩包（带进度/速度，多线路自动切换 + 断点续传）。</summary>
    Task DownloadAsync(ReleaseInfo release);
    /// <summary>退出本程序 → 解压覆盖 → 重启（由 update.cmd 完成）。</summary>
    void Install(string downloadedFile);
    /// <summary>启动时自动检查（询问式，间隔由配置控制）。</summary>
    Task AutoCheckOnStartupAsync();
}

/// <summary>
/// 自动更新服务（GitHub Releases，稳定版 / Beta 双通道）。
/// 版本只分稳定版与 Beta 两档：Stable 只接收正式发布；Beta 跟踪一切发布（含预发布）。
///  · 加速：内置多条 GitHub 加速线路，检查与下载自动优选并记忆最近可用线路，失败逐条回退、直连兜底；
///          API 全灭时降级 releases.atom 通道解析（保证"能发现新版本"）
///  · 状态精确区分：无发布（空列表短路，提示先建 Release）/ 仓库 404（地址错误）/ 网络不可达
///  · 下载：断点续传（Range）+ 读停滞看门狗（90s 无数据自动换线）+ 实时速度显示
///  · 询问式：发现新版本 → 弹窗询问 → 下载（进度可见）→ 再次询问 → 退出并安装，全程不静默
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const int ApiTimeoutSeconds = 8;        // 检查阶段单线路超时
    private const int AtomTimeoutSeconds = 8;       // Atom 降级单线路超时
    private const int DownloadIdleSeconds = 90;     // 下载读停滞看门狗

    /// <summary>检查探测结果：Found=有发布 / Empty=仓库可达但无发布（短路）/ Unreachable=线路全灭。</summary>
    private enum ReleasesProbe { Found, Empty, Unreachable }
    private enum AtomProbe { TagFound, Empty, ChannelMismatch, Unreachable }

    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private bool _saw404; // 本轮检查是否出现过 404（仓库地址错误/转私有的关键线索）

    public event Action<UpdateStatus>? StatusChanged;
    public UpdateStatus Current { get; private set; } = new() { Phase = UpdatePhase.Idle };

    public UpdateService(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QingShu.Toolkit", AppVersion.Text));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private static string DownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingShu.Toolkit", "update");

    // ---- 线路记忆：会话内字段优先，持久化到设置（下次启动沿用最近可用线路） ----
    private GitHubMirror? _lastMirror;
    private string MemoryId => _lastMirror?.Id ?? _settings.Current.GitHubMirrorId ?? "auto";

    private async Task RememberMirrorAsync(GitHubMirror m)
    {
        _lastMirror = m;
        if (GitHubAccess.IsDirect(m)) return; // 直连成功不覆盖记忆（保留最近一次加速线路以备下次优先）
        if (_settings.Current.GitHubMirrorId != m.Id)
        {
            _settings.Current.GitHubMirrorId = m.Id;
            await _settings.SaveAsync().ConfigureAwait(false);
        }
    }

    public async Task AutoCheckOnStartupAsync()
    {
        var s = _settings.Current;
        if (!s.AutoCheckUpdate) return;
        if (s.LastUpdateCheckUtc.HasValue && DateTime.UtcNow - s.LastUpdateCheckUtc.Value < TimeSpan.FromHours(Math.Max(1, s.UpdateCheckIntervalHours)))
            return;
        await Task.Delay(8000).ConfigureAwait(false); // 等主窗口稳定
        await CheckAsync(manual: false).ConfigureAwait(false);
    }

    // ================= 检查 =================

    public async Task CheckAsync(bool manual)
    {
        Set(new UpdateStatus { Phase = UpdatePhase.Checking, Message = "正在检查更新…" });
        _saw404 = false;
        try
        {
            var repo = _settings.Current.UpdateRepo;
            var channel = UpdateChannelHelper.Parse(_settings.Current.UpdateChannel);
            var channelText = channel == UpdateChannel.Beta ? "Beta 通道" : "稳定版通道";

            // 1) releases 列表（多线路 failover；200+空列表=仓库可达但无发布，立即短路）
            var (probe, list, via) = await TryFetchReleasesAsync(repo).ConfigureAwait(false);
            if (probe == ReleasesProbe.Found)
            {
                await HandleReleaseListAsync(list, via!, channel, channelText, manual).ConfigureAwait(false);
                return;
            }
            if (probe == ReleasesProbe.Empty)
            {
                _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                await _settings.SaveAsync().ConfigureAwait(false);
                Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual
                    ? "仓库可达，但尚无任何发布版本：请先在 GitHub 创建 Release（附 zip 资产）。可运行仓库内 scripts/make_release.ps1 一键发布。"
                    : "" });
                _log.App(LogLevel.Warn, "update", "仓库可达但没有任何 Release，更新检查空转");
                return;
            }

            // 2) API 全灭 → releases.atom 降级解析（只能拿到 tag，无资产信息）
            var (atomProbe, atomTag) = await TryFetchLatestTagViaAtomAsync(repo, channel).ConfigureAwait(false);
            switch (atomProbe)
            {
                case AtomProbe.TagFound:
                    _log.App(LogLevel.Warn, "update", $"API 不可达，已降级经 Atom 解析到 {atomTag}（{channelText}）");
                    _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                    await _settings.SaveAsync().ConfigureAwait(false);
                    HandleAtomTag(atomTag, repo, manual);
                    return;
                case AtomProbe.Empty:
                    _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                    await _settings.SaveAsync().ConfigureAwait(false);
                    Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual
                        ? "仓库可达，但尚无任何发布版本：请先在 GitHub 创建 Release（附 zip 资产）。可运行仓库内 scripts/make_release.ps1 一键发布。"
                        : "" });
                    return;
                case AtomProbe.ChannelMismatch:
                    _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
                    await _settings.SaveAsync().ConfigureAwait(false);
                    Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual
                        ? $"仓库有发布，但{channelText}暂无匹配版本（仓库仅有预发布版本时，稳定版通道不可见）。"
                        : "" });
                    return;
            }

            // 3) 所有线路均不可达（含 Atom 降级）
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = _saw404
                ? "检查失败：仓库返回 404。请检查设置中的 GitHub 仓库地址是否正确（仓库可能已改名或转私有）。"
                : (manual
                    ? "检查失败：加速与直连线路均无法访问 GitHub，请检查网络或稍后重试。"
                    : "自动检查更新失败（所有线路不可达，将稍后重试）。") });
        }
        catch (Exception ex)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = manual ? $"检查失败：{ex.Message}" : "自动检查更新失败（将稍后重试）。" });
        }
    }

    private async Task<(ReleasesProbe Probe, JsonElement List, GitHubMirror? Via)> TryFetchReleasesAsync(string repo)
    {
        var chain = GitHubAccess.BuildApiChain(MemoryId, _settings.Current.AccelerateGitHub, repo);
        foreach (var (mirror, url) in chain)
        {
            var (probe, list, via) = await FetchReleasesViaAsync(mirror, repo).ConfigureAwait(false);
            if (probe == ReleasesProbe.Found) return (probe, list, via);
            if (probe == ReleasesProbe.Empty)
            {
                // 镜像 200+空数组可能来自代理缓存/异常响应：直连复核一次防误报“无发布”，
                // 直连拿到数据则改用直连结果（直连仍空或不可达 → 维持空结论）
                if (!GitHubAccess.IsDirect(mirror) && _settings.Current.AccelerateGitHub)
                {
                    _log.App(LogLevel.Info, "update", $"线路 {mirror.Name} 报告空列表，直连复核中…");
                    var (p2, l2, v2) = await FetchReleasesViaAsync(GitHubAccess.Direct, repo).ConfigureAwait(false);
                    if (p2 == ReleasesProbe.Found) return (p2, l2, v2);
                }
                return (ReleasesProbe.Empty, default, mirror);
            }
            // Unreachable → 尝试下一线路
        }
        return (ReleasesProbe.Unreachable, default, null);
    }

    /// <summary>单线路 releases 列表探测：成功返回 Found+列表；200+空数组返回 Empty；其余 Unreachable。</summary>
    private async Task<(ReleasesProbe Probe, JsonElement List, GitHubMirror? Via)> FetchReleasesViaAsync(GitHubMirror mirror, string repo)
    {
        try
        {
            var url = $"{mirror.Prefix}https://api.github.com/repos/{repo}/releases?per_page=15";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
            using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404) _saw404 = true;
                _log.App(LogLevel.Info, "update", $"检查线路不可用（{mirror.Name}）：HTTP {(int)resp.StatusCode}");
                return (ReleasesProbe.Unreachable, default, mirror);
            }
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, cts.Token).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                _log.App(LogLevel.Info, "update", $"检查线路返回异常（{mirror.Name}）：非 release 列表");
                return (ReleasesProbe.Unreachable, default, mirror);
            }
            if (doc.RootElement.GetArrayLength() == 0)
            {
                // 200 + 空数组 = 线路确认仓库无发布（外层会按需直连复核后短路）
                _log.App(LogLevel.Info, "update", $"检查经线路 {mirror.Name} 确认：仓库尚无任何 Release");
                return (ReleasesProbe.Empty, default, mirror);
            }
            _log.App(LogLevel.Info, "update", $"更新检查经线路 {mirror.Name} 成功");
            return (ReleasesProbe.Found, doc.RootElement.Clone(), mirror);
        }
        catch (OperationCanceledException)
        {
            _log.App(LogLevel.Info, "update", $"检查线路超时（{mirror.Name}，{ApiTimeoutSeconds}s）");
        }
        catch (Exception ex)
        {
            _log.App(LogLevel.Info, "update", $"检查线路不可用（{mirror.Name}）：{ex.Message}");
        }
        return (ReleasesProbe.Unreachable, default, mirror);
    }

    private async Task HandleReleaseListAsync(JsonElement list, GitHubMirror via, UpdateChannel channel, string channelText, bool manual)
    {
        _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
        await _settings.SaveAsync().ConfigureAwait(false);

        // 按通道选目标：列表按创建时间倒序，取第一个匹配且非草稿的发布
        JsonElement? picked = null;
        foreach (var r in list.EnumerateArray())
        {
            bool pre = r.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean();
            bool draft = r.TryGetProperty("draft", out var dEl) && dEl.GetBoolean();
            if (draft) continue;
            var tag = r.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (tag.Length == 0) continue;
            if (GitHubAccess.ChannelMatches(channel, pre, tag)) { picked = r.Clone(); break; }
        }

        if (!picked.HasValue)
        {
            string hint = channel == UpdateChannel.Beta
                ? "（Beta 通道：仓库发布列表为空或仅剩草稿）"
                : "（稳定版通道：仓库仅有预发布版本，暂无正式发布）";
            Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual ? $"已是最新版本。{hint}" : "" });
            return;
        }

        var root = picked.Value;
        var release = new ReleaseInfo
        {
            Tag = root.GetProperty("tag_name").GetString() ?? "",
            Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            HtmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
            PublishedAt = root.TryGetProperty("published_at", out var pub) && pub.TryGetDateTime(out var dt) ? dt : DateTime.MinValue,
            IsPrerelease = root.TryGetProperty("prerelease", out var pre2) && pre2.GetBoolean()
        };

        // 选择压缩包资产（.zip 优先）
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                release.AssetName = name;
                release.AssetUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                release.AssetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                break;
            }
        }

        var localKey = GitHubAccess.LocalKey();
        var remoteKey = GitHubAccess.ParseVersionKey(release.Tag);
        if (remoteKey.CompareTo(localKey) <= 0 || release.Tag == _settings.Current.SkipVersion)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual ? $"已是最新版本（{channelText}）。" : "" });
            _log.App(LogLevel.Info, "update", $"检查更新：本地 v{localKey.Num}（{channelText}），远端 {release.Tag}，无需更新");
            return;
        }

        if (string.IsNullOrEmpty(release.AssetUrl))
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Available, Release = release,
                Message = $"发现新版本 v{release.VersionText}（{channelText}），但该版本未提供压缩包资产，请到发布页手动下载。" });
            return;
        }
        var viaText = GitHubAccess.IsDirect(via) ? "直连" : $"经 {via.Name} 加速";
        Set(new UpdateStatus { Phase = UpdatePhase.Available, Release = release,
            Message = $"发现新版本 v{release.VersionText}（{channelText}，{viaText}）" });
        _log.App(LogLevel.Info, "update", $"发现新版本 {release.Tag}（本地 v{localKey.Num}，{channelText}，线路 {via.Name}）");
    }

    // ---- Atom 降级 ----

    private async Task<(AtomProbe Probe, string Tag)> TryFetchLatestTagViaAtomAsync(string repo, UpdateChannel channel)
    {
        var chain = GitHubAccess.BuildAtomChain(_settings.Current.AccelerateGitHub, repo);
        foreach (var (mirror, url) in chain)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AtomTimeoutSeconds));
                using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 404) _saw404 = true;
                    _log.App(LogLevel.Info, "update", $"Atom 降级线路不可用（{mirror.Name}）：HTTP {(int)resp.StatusCode}");
                    continue;
                }
                var xml = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.Name.Namespace;
                // 真实结构（实测）：link href = .../releases/tag/{tag}；id = tag:github.com,2008:Repository/{rid}/{tag}
                string TagOf(XElement el)
                {
                    var href = el.Element(ns + "link")?.Attribute("href")?.Value ?? "";
                    if (href.Contains("/releases/tag/", StringComparison.Ordinal))
                        return href.Split("/releases/tag/")[^1].Trim();
                    var id = el.Element(ns + "id")?.Value ?? "";
                    var tail = id.Split('/')[^1].Trim();
                    return tail.Length > 0 && !tail.All(char.IsDigit) ? tail : "";
                }
                var entries = doc.Descendants(ns + "entry").ToList();
                if (entries.Count == 0)
                {
                    _log.App(LogLevel.Info, "update", $"Atom 经线路 {mirror.Name} 确认：仓库尚无任何 Release");
                    return (AtomProbe.Empty, "");
                }
                foreach (var entry in entries)
                {
                    var tag = TagOf(entry);
                    if (tag.Length == 0) continue;
                    var key = GitHubAccess.ParseVersionKey(tag);
                    if (!GitHubAccess.ChannelMatchesByKey(channel, key)) continue;
                    _log.App(LogLevel.Info, "update", $"Atom 降级经线路 {mirror.Name} 解析到 {tag}");
                    return (AtomProbe.TagFound, tag);
                }
                // feed 有内容但当前通道无匹配（稳定版通道 + 仓库仅有预发布）
                return (AtomProbe.ChannelMismatch, "");
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Info, "update", $"Atom 降级线路不可用（{mirror.Name}）：{ex.Message}");
            }
        }
        return (AtomProbe.Unreachable, "");
    }

    private void HandleAtomTag(string tag, string repo, bool manual)
    {
        var localKey = GitHubAccess.LocalKey();
        var remoteKey = GitHubAccess.ParseVersionKey(tag);
        if (remoteKey.CompareTo(localKey) <= 0 || tag == _settings.Current.SkipVersion)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual ? "已是最新版本。" : "" });
            return;
        }
        var release = new ReleaseInfo
        {
            Tag = tag,
            HtmlUrl = $"https://github.com/{repo}/releases/tag/{tag}"
        };
        Set(new UpdateStatus { Phase = UpdatePhase.Available, Release = release,
            Message = $"发现新版本 v{tag.TrimStart('v')}（降级线路检测）。加速线路受限，请到发布页手动下载更新包。" });
    }

    // ================= 下载 =================

    public async Task DownloadAsync(ReleaseInfo release)
    {
        if (string.IsNullOrEmpty(release.AssetUrl))
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = "该版本未提供压缩包资产，请到发布页手动下载。" });
            return;
        }
        try
        {
            Directory.CreateDirectory(DownloadDir);
            var target = Path.Combine(DownloadDir, $"QingShu.Toolkit-{release.VersionText}.zip");
            Set(new UpdateStatus { Phase = UpdatePhase.Downloading, Release = release, ProgressPercent = 0, Message = "正在下载更新…" });

            var chain = GitHubAccess.BuildDownloadChain(MemoryId, _settings.Current.AccelerateGitHub, release.AssetUrl);
            Exception? last = null;
            foreach (var (mirror, url) in chain)
            {
                try
                {
                    await DownloadViaAsync(url, mirror, target, release).ConfigureAwait(false);
                    await RememberMirrorAsync(mirror).ConfigureAwait(false);
                    _log.App(LogLevel.Info, "update", $"新版本压缩包下载完成：{target}（线路 {mirror.Name}）");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _log.App(LogLevel.Info, "update", $"下载线路失败（{mirror.Name}）：{ex.Message}，切换下一线路…");
                }
            }
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = $"下载失败：{last?.Message}。已尝试全部线路，可稍后重试（已下载部分保留、自动续传），或到发布页手动下载。" });
        }
        catch (Exception ex)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = $"下载失败：{ex.Message}" });
        }
    }

    private async Task DownloadViaAsync(string url, GitHubMirror mirror, string target, ReleaseInfo release)
    {
        // 断点续传：已有部分文件 → Range 请求续传；服务器不支持（200）则从头重写
        long existing = 0;
        if (File.Exists(target))
        {
            existing = new FileInfo(target).Length;
            if (release.AssetSize > 0 && existing > release.AssetSize)
            {
                File.Delete(target); // 异常残留文件
                existing = 0;
            }
            else if (release.AssetSize > 0 && existing == release.AssetSize)
            {
                EnsureZipFile(target, release); // 长度符合仍需校验内容，防历史异常残留
                Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100,
                    DownloadedFile = target, Message = "下载完成，准备安装。" });
                return;
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        if ((int)resp.StatusCode == 416 && existing > 0)
        {
            // 范围越界：仅当本地长度已达到期望全长才视为完整；
            // 镜像文件滞后（服务器文件比 API 报的 size 小）时 416 不代表下载完成，换线路重来
            if (release.AssetSize <= 0 || existing >= release.AssetSize)
            {
                EnsureZipFile(target, release);
                Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100,
                    DownloadedFile = target, Message = "下载完成，准备安装。" });
                return;
            }
            throw new IOException($"该线路文件长度({existing})小于期望({release.AssetSize})，数据可能滞后，需换线路");
        }
        resp.EnsureSuccessStatusCode();

        bool resumed = existing > 0 && (int)resp.StatusCode == 206;
        long done = resumed ? existing : 0;
        long total = 0;
        if (resp.Content.Headers.ContentRange?.Length is { } crLen) total = crLen;              // 206：全长
        else if (!resumed) total = resp.Content.Headers.ContentLength ?? release.AssetSize;      // 200：全长

        await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var dst = new FileStream(target, resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        int lastPct = -1;
        var sw = Stopwatch.StartNew();
        var lastSample = TimeSpan.Zero;
        long lastSampleBytes = done;
        double speedBps = 0;
        using var idleCts = new CancellationTokenSource(TimeSpan.FromSeconds(DownloadIdleSeconds));

        while ((read = await src.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false)) > 0)
        {
            idleCts.CancelAfter(TimeSpan.FromSeconds(DownloadIdleSeconds)); // 重置读停滞看门狗
            await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            copied += read;

            var el = sw.Elapsed;
            if ((el - lastSample).TotalMilliseconds >= 400)
            {
                var dt = (el - lastSample).TotalSeconds;
                if (dt > 0.1) speedBps = (done + copied - lastSampleBytes) / dt;
                lastSample = el;
                lastSampleBytes = done + copied;
            }
            if (total > 0)
            {
                int pct = (int)((done + copied) * 100 / total);
                if (pct != lastPct)
                {
                    lastPct = pct;
                    Set(new UpdateStatus { Phase = UpdatePhase.Downloading, Release = release, ProgressPercent = pct,
                        Message = $"正在下载更新… {pct}%（{FormatSpeed(speedBps)}）" });
                }
            }
            else if (lastPct != (int)(copied / (1024 * 1024))) // 总长未知：按 MiB 节流刷新速度
            {
                lastPct = (int)(copied / (1024 * 1024));
                Set(new UpdateStatus { Phase = UpdatePhase.Downloading, Release = release, ProgressPercent = 0,
                    Message = $"正在下载更新… 已下载 {copied / 1024 / 1024:F1} MB（{FormatSpeed(speedBps)}）" });
            }
        }

        long finalLen = done + copied;
        if (total > 0 && finalLen < total)
        {
            // 全量模式收到非 zip 数据（镜像错误页）：清理残留，避免下一线路把坏前缀续传进来
            if (!resumed && !IsZipHead(target))
            {
                try { File.Delete(target); } catch { }
            }
            throw new IOException($"下载不完整（{finalLen}/{total} 字节）");
        }

        EnsureZipFile(target, release); // 完整性终检：防故障镜像返回 200+错误页被当作更新包
        Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100,
            DownloadedFile = target, Message = $"下载完成，准备安装。（{FormatSpeed(speedBps)} 结束）" });
    }

    /// <summary>
    /// zip 内容终检：文件头必须是 "PK"。故障镜像可能返回 200 + HTML 错误页（HTTP 层成功、
    /// 长度也可能与流结束一致），不作校验会一路走到 Expand-Archive 才失败。
    /// 校验失败删除残留文件并抛异常 → 上层自动切换下一线路。
    /// </summary>
    private static void EnsureZipFile(string file, ReleaseInfo release)
    {
        if (IsZipHead(file)) return;
        try { File.Delete(file); } catch { }
        throw new IOException($"下载内容校验失败（非 zip 数据，线路 {release.Tag} 可能返回了错误页），已清理残留文件");
    }

    /// <summary>文件头是否为 "PK"（任意 zip 变体均以 PK 开头）。</summary>
    private static bool IsZipHead(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[2];
            return fs.Read(buf, 0, 2) == 2 && buf[0] == 0x50 && buf[1] == 0x4B;
        }
        catch { return false; }
    }

    private static string FormatSpeed(double bps) => bps >= 1024 * 1024
        ? $"{bps / 1024 / 1024:F1} MB/s"
        : $"{Math.Max(0, bps / 1024):F0} KB/s";

    // ================= 安装 =================

    public void Install(string downloadedFile)
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var newDir = Path.Combine(DownloadDir, "app-new");
            var script = Path.Combine(DownloadDir, "update.cmd");
            var zip = downloadedFile.Replace("'", "''");
            File.WriteAllText(script, string.Join("\r\n", new[]
            {
                "@echo off",
                "chcp 65001 >nul",
                "echo 青枢工具箱正在更新，请勿关闭此窗口...",
                "timeout /t 2 /nobreak >nul",
                $"rmdir /s /q \"{newDir}\" 2>nul",
                $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -Force -LiteralPath '{zip}' -DestinationPath '{newDir}'\"",
                $"robocopy \"{newDir}\" \"{appDir}\" /e /is /it /njh /njs /ndl /nc /ns >nul",
                "if errorlevel 8 (",
                "  echo 更新失败，新版本已保存在下载目录，请手动更新。",
                "  pause",
                "  exit /b 1",
                ")",
                $"rmdir /s /q \"{newDir}\"",
                $"del /f /q \"{downloadedFile}\"",
                $"start \"\" \"{Path.Combine(appDir, "QingShu.Toolkit.exe")}\"",
                "exit /b 0"
            }), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // 无 BOM：cmd 对带 BOM 批处理首行 @echo 会报错（Task 19 同款教训）

            _log.App(LogLevel.Info, "update", "启动更新脚本并退出主程序");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Minimized
            });
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            _log.App(LogLevel.Error, "update", $"启动更新脚本失败：{ex.Message}");
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = $"安装失败：{ex.Message}" });
        }
    }

    private void Set(UpdateStatus s)
    {
        // 后台 Http 全程 ConfigureAwait(false)：状态事件必须在源侧调度回 UI 线程
        UiDispatcher.Run(() =>
        {
            Current = s;
            StatusChanged?.Invoke(s);
        });
    }
}
