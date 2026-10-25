using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>下载新版本压缩包（带进度）。</summary>
    Task DownloadAsync(ReleaseInfo release);
    /// <summary>退出本程序 → 解压覆盖 → 重启（由 update.cmd 完成）。</summary>
    void Install(string downloadedFile);
    /// <summary>启动时自动检查（询问式，间隔由配置控制）。</summary>
    Task AutoCheckOnStartupAsync();
}

/// <summary>
/// 自动更新服务（GitHub Releases）：
///  · 询问式：发现新版本 → 弹窗询问 → 下载（进度可见）→ 再次询问 → 退出并安装
///  · 全程不静默安装，所有关键节点都需要用户确认
/// </summary>
public sealed class UpdateService : IUpdateService
{
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

    public async Task AutoCheckOnStartupAsync()
    {
        var s = _settings.Current;
        if (!s.AutoCheckUpdate) return;
        if (s.LastUpdateCheckUtc.HasValue && DateTime.UtcNow - s.LastUpdateCheckUtc.Value < TimeSpan.FromHours(Math.Max(1, s.UpdateCheckIntervalHours)))
            return;
        await Task.Delay(8000).ConfigureAwait(false); // 等主窗口稳定
        await CheckAsync(manual: false).ConfigureAwait(false);
    }

    public async Task CheckAsync(bool manual)
    {
        Set(new UpdateStatus { Phase = UpdatePhase.Checking, Message = "正在检查更新…" });
        try
        {
            var repo = _settings.Current.UpdateRepo;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await _http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = $"检查失败（HTTP {(int)resp.StatusCode}）。校园网络可能无法访问 GitHub。" });
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = doc.RootElement;

            var release = new ReleaseInfo
            {
                Tag = root.GetProperty("tag_name").GetString() ?? "",
                Notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                HtmlUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                PublishedAt = root.TryGetProperty("published_at", out var pub) && pub.TryGetDateTime(out var dt) ? dt : DateTime.MinValue,
                IsPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()
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

            _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
            await _settings.SaveAsync().ConfigureAwait(false);

            if (!System.Version.TryParse(release.VersionText.Split('-')[0], out var remote) )
            {
                Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = "已是最新版本。" });
                return;
            }
            var local = System.Version.Parse(AppVersion.Text);
            var comparison = remote.CompareTo(local);
            if (comparison <= 0 || release.VersionText == _settings.Current.SkipVersion)
            {
                Set(new UpdateStatus { Phase = UpdatePhase.UpToDate, Message = manual ? "已是最新版本。" : "" });
                _log.App(LogLevel.Info, "update", $"检查更新：本地 v{local}，远端 {release.Tag}，无需更新");
                return;
            }

            if (string.IsNullOrEmpty(release.AssetUrl))
            {
                Set(new UpdateStatus { Phase = UpdatePhase.Available, Release = release,
                    Message = $"发现新版本 v{release.VersionText}，但该版本未提供压缩包资产，请到发布页手动下载。" });
                return;
            }
            Set(new UpdateStatus { Phase = UpdatePhase.Available, Release = release, Message = $"发现新版本 v{release.VersionText}" });
            _log.App(LogLevel.Info, "update", $"发现新版本 {release.Tag}（本地 v{local}）");
        }
        catch (Exception ex)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = manual ? $"检查失败：{ex.Message}" : "自动检查更新失败（将稍后重试）。" });
        }
    }

    public async Task DownloadAsync(ReleaseInfo release)
    {
        try
        {
            Directory.CreateDirectory(DownloadDir);
            var target = Path.Combine(DownloadDir, $"QingShu.Toolkit-{release.VersionText}.zip");
            Set(new UpdateStatus { Phase = UpdatePhase.Downloading, Release = release, ProgressPercent = 0, Message = "正在下载更新…" });

            using var resp = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? release.AssetSize;
            await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            int lastPct = -1;
            while ((read = await src.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                {
                    int pct = (int)(copied * 100 / total);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        Set(new UpdateStatus { Phase = UpdatePhase.Downloading, Release = release, ProgressPercent = pct, Message = $"正在下载更新… {pct}%" });
                    }
                }
            }
            Set(new UpdateStatus { Phase = UpdatePhase.ReadyToInstall, Release = release, ProgressPercent = 100, DownloadedFile = target, Message = "下载完成，准备安装。" });
            _log.App(LogLevel.Info, "update", $"新版本压缩包已下载：{target}");
        }
        catch (Exception ex)
        {
            Set(new UpdateStatus { Phase = UpdatePhase.Error, Message = $"下载失败：{ex.Message}" });
        }
    }

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
        Current = s;
        StatusChanged?.Invoke(s);
    }
}
