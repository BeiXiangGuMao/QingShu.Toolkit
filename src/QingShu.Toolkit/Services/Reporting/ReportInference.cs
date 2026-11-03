using System.Collections.Generic;
using System.Linq;
using QingShu.Toolkit.Models;

namespace QingShu.Toolkit.Services.Reporting;

/// <summary>
/// 报告综合推断引擎（纯规则推理、无 IO，与 SlowSpeedAnalyzer 同模式）。
/// 动机：PDF 报告各节的结论此前是分散的——结论概览只看通过/未通过数量、低速原因分析
/// 只覆盖低速线索、拓扑快照只有一句链路摘要——读者需要自行拼接才能得出最终判断。
/// 本引擎把全部证据合成为一段统一结论：总体判定、问题定位（就近优先）、跨节证据链
/// （逐条注明来源节）、交叉验证（证据印证 / 矛盾调和）与责任归属，供 PDF 报告渲染。
/// </summary>
public static class ReportInference
{
    /// <summary>综合推断结果（全部字段为完整中文文案，直接可渲染）。</summary>
    public sealed class Result
    {
        /// <summary>判定档位：0=健康 1=可用有隐患 2=存在故障（决定横幅配色）。</summary>
        public int Level { get; init; }
        /// <summary>档位徽标词：健康 / 可用有隐患 / 存在故障。</summary>
        public string Badge { get; init; } = "";
        /// <summary>总体判定一句话（含定位与多路证据概述）。</summary>
        public string Verdict { get; init; } = "";
        /// <summary>证据链，每条注明来源节（诊断明细/拓扑快照/网速实测/低速原因/修复记录）。</summary>
        public List<string> Evidence { get; init; } = new();
        /// <summary>交叉验证：证据印证关系与矛盾调和（可为空）。</summary>
        public string CrossCheck { get; init; } = "";
        /// <summary>责任归属与下一步动作。</summary>
        public string Responsibility { get; init; } = "";
    }

    // ---- 环节常量（层序：自本机向外 + 服务层；就近故障视为根因，远端故障多为连锁） ----
    private const string LPhysical = "本机物理链路与硬件";
    private const string LIpConfig = "本机网络配置";
    private const string LStack = "本机系统网络栈";
    private const string LProxy = "本机代理/VPN 配置";
    private const string LAccess = "接入段（本机 → 直连网关）";
    private const string LCore = "核心段（楼宇软路由 → 科技楼服务器）";
    private const string LExit = "出口段（裸光纤 → 城域网）";
    private const string LAuth = "城域网认证环节";
    private const string LDns = "DNS 解析环节";
    private const string LThroughput = "端到端吞吐能力";
    private const string LQuality = "端到端连接质量";

    public static Result Build(NetworkReportData data)
    {
        var results = data.Results;
        int fail = 0, warn = 0, fixedN = 0, pass = 0;
        foreach (var r in results)
        {
            switch (r.Status)
            {
                case CheckStatus.Fail: fail++; break;
                case CheckStatus.Warning: warn++; break;
                case CheckStatus.Fixed: fixedN++; pass++; break;
                case CheckStatus.Pass: pass++; break;
            }
        }
        var fails = results.Where(r => r.Status == CheckStatus.Fail).ToList();
        var warns = results.Where(r => r.Status == CheckStatus.Warning).ToList();

        int repTotal = data.Repairs.Count;
        int repOk = data.Repairs.Count(r => r.Status == RepairStatus.Ok);
        int repFail = data.Repairs.Count(r => r.Status == RepairStatus.Fail);

        var speed = data.SpeedTest;
        var causes = data.SlowCauses;
        var topo = data.Topology;

        // ---------- 定位：检查项环节 + 拓扑实测环节，就近排序取首要 ----------
        var layersFromChecks = (fails.Count > 0 ? fails : warns)
            .Select(r => LayerOf(r.Category)).Distinct().OrderBy(LayerOrder).ToList();
        var layers = new List<string>(layersFromChecks);
        var topoWorst = WorstOf(topo);
        if (topoWorst is { Health: TopoHealth.Fail } && !layers.Contains(topoWorst.Value.Layer))
            layers.Insert(0, topoWorst.Value.Layer);           // 实测故障证据最强，优先定位
        else if (topoWorst is { Health: TopoHealth.Warn } && !layers.Contains(topoWorst.Value.Layer))
            layers.Add(topoWorst.Value.Layer);                 // 实测瓶颈作补充环节
        string primary = layers.Count > 0 ? layers[0] : "";

        // ---------- 判定档位与总体判定 ----------
        int level;
        string badge, verdict;
        if (pass + fail + warn == 0)
        {
            level = 0;
            badge = "无有效数据";
            verdict = "本次诊断未产出有效检查结果（全部项目被跳过或未执行），请重新运行诊断后再导出报告。";
        }
        else if (fail > 0)
        {
            level = 2;
            badge = "存在故障";
            string loc = layers.Count == 0 ? "未定环节" :
                layers.Count == 1 ? $"「{layers[0]}」" :
                $"「{layers[0]}」（同时涉及 {string.Join("、", layers.Skip(1))}）";
            verdict = $"本次诊断发现 {fail} 项故障、{warn} 项警告。综合检查项、拓扑实测" +
                      (speed != null ? "、吞吐实测" : "") + (causes.Count > 0 ? "、低速归因" : "") +
                      $"多路证据交叉判断，问题定位于{loc}。";
            if (repTotal > 0)
                verdict += $" 已执行自动修复 {repTotal} 项（成功 {repOk} 项" +
                           (repFail > 0 ? $"、未成功 {repFail} 项" : "") + "），明细见「修复记录」。";
        }
        else if (warn > 0)
        {
            level = 1;
            badge = "可用有隐患";
            verdict = $"网络当前可用，未发现阻断性故障；但存在 {warn} 项警告，主要涉及" +
                      (layers.Count > 0 ? $"「{layers[0]}」" : "若干环节") +
                      "。隐患暂不影响正常使用，建议按「四、分级处置建议」在今日内处理，防止演变为故障。";
        }
        else
        {
            level = 0;
            badge = "健康";
            verdict = $"全部 {pass} 项检查通过" +
                      (fixedN > 0 ? $"（含已修复 {fixedN} 项）" : "") +
                      "，未发现故障与隐患，网络处于健康状态。该判断整合了结论概览、诊断明细" +
                      (topo != null ? "、网络拓扑实测" : "") + (speed != null ? "、网速实测" : "") +
                      "的全部证据；如后续出现异常，建议留存本报告作为基线对比。";
        }

        // ---------- 跨节证据链（逐条注明来源节） ----------
        var ev = new List<string>();
        if (fails.Count > 0)
        {
            var lines = fails.Take(3).Select(r => $"「{r.Name}」未通过：{Clip(SummaryOf(r), 72)}");
            ev.Add("检查项（三、诊断明细）：" + string.Join("；", lines) +
                   (fails.Count > 3 ? $"；其余 {fails.Count - 3} 项见明细表" : "") + "。");
        }
        else if (warns.Count > 0)
        {
            var lines = warns.Take(2).Select(r => $"「{r.Name}」警告：{Clip(SummaryOf(r), 72)}");
            ev.Add("检查项（三、诊断明细）：" + string.Join("；", lines) +
                   (warns.Count > 2 ? $"；其余 {warns.Count - 2} 项见明细表" : "") + "。");
        }
        else
        {
            ev.Add($"检查项（三、诊断明细）：{pass} 项全部通过，无故障与警告。");
        }

        if (topo is { Nodes.Count: > 0 })
        {
            var bad = topo.Nodes.Where(n => n.Health is TopoHealth.Fail or TopoHealth.Warn)
                .OrderByDescending(n => Rank(n.Health)).Take(2).ToList();
            string t = bad.Count > 0
                ? "拓扑实测（网络拓扑快照）：" + string.Join("；",
                      bad.Select(n => $"{n.Badge}「{n.Title}」{HealthWord(n.Health)}（{Clip(StatusOf(n), 44)}）"))
                : "拓扑实测（网络拓扑快照）：主链各段与出口分支实测均为正常";
            if (!string.IsNullOrWhiteSpace(topo.SummaryText)) t += $"。{topo.SummaryText}";
            ev.Add(t + "。");
        }
        else
        {
            ev.Add("拓扑实测（网络拓扑快照）：本次未生成拓扑快照，分段定位以检查项与命令输出为准。");
        }

        if (speed != null)
        {
            ev.Add(speed.Success
                ? $"吞吐实测（网速实测）：代表带宽 {speed.SpeedText}（{speed.ExperienceBand}），链路协商 {speed.LinkText}，实测效率 {speed.EfficiencyText}。"
                : $"吞吐实测（网速实测）：本次未测得有效吞吐数据（{speed.Error}），低速判断以分段延迟画像与链路协商为准。");
        }

        if (causes.Count > 0)
        {
            var parts = new List<string>();
            for (int i = 0; i < causes.Count && i < 2; i++)
                parts.Add($"{(i == 0 ? "首要嫌疑" : "次要嫌疑")}「{causes[i].Title}」（{causes[i].Level}嫌疑，权重 {causes[i].Weight}）");
            ev.Add($"低速归因（低速原因分析）：共 {causes.Count} 项嫌疑，" + string.Join("；", parts) + "。");
        }
        else if (speed != null)
        {
            ev.Add("低速归因（低速原因分析）：各路证据未识别出明确拖速因素。");
        }

        if (repTotal > 0)
            ev.Add($"修复记录：执行自动修复 {repTotal} 项，成功 {repOk} 项、未成功 {repFail} 项（明细见修复记录节）。");

        // ---------- 交叉验证：印证 / 收敛 / 矛盾调和 ----------
        // 印证必须对拓扑合并前的检查项原始首要定位比较：拓扑抢占插入首位后
        // 再与 layers[0] 比对会恒真，把「两路证据不一致」误报成「相互印证」。
        var notes = new List<string>();
        bool chainBad = topoWorst is { Health: TopoHealth.Fail or TopoHealth.Warn } &&
                        IsSegmentLayer(topoWorst.Value.Layer);
        if (topoWorst is { Health: TopoHealth.Fail or TopoHealth.Warn } &&
            layersFromChecks.Count > 0 && topoWorst.Value.Layer == layersFromChecks[0])
            notes.Add($"拓扑实测与检查项结论指向同一环节（{layers[0]}），多路证据相互印证，判定可信度高。");
        if (topo is { Nodes.Count: > 0 } && !chainBad && fails.Any(f => f.Category == CheckCategory.Internet))
            notes.Add("内网各段实测正常而公网方向检查未通过：断点收敛于出口段上游（裸光纤/城域网），可基本排除本机与校园内网。");
        if (speed is { Success: false } && topo is { Nodes.Count: > 0 } && !chainBad)
            notes.Add("测速未获有效数据但各段链路实测正常：多为测速节点被校园网白名单拦截或高峰限速，不代表网络故障，低速判断以分段延迟画像为准。");
        if (speed is { Success: true } && causes.Count > 0 && layers.Count > 0 &&
            CauseLayer(causes[0].Title) is { } cl && cl == layers[0])
            notes.Add($"测速偏低、低速归因与环节定位三方一致指向「{layers[0]}」，按首要嫌疑处置后复测应能直接改善体验。");
        if (topo == null && speed == null && (fail > 0 || warn > 0))
            notes.Add("本次证据面以检查项为限（无拓扑实测与吞吐数据），定位精度受限；如需分段定位请运行全面诊断或网速与低速分析。");
        if (repFail > 0)
            notes.Add($"存在 {repFail} 项自动修复未成功，相关环节需按「分级处置建议」人工处理。");

        // ---------- 责任归属与下一步 ----------
        string resp = ResponsibilityOf(primary) + (level > 0
            ? " 下一步：按「四、分级处置建议」逐项执行，处理后重跑诊断复核，并保留本报告作为对比基线。"
            : " 下一步：无需处理，建议每周复检形成健康基线。");

        return new Result
        {
            Level = level,
            Badge = badge,
            Verdict = verdict,
            Evidence = ev,
            CrossCheck = string.Join("", notes.Take(2)),
            Responsibility = resp
        };
    }

    // ================= 规则辅助 =================

    private static int Rank(TopoHealth h) => h switch
    {
        TopoHealth.Fail => 2,
        TopoHealth.Warn => 1,
        _ => 0
    };

    private static string HealthWord(TopoHealth h) => h switch
    {
        TopoHealth.Fail => "实测异常",
        TopoHealth.Warn => "实测瓶颈",
        _ => "实测正常"
    };

    /// <summary>拓扑主链 + 分支的最差环节（节点与连线一起评估，Unknown 忽略）。</summary>
    private static (TopoHealth Health, string Layer)? WorstOf(NetworkTopology? topo)
    {
        if (topo is not { Nodes.Count: > 0 }) return null;
        (TopoHealth, string)? worst = null;
        foreach (var n in topo.Nodes)
        {
            if (n.Health == TopoHealth.Unknown) continue;
            if (worst == null || Rank(n.Health) > Rank(worst.Value.Item1))
                worst = (n.Health, RoleLayer(n.Role));
        }
        var roleByKey = new Dictionary<string, TopoRole>();
        foreach (var n in topo.Nodes) roleByKey[n.Key] = n.Role;
        foreach (var e in topo.Edges)
        {
            if (e.Health == TopoHealth.Unknown) continue;
            var role = roleByKey.TryGetValue(e.To, out var to) ? to
                     : roleByKey.TryGetValue(e.From, out var from) ? from : TopoRole.Local;
            if (worst == null || Rank(e.Health) > Rank(worst.Value.Item1))
                worst = (e.Health, RoleLayer(role));
        }
        return worst == null ? null : (worst.Value.Item1, worst.Value.Item2);
    }

    private static string RoleLayer(TopoRole r) => r switch
    {
        TopoRole.Local => LPhysical,
        TopoRole.Gateway => LAccess,
        TopoRole.InnerHop => LCore,
        TopoRole.Exit or TopoRole.Internet => LExit,
        TopoRole.Portal => LAuth,
        TopoRole.Dns => LDns,
        _ => "其他环节"
    };

    private static string LayerOf(CheckCategory c) => c switch
    {
        CheckCategory.PhysicalLink or CheckCategory.HardwareDriver => LPhysical,
        CheckCategory.IpConfig => LIpConfig,
        CheckCategory.SystemStack or CheckCategory.Clock => LStack,
        CheckCategory.ProxyVpn => LProxy,
        CheckCategory.GatewayRouting => LAccess,
        CheckCategory.Intranet => LCore,
        CheckCategory.Internet => LExit,
        CheckCategory.PortalAuth => LAuth,
        CheckCategory.Dns => LDns,
        CheckCategory.Throughput => LThroughput,
        CheckCategory.Quality => LQuality,
        _ => "其他环节"
    };

    private static int LayerOrder(string layer) => layer switch
    {
        LPhysical => 0,
        LIpConfig => 1,
        LStack => 2,
        LProxy => 3,
        LAccess => 4,
        LCore => 5,
        LExit => 6,
        LAuth => 7,
        LDns => 8,
        LThroughput => 9,
        LQuality => 10,
        _ => 11
    };

    private static bool IsSegmentLayer(string layer) => layer == LAccess || layer == LCore || layer == LExit;

    /// <summary>低速嫌疑标题 → 环节（关键词映射，匹配不上返回 null 不参与印证）。</summary>
    private static string? CauseLayer(string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        if (title.Contains("接入段")) return LAccess;
        if (title.Contains("核心段")) return LCore;
        if (title.Contains("出口段")) return LExit;
        if (title.Contains("DNS")) return LDns;
        if (title.Contains("代理")) return LProxy;
        if (title.Contains("无线") || title.Contains("网卡") || title.Contains("链路协商") || title.Contains("半双工")) return LPhysical;
        return null;
    }

    private static string ResponsibilityOf(string primary) => primary switch
    {
        LPhysical => "物理层与硬件问题请先本机自检（更换网线/端口，无线则靠近 AP）；无效请联系机房管理员报修网线、水晶头或交换机端口。",
        LIpConfig => "属本机配置范围，可自行按「分级处置建议」修复后复测；无效时联系机房管理员核对教室端口与 VLAN 划分。",
        LStack => "属本机系统栈范围，一键修复即可处理大部分项目；无效时联系机房管理员重置系统网络组件。",
        LProxy => "由本机使用者在系统设置中关闭代理/VPN 后复测；教学机原则上不应配置代理。",
        LAccess => "先与同班/同楼层其他电脑比对：同样异常则报本校信息中心核查班级中转设备与楼宇软路由；仅本机异常则更换网线/端口排除本机链路。",
        LCore => "楼宇软路由与科技楼服务器为全校共享压力点，请携本报告联系本校信息中心核查软路由负载与上联链路。",
        LExit => "裸光纤上联与城域网出口归教体局管理，请携本报告联系教体局信息中心核查出口带宽与 QoS 策略。",
        LAuth => "先在认证门户重新登录即可恢复；持续异常联系学校信息中心，必要时由其反馈教体局城域网认证平台。",
        LDns => "可用一键修复切换公共 DNS；若校园网限制外部 DNS，请本校信息中心调整递归策略。",
        LThroughput => "吞吐为端到端综合表现，请先处理明细表中的具体失败项并复测；持续偏低按出口方向反馈教体局信息中心。",
        LQuality => "连接质量类警告多为环境性波动，按「分级处置建议」观察复测即可；持续出现再逐段定位。",
        _ => "结合「分级处置建议」逐项处理；无法自行定位时保留本报告联系学校信息中心。"
    };

    private static string SummaryOf(CheckItem r)
        => string.IsNullOrWhiteSpace(r.Summary) ? "无结果说明" : r.Summary.Trim();

    private static string StatusOf(TopoNode n)
        => string.IsNullOrWhiteSpace(n.StatusText) ? n.Subtitle : n.StatusText;

    private static string Clip(string s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
