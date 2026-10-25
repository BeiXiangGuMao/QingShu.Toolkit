using System;
using System.Collections.Generic;
using System.Linq;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Network;

/// <summary>
/// 低速原因分析引擎：把低速专项各项检查证据 + 测速实测结果，
/// 归并为按嫌疑度排序的原因清单（高/中/低），每条附依据与处置措施。
/// 纯规则推理、无外部依赖；供 UI 展示与 PDF 报告使用。
/// </summary>
public static class SlowSpeedAnalyzer
{
    public static List<SlowCause> Analyze(CheckContext? ctx, IReadOnlyList<CheckItem> results, SpeedTestResult? speed)
    {
        var causes = new List<SlowCause>();
        var byId = results.Where(r => r.Status is CheckStatus.Warning or CheckStatus.Fail)
                          .GroupBy(r => r.Id)
                          .ToDictionary(g => g.Key, g => g.First());

        void Add(string title, int weight, List<string> evidence, List<string> actions)
        {
            var e = evidence.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            var a = actions.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (a.Count == 0) a.Add("结合其他嫌疑项综合处置后复测验证。");
            causes.Add(new SlowCause
            {
                Title = title,
                Weight = weight,
                Level = weight >= 75 ? "高" : weight >= 50 ? "中" : "低",
                Evidence = e,
                Actions = a
            });
        }

        // ---- 1. 代理中转 ----
        if (byId.ContainsKey("proxy.system") || ctx?.SharedBool("proxy.enabled") == true)
            Add("流量经系统代理中转", 82,
                new() { "系统代理处于开启状态，所有请求都要经代理服务器转发，代理慢或过载时整体变慢。" },
                new() { "关闭系统代理（一键修复支持）；确需代理的请检查代理服务器负载与出口。" });

        // ---- 2. 链路协商异常（半双工/百兆/低无线速率） ----
        if (byId.TryGetValue("speed.nic_rate", out var nic) && nic.Status == CheckStatus.Fail)
            Add("网卡半双工/协商异常", 88,
                new() { "网卡处于半双工或速率协商异常状态，冲突重传会严重拖慢吞吐。" },
                new() { "一键修复重新协商网卡；无效则更换网线（CAT5e+）、水晶头或交换机端口。" });
        else if (byId.ContainsKey("speed.nic_rate"))
            Add("链路协商速率偏低", 60,
                new() { "有线未达千兆或无线 PHY 速率偏低：物理层先于出口限制了带宽上限。" },
                new() { "检查网线等级与端口；无线请靠近 AP 或切换 5G；一键修复可重协商。" });

        // ---- 3. Wi-Fi 频段/信道/信号 ----
        if (byId.ContainsKey("speed.wifi_phy") || byId.ContainsKey("wifi.signal"))
        {
            var ev = new List<string>();
            if (ctx?.SharedBool("wifi.24g") == true) ev.Add("工作在 2.4 GHz 频段（教室密集场景干扰大）");
            if (ctx?.SharedBool("wifi.channel.overlap") == true) ev.Add("信道与邻区 AP 重叠");
            if ((ctx?.Wifi?.SignalPct ?? 100) < 60) ev.Add($"信号强度仅 {ctx!.Wifi!.SignalPct}%");
            if (ev.Count == 0) ev.Add("无线链路质量欠佳（频段/信号/干扰之一）");
            Add("无线链路是瓶颈", 72, ev,
                new() { "优先改用有线；无法布线时切换 5 GHz SSID、缩短与 AP 距离、避开微波炉/蓝牙。" });
        }

        // ---- 4. 本机到网关段 ----
        if (byId.ContainsKey("speed.path_profile") && ctx?.SharedBool("path.gw.bad") == true)
            Add("本机 → 网关段拥塞/劣化", 78,
                new() { "第一跳就出现高延迟/丢包：问题在教室内部（AP 过载、交换机端口、无线干扰），与出口无关。" },
                new() { "换有线/换交换机端口/换 AP 排除法定位；无线则减少同 AP 并发，报修本校信息中心。" });

        // ---- 5. 网关到出口段 ----
        if (byId.ContainsKey("speed.path_profile") && ctx?.SharedBool("path.uplink.bad") == true)
            Add("城域网出口高峰拥塞", 70,
                new() { "本机到网关很快、出网关即慢：典型的上联/出口带宽高峰期拥塞。" },
                new() { "错峰使用大流量任务；持续如此请携 PDF 报告联系教体局信息中心核查出口带宽与 QoS 策略。" });

        // ---- 6. DNS 拖慢 ----
        if (byId.ContainsKey("speed.dns_perf"))
            Add("DNS 解析拖慢所有网站", 66,
                new() { "解析耗时偏高：每个网站的首次打开都要先等 DNS，是“感觉慢”的常见隐形原因。" },
                new() { "一键修复切换为更快的公共 DNS（若校园网限制外部 DNS，请信息中心调整递归策略）。" });

        // ---- 7. TCP 自调谐被改 ----
        if (byId.ContainsKey("speed.tcp_tuning"))
            Add("TCP 接收窗口自调谐被改", 76,
                new() { "接收窗口自动调节不是 normal（曾遭优化软件改动），高带宽链路跑不满。" },
                new() { "一键修复恢复 normal 并启用 RSS，无需重启。" });

        // ---- 8. 传递优化 P2P ----
        if (byId.ContainsKey("speed.delivopt"))
            Add("Windows 传递优化 P2P 占用上行", 58,
                new() { "系统正在向局域网/互联网上传更新分片，上行被打满时整个网络都会卡。" },
                new() { "一键修复关闭 P2P 分发（单值可逆）；或组策略统一配置教学机分发模式。" });

        // ---- 9. 后台占用 ----
        if (byId.ContainsKey("speed.background"))
            Add("后台程序占用带宽", 54,
                new() { "网盘同步/下载器/流媒体客户端等在后台消耗带宽与连接数。" },
                new() { "退出或限速后台程序；上课时段统一关闭自动同步与自动更新。" });

        // ---- 10. 测速结果的横向对比 ----
        if (speed is { Success: true })
        {
            if (speed.MbpsAvg < 20)
                Add("实测带宽本身不足", 68,
                    new() { $"下载实测仅 {speed.SpeedText}（{speed.ExperienceBand}）。" },
                    new() { "先处理上方高嫌疑项再复测；若复测依旧且各段延迟正常，多为出口带宽/限速问题，向教体局信息中心反馈。" });
            if (speed.LinkMbps > 100 && speed.Efficiency < 0.4)
                Add("实测远低于链路能力", 64,
                    new() { $"链路协商 {speed.LinkText}，实测效率仅 {speed.EfficiencyText}：瓶颈不在本机链路。" },
                    new() { "按出口拥塞/限速方向排查（错峰、限流、联系信息中心）；本机侧修复收益有限。" });
        }

        // ---- 11. VPN/多网卡 ----
        if (byId.ContainsKey("vpn.adapters"))
            Add("VPN/多网卡流量绕行", 60,
                new() { "VPN 或多网络并存会使流量绕行隧道，认证、路由与吞吐都受影响。" },
                new() { "关闭教学机上的 VPN；只保留教学所需网络。" });

        // ---- 12. MTU ----
        if (byId.ContainsKey("quality.mtu"))
            Add("链路 MTU 偏小", 32,
                new() { "存在隧道/PPPoE 压低 MTU：大包分片使大文件传输与网页加载偶发变慢。" },
                new() { "一般无需处理；持续影响请信息中心核查中间链路 MTU。" });

        return causes.OrderByDescending(c => c.Weight).ToList();
    }

    /// <summary>低速结论一句话（UI/报告用）。</summary>
    public static string Conclusion(IReadOnlyList<SlowCause> causes, SpeedTestResult? speed)
    {
        if (causes.Count == 0)
        {
            if (speed is { Success: true })
                return $"未发现明显拖速因素，实测带宽 {speed.SpeedText}；若仍感觉慢，多为出口带宽本身有限，建议错峰使用并反馈信息中心。";
            return "未发现明显拖速因素。";
        }
        var top = causes[0];
        return $"共识别 {causes.Count} 项嫌疑因素，首要嫌疑：{top.Title}（{top.Level}）。建议先执行其处置措施并复测对比。";
    }
}
