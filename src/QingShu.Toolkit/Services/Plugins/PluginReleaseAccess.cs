using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Update;

namespace QingShu.Toolkit.Services.Plugins;

/// <summary>
/// 插件的仓库 Release 只读访问实现。
/// 复用更新模块的 GitHubAccess：多线路 API 链 / 下载链、Token 治理
/// （Token 仅随直连请求附带，绝不发给第三方加速镜像；空 Token 走匿名，公库足够）。
/// 仓库地址取自设置 UpdateRepo，官方改仓库无需改插件。
/// 下载目录：%LOCALAPPDATA%\QingShu.Toolkit\plugin-downloads\&lt;tag&gt;\（宿主统一管理）。
/// </summary>
public sealed class PluginReleaseAccess : IPluginReleaseAccess
{
    private readonly ISettingsService _settings;
    private readonly ILogService _log;

    /// <summary>API 请求 20s 超时（链上逐条切换）；下载整体依赖链路看门狗与用户取消。</summary>
    private static readonly HttpClient Http = CreateHttp();
    private const int ApiTimeoutSeconds = 20;
    private const int MaxListLimit = 30;

    public PluginReleaseAccess(ISettingsService settings, ILogService log)
    {
        _settings = settings;
        _log = log;
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("QingShu.Toolkit-PluginHost/" + AppVersion.Text);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private string Repo => string.IsNullOrWhiteSpace(_settings.Current.UpdateRepo)
        ? "BeiXiangGuMao/QingShu.Toolkit"
        : _settings.Current.UpdateRepo.Trim();
    private string Token => GitHubAccess.SanitizeToken(_settings.Current.GitHubToken);
    private string MemoryId => _settings.Current.GitHubMirrorId;
    private bool Accelerate => _settings.Current.AccelerateGitHub;

    private void Log(string level, string message)
    {
        var lv = level switch
        {
            "warn" or "warning" => LogLevel.Warn,
            "error" => LogLevel.Error,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Info
        };
        _log.App(lv, "plugins-release", message);
    }

    // ================= Release 列表 =================

    public async Task<IReadOnlyList<PluginReleaseInfo>> ListAsync(int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit <= 0 ? 10 : limit, 1, MaxListLimit);
        string? lastError = null;
        foreach (var (mirror, url) in GitHubAccess.BuildApiChain(MemoryId, Accelerate, Repo, Token))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                GitHubAccess.ApplyAuth(req, mirror, Token);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(ApiTimeoutSeconds));
                using var resp = await Http.SendAsync(req, timeoutCts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode}（{mirror.Name}）";
                    continue;
                }
                await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
                var list = await ParseReleasesAsync(stream, limit).ConfigureAwait(false);
                Log("info", $"插件 Release 列表获取成功：{list.Count} 条（线路 {mirror.Name}，仓库 {Repo}）");
                return list;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"请求超时（{mirror.Name}）";
            }
            catch (OperationCanceledException) { throw; } // 用户取消：不吞
            catch (Exception ex)
            {
                lastError = $"{ex.Message}（{mirror.Name}）";
            }
        }
        var hint = Token.Length == 0
            ? "若仓库为私有，请先在「设置」填写 GitHub Token。"
            : "Token 已启用：请确认其对该仓库有 Contents 读取权限。";
        throw new InvalidOperationException($"获取 Release 失败：所有线路均不可用（{lastError}）。{hint}");
    }

    private static async Task<List<PluginReleaseInfo>> ParseReleasesAsync(Stream stream, int limit)
    {
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        var list = new List<PluginReleaseInfo>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (list.Count >= limit) break;
            string tag = GetString(el, "tag_name");
            if (tag.Length == 0) continue;
            var assets = new List<PluginReleaseAsset>();
            if (el.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = GetString(a, "name");
                    if (name.Length == 0) continue;
                    assets.Add(new PluginReleaseAsset(
                        name,
                        a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s) ? s : 0,
                        GetString(a, "browser_download_url")));
                }
            }
            DateTimeOffset? published = null;
            if (el.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(pa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var pdt))
            {
                published = pdt.ToLocalTime();
            }
            list.Add(new PluginReleaseInfo(
                tag,
                GetString(el, "name"),
                GetString(el, "body"),
                el.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                published,
                assets));
        }
        return list;
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ================= 资产下载 =================

    public async Task<string> DownloadAssetAsync(string tagName, string assetName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagName)) throw new InvalidOperationException("tagName 不能为空");
        if (string.IsNullOrWhiteSpace(assetName)) throw new InvalidOperationException("assetName 不能为空");
        var releases = await ListAsync(MaxListLimit, ct).ConfigureAwait(false);
        var rel = releases.FirstOrDefault(r => string.Equals(r.TagName, tagName.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"仓库最近 {releases.Count} 条发布中没有 {tagName}");
        var asset = rel.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"发布 {rel.TagName} 中没有资产 {assetName}（可用：{string.Join("、", rel.Assets.Select(a => a.Name))}）");

        var dir = Path.Combine(DownloadRoot(), MakeSafeSegment(rel.TagName));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(asset.Name));

        string? lastError = null;
        foreach (var (mirror, url) in GitHubAccess.BuildDownloadChain(MemoryId, Accelerate, asset.BrowserDownloadUrl))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                GitHubAccess.ApplyAuth(req, mirror, Token);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"HTTP {(int)resp.StatusCode}（{mirror.Name}）";
                    continue;
                }
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(target);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
                Log("info", $"插件资产下载完成：{asset.Name}（{mirror.Name}）→ {target}");
                return target;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = $"{ex.Message}（{mirror.Name}）";
                try { File.Delete(target); } catch { } // 残缺文件不留
            }
        }
        throw new InvalidOperationException($"下载 {asset.Name} 失败：所有线路均不可用（{lastError}）。");
    }

    private static string DownloadRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingShu.Toolkit", "plugin-downloads");
    }

    /// <summary>tag → 目录段：剔除路径非法字符，防穿越。</summary>
    private static string MakeSafeSegment(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        var s = sb.ToString();
        return s.Length == 0 ? "unknown" : s;
    }
}
