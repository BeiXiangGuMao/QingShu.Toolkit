using System;
using System.Collections.Generic;
using System.Linq;

namespace QingShu.Toolkit.Services.Update;

/// <summary>更新通道：Stable=正式发布 / Beta=抢先体验（含预发布）。</summary>
public enum UpdateChannel { Stable, Beta }

public static class UpdateChannelHelper
{
    public static UpdateChannel Parse(string? s) => s?.Trim().ToLowerInvariant() == "beta"
        ? UpdateChannel.Beta
        : UpdateChannel.Stable; // 旧版遗留 "lts" 值回退稳定版

    public static string ToSettingValue(this UpdateChannel ch) =>
        ch == UpdateChannel.Beta ? "beta" : "stable";
}

/// <summary>一条 GitHub 加速线路。Prefix 拼接规则：{Prefix}{原始完整 URL}。</summary>
public sealed record GitHubMirror(string Id, string Prefix, bool SupportsApi, bool SupportsDownload, string Name);

/// <summary>版本键：数字版本 + 发布级别（正式 &gt; LTS &gt; 预发布）+ 同级修订号（如 beta.2 的 2）。</summary>
public readonly record struct ReleaseVersionKey(Version Num, int Rank, int Rev) : IComparable<ReleaseVersionKey>
{
    public int CompareTo(ReleaseVersionKey other)
    {
        int c = Num.CompareTo(other.Num);
        if (c != 0) return c;
        c = Rank.CompareTo(other.Rank);
        return c != 0 ? c : Rev.CompareTo(other.Rev);
    }
}

/// <summary>
/// GitHub 访问基础设施：内置加速线路表、多线路 URL 链构造、Beta/LTS 通道语义与版本键解析。
/// 线路实测（2026-10）：gh-proxy.com / ghfast.top / ghproxy.net 存活；ghfast 与 ghproxy.net
/// 对境外 IP 返回 403（仅对国内开放），校园网场景反而可达；gh.llkk.cc / github.moeyy.xyz
/// 境外不可达（疑似国内线路），保留在下载链尾部。api.github.com 路径各镜像默认不代理，
/// API 链仅保留少量支持项 + 直连兜底，全灭时降级 releases.atom。
/// </summary>
public static class GitHubAccess
{
    public static readonly GitHubMirror Direct = new("direct", "", true, true, "直连 GitHub");

    /// <summary>内置线路（顺序即优选顺序，直连永远兜底在链尾）。</summary>
    public static readonly GitHubMirror[] Builtin =
    {
        new("gh-proxy",     "https://gh-proxy.com/",      true,  true,  "gh-proxy.com"),
        new("ghfast",       "https://ghfast.top/",        false, true,  "ghfast.top"),
        new("ghproxy-net",  "https://ghproxy.net/",       true,  true,  "ghproxy.net"),
        new("llkk",         "https://gh.llkk.cc/",        false, true,  "gh.llkk.cc"),
        new("moeyy",        "https://github.moeyy.xyz/",  false, true,  "github.moeyy.xyz"),
        Direct,
    };

    public static GitHubMirror? Find(string? id) => string.IsNullOrEmpty(id)
        ? null
        : Builtin.FirstOrDefault(m => m.Id == id);

    public static bool IsDirect(GitHubMirror m) => m.Id == Direct.Id;

    /// <summary>API 链（releases 列表）：记忆线路优先 → 支持 API 的加速线路 → 直连兜底。</summary>
    public static List<(GitHubMirror Mirror, string Url)> BuildApiChain(string memoryId, bool accelerate, string repo)
    {
        var chain = new List<(GitHubMirror, string)>();
        void Add(GitHubMirror m)
        {
            if (!m.SupportsApi || chain.Any(c => c.Item1.Id == m.Id)) return;
            chain.Add((m, $"{m.Prefix}https://api.github.com/repos/{repo}/releases?per_page=15"));
        }
        if (accelerate)
        {
            var mem = Find(memoryId);
            if (mem != null && mem.SupportsApi) Add(mem);
            foreach (var m in Builtin) Add(m); // Builtin 以直连收尾，自动兜底
        }
        Add(Direct);
        return chain;
    }

    /// <summary>下载链（release 资产）：记忆线路优先 → 全部加速线路 → 直连。</summary>
    public static List<(GitHubMirror Mirror, string Url)> BuildDownloadChain(string memoryId, bool accelerate, string browserUrl)
    {
        var chain = new List<(GitHubMirror, string)>();
        void Add(GitHubMirror m)
        {
            if (!m.SupportsDownload || chain.Any(c => c.Item1.Id == m.Id)) return;
            chain.Add((m, $"{m.Prefix}{browserUrl}"));
        }
        if (accelerate)
        {
            Add(Find(memoryId) ?? Builtin[0]);
            foreach (var m in Builtin) Add(m);
        }
        Add(Direct);
        return chain;
    }

    /// <summary>Atom 降级链（API 全灭时经 releases.atom 解析最新 tag）。</summary>
    public static List<(GitHubMirror Mirror, string Url)> BuildAtomChain(bool accelerate, string repo)
    {
        var chain = new List<(GitHubMirror, string)>();
        void Add(GitHubMirror m)
        {
            if (chain.Any(c => c.Item1.Id == m.Id)) return;
            chain.Add((m, $"{m.Prefix}https://github.com/{repo}/releases.atom"));
        }
        if (accelerate)
        {
            // 实测 gh-proxy.com 不放行 releases.atom，放链尾；403 型镜像对国内 IP 大概率可用
            Add(Builtin[1]); // ghfast
            Add(Builtin[2]); // ghproxy-net
            Add(Builtin[3]); // llkk
        }
        Add(Direct);
        return chain;
    }

    // ---------------- 通道语义与版本键 ----------------

    /// <summary>
    /// 通道匹配规则（版本只分稳定版与 Beta 两档）：Beta 跟踪一切发布（含预发布）；Stable 仅正式发布。
    /// 发布约定：tag 形如 v1.3.0（正式）/ v1.3.0-beta.2（预发布）。
    /// </summary>
    public static bool ChannelMatches(UpdateChannel channel, bool isPrerelease, string tag)
        => channel == UpdateChannel.Beta || !isPrerelease;

    /// <summary>Atom 降级模式的通道匹配：无 prerelease 标志，用版本键级别近似判断（级别=预发布视为预发布）。</summary>
    public static bool ChannelMatchesByKey(UpdateChannel channel, ReleaseVersionKey key)
        => channel == UpdateChannel.Beta || key.Rank != 1;

    /// <summary>
    /// 解析 tag/版本号为版本键：v1.3.0-beta.2 → (1.3.0, 级别=预发布, 修订=2)；
    /// 1.3.0 / v1.3.0 / 1.3.0-stable → 级别=正式。数字部分解析失败回退 0.0.0（排序恒最低）。
    /// 保留 lts 级别(2)解析以兼容历史 tag：稳定版通道仍会把它视为正式发布。
    /// </summary>
    public static ReleaseVersionKey ParseVersionKey(string? tagOrVersion)
    {
        var s = (tagOrVersion ?? "").Trim().TrimStart('v', 'V');
        if (s.Length == 0) return new ReleaseVersionKey(new Version(0, 0, 0), 0, 0);
        int dash = s.IndexOf('-');
        var numPart = dash < 0 ? s : s[..dash];
        var suffix = dash < 0 ? "" : s[(dash + 1)..];
        if (!Version.TryParse(numPart, out var num)) num = new Version(0, 0, 0);

        if (suffix.Length == 0 || suffix.Equals("stable", StringComparison.OrdinalIgnoreCase))
            return new ReleaseVersionKey(num, 3, 0);
        if (suffix.Contains("lts", StringComparison.OrdinalIgnoreCase))
            return new ReleaseVersionKey(num, 2, TrailingNumber(suffix));
        return new ReleaseVersionKey(num, 1, TrailingNumber(suffix)); // beta / alpha / rc / preview
    }

    /// <summary>本机版本键：InformationalVersion 优先（含 -beta 后缀最准确），失败退回纯数字版本。</summary>
    public static ReleaseVersionKey LocalKey()
    {
        var k = ParseVersionKey(AppVersion.DisplayText);
        if (k.Num.Major == 0 && k.Num.Minor == 0) k = ParseVersionKey(AppVersion.Text);
        return k;
    }

    /// <summary>取字符串末尾连续数字段（beta.2 → 2；beta2 → 2；lts → 0）。</summary>
    private static int TrailingNumber(string s)
    {
        int i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return i < s.Length && int.TryParse(s[i..], out var v) ? v : 0;
    }
}
