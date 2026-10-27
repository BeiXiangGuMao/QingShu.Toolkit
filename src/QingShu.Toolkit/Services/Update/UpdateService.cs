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
/// 自动更新服务（GitHub Releases，Beta / LTS 双通道）：
///  · 通道：Stable 只接收正式发布；Beta 跟踪一切发布（含预发布）；LTS 只接收 vX.Y.Z-lts 维护版
///  · 加速：内置多条 GitHub 加速线路，检查与下载自动优选并记忆最近可用线路，失败逐条回退、直连兜底；
///          API 全灭时降级 releases.atom 通道解析（保证"能发现新版本"）
///  · 下载：断点续传（Range）+ 读停滞看门狗（90s 无数据自动换线）+ 实时速度显示
///  · 询问式：发现新版本 → 弹窗询问 → 下载（进度可见）→ 再次询问 → 退出并安装，全程不静默
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const int ApiTimeoutSeconds = 8;        // 检查阶段单线路超时
    private const int AtomTimeoutSeconds = 8;       // Atom 降级单线路超时
    private const int DownloadIdleSeconds = 90;     // 下载读停滞看门狗

    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private readonly HttpClient _http;

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
        try
        {
            var repo = _settings.Current.UpdateRepo;
            var channel = UpdateChannelHelper.Parse(_settings.Current.UpdateChannel);
            var channelText = channel switch
            {
                UpdateChannel.Beta => "Beta 通道",
                UpdateChannel.Lts => "LTS 通道",
                _ => "稳定版通道"
            };

            // 1) releases 列表（多线路 failover）
            var fetched = await TryFetchReleasesAsync(repo).ConfigureAwait(false);
            if (fetched.HasValue)
            {
                await HandleReleaseListAsync(fetched.Value.Releases, fetched.Value.Via, channel, channelText, manual).ConfigureAwait(false);
                return;
            }

            // 2) API 全灭 → releases.atom 降级解析（只能拿到 tag，无资产信息）
            var atomTag = await TryFetchLatestTagViaAtomAsync(repo, channel).ConfigureAwait(false);
            if (atomTag is null)
            {
                Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = manual
                    ? "检查失败：加速与直连线路均无法访问 GitHub，请检查校园网或稍后重试。"
                    : "自动检查更新失败（所有线路不可达，将稍后重试）。" });
                return;
            }
            _log.App(LogLevel.Warn, "update", $"API 不可达，已降级经 Atom 解析到 {atomTag}（{channelText}）");
            _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
            await _settings.SaveAsync().ConfigureAwait(false);
            HandleAtomTag(atomTag, repo, manual);
        }
        catch (Exception ex)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = manual ? $"检查失败：{ex.Message}" : "自动检查更新失败（将稍后重试）。" });
        }
    }

    private async Task<(JsonElement Releases, GitHubMirror Via)?> TryFetchReleasesAsync(string repo)
    {
        var chain = GitHubAccess.BuildApiChain(MemoryId, _settings.Current.AccelerateGitHub, repo);
        foreach (var (mirror, url) in chain)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ApiTimeoutSeconds));
                using var resp = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.App(LogLevel.Info, "update", $"检查线路不可用（{mirror.Name}）：HTTP {(int)resp.StatusCode}");
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, cts.Token).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    _log.App(LogLevel.Info, "update", $"检查线路返回异常（{mirror.Name}）：非 release 列表");
                    continue;
                }
                _log.App(LogLevel.Info, "update", $"更新检查经线路 {mirror.Name} 成功");
                return (doc.RootElement.Clone(), mirror);
            }
            catch (OperationCanceledException)
            {
                _log.App(LogLevel.Info, "update", $"检查线路超时（{mirror.Name}，{ApiTimeoutSeconds}s）");
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Info, "update", $"检查线路不可用（{mirror.Name}）：{ex.Message}");
            }
        }
        return null;
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
            string hint = channel switch
            {
                UpdateChannel.Lts => "（LTS 通道：仓库暂无形如 vX.Y.Z-lts 的长期支持版本）",
                UpdateChannel.Beta => "（Beta 通道：仓库尚无任何发布）",
                _ => "（稳定版通道：仓库暂无正式发布）"
            };
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

    private async Task<string?> TryFetchLatestTagViaAtomAsync(string repo, UpdateChannel channel)
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
                    _log.App(LogLevel.Info, "update", $"Atom 降级线路不可用（{mirror.Name}）：HTTP {(int)resp.StatusCode}");
                    continue;
                }
                var xml = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.Name.Namespace;
                var entry = doc.Descendants(ns + "entry").FirstOrDefault();
                // 真实结构（实测）：link href = .../releases/tag/{tag}；id = tag:github.com,2008:Repository/{rid}/{tag}
                var href = entry?.Element(ns + "link")?.Attribute("href")?.Value ?? "";
                var id = entry?.Element(ns + "id")?.Value ?? "";
                string tag = "";
                if (href.Contains("/releases/tag/", StringComparison.Ordinal))
                    tag = href.Split("/releases/tag/")[^1].Trim();
                else if (id.Length > 0)
                {
                    var tail = id.Split('/')[^1].Trim();
                    if (tail.Length > 0 && !tail.All(char.IsDigit)) tag = tail;
                }
                if (tag.Length == 0) continue;

                var key = GitHubAccess.ParseVersionKey(tag);
                if (!GitHubAccess.ChannelMatchesByKey(channel, key)) continue;
                _log.App(LogLevel.Info, "update", $"Atom 降级经线路 {mirror.Name} 解析到 {tag}");
                return tag;
            }
            catch (Exception ex)
            {
                _log.App(LogLevel.Info, "update", $"Atom 降级线路不可用（{mirror.Name}）：{ex.Message}");
            }
        }
        return null;
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
            // 范围越界 → 本地已完整
            Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100,
                DownloadedFile = target, Message = "下载完成，准备安装。" });
            return;
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
            throw new IOException($"下载不完整（{finalLen}/{total} 字节）");

        Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100,
            DownloadedFile = target, Message = $"下载完成，准备安装。（{FormatSpeed(speedBps)} 结束）" });
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
            }), System.Text.Encoding.UTF8);

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
