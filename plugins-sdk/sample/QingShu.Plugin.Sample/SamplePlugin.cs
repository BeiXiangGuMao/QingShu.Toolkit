using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Plugin.Sample;

/// <summary>
/// 最小可用插件示例：
/// 1) 注册「查询最新 Release」快捷动作（走宿主多线路 + Token 治理访问仓库 Releases）；
/// 2) 演示日志 / 数据目录 / 权限的正确用法。
/// 部署：dotnet build 后把 plugin.json.template 改名 plugin.json 并连同 dll 一起放进
///       plugins\qingshu.sample\ 目录，用 tools\plugin-sign.ps1 签名。
/// </summary>
public sealed class SamplePlugin : IQToolkitPlugin
{
    private IPluginContext? _ctx;

    public string DisplayName => "示例插件（Release 查询）";

    public async Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        _ctx = context;
        context.Log("info", "示例插件加载成功");

        context.RegisterQuickAction("查询最新 Release", async token =>
        {
            var access = context.Releases
                ?? throw new InvalidOperationException("plugin.json 未声明 releases 权限，无法访问仓库 Release");

            var list = await access.ListAsync(5, token).ConfigureAwait(false);
            if (list.Count == 0) return "仓库暂无 Release。";

            var sb = new StringBuilder();
            foreach (var r in list.Take(5))
            {
                sb.Append(r.TagName);
                if (r.Prerelease) sb.Append("（预发布）");
                sb.Append("：").Append(r.Name);
                if (r.PublishedAt is { } at) sb.Append($" · {at:yyyy-MM-dd}");
                sb.Append($" · 资产 {r.Assets.Count} 个");
                if (r.Assets.Count > 0)
                    sb.Append("（").Append(string.Join("、", r.Assets.Select(a => a.Name))).Append("）");
                sb.AppendLine();
            }
            sb.Append($"共 {list.Count} 条。需要文件时调用 DownloadAssetAsync(tag, 资产名)，下载落地在宿主 plugin-downloads 目录。");
            return sb.ToString();
        });

        context.RegisterQuickAction("写数据目录探针", _ =>
        {
            // storage 权限示例：插件私有数据目录（%LOCALAPPDATA%\QingShu.Toolkit\plugins-data\<id>）
            var probe = System.IO.Path.Combine(context.DataFolder, "probe.txt");
            System.IO.File.WriteAllText(probe, $"touched {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            return Task.FromResult<string?>($"已写入 {probe}");
        });

        await Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        _ctx?.Log("info", "示例插件卸载：清理在途状态");
        _ctx = null; // 释放对宿主回调的引用，配合宿主卸载 ALC
        return Task.CompletedTask;
    }
}
