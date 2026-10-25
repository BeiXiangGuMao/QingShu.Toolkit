using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services.Network;

namespace QingShu.Toolkit.Services.Reporting;

public interface IPdfReportService
{
    /// <summary>生成正式网络诊断报告 PDF，返回文件完整路径。</summary>
    Task<string> GenerateAsync(NetworkReportData data);
}

/// <summary>
/// QuestPDF 报告模板（正规文档样式）：
/// 页眉品牌条 + 报告编号（按日累计次数）→ 设备与位置信息 → 结论概览 →
/// 分层诊断明细表 → 修复记录 → 分级处置建议 → 签名栏 → 页脚页码。
/// </summary>
public sealed class PdfReportService : IPdfReportService
{
    public Task<string> GenerateAsync(NetworkReportData data)
        => Task.Run(() =>
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "QingShu.Toolkit", "Reports", DateTime.Now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);

            int seq = 1;
            try
            {
                seq = Directory.GetFiles(dir, "网络诊断报告_*.pdf").Length + 1;
            }
            catch { }
            var reportId = $"QS-DIAG-{DateTime.Now:yyyyMMdd}-{seq:D3}";
            data.Meta.ReportId = reportId;

            var file = Path.Combine(dir,
                $"网络诊断报告_{Sanitize(data.Meta.DeviceTag)}_{DateTime.Now:yyyyMMdd-HHmmss}.pdf");

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.DefaultTextStyle(x => x.FontFamily("Microsoft YaHei").FontSize(9.5f).FontColor("#1E293B").LineHeight(1.5f));

                    page.Header().Element(c => ComposeHeader(c, data));
                    page.Content().Element(c => ComposeContent(c, data));
                    page.Footer().Row(row =>
                    {
                        row.RelativeItem().Text($"青枢工具箱 v{data.Meta.ToolVersion} 自动生成 · 报告编号 {reportId}")
                            .FontSize(8).FontColor("#94A3B8");
                        row.AutoItem().Width(120, Unit.Point).AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(x => x.FontSize(8).FontColor("#94A3B8"));
                            t.Span("第 ");
                            t.CurrentPageNumber();
                            t.Span(" 页 / 共 ");
                            t.TotalPages();
                            t.Span(" 页");
                        });
                    });
                });
            }).GeneratePdf(file);

            return file;
        });

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "未命名设备" : name;
    }

    // ================= 页眉 =================
    private static void ComposeHeader(IContainer c, NetworkReportData data)
    {
        c.Row(row =>
        {
            row.AutoItem().Width(4, Unit.Point).Height(34, Unit.Point)
                .Background("#2563EB");
            row.RelativeItem().PaddingLeft(10).Column(col =>
            {
                col.Item().Text("网络故障诊断报告").FontSize(17).SemiBold().FontColor("#0F172A");
                col.Item().Text("青枢工具箱 QingShu.Toolkit · 校园网络专项诊断").FontSize(9).FontColor("#64748B");
            });
            row.RelativeItem().AlignRight().Column(col =>
            {
                col.Item().AlignRight().Text(data.Meta.ReportId).FontSize(10).SemiBold().FontColor("#2563EB");
                col.Item().AlignRight().Text($"生成时间 {data.Meta.GeneratedAt:yyyy-MM-dd HH:mm}").FontSize(8).FontColor("#94A3B8");
            });
        });
        // 分隔线（LineHorizontal 返回 void，用 1pt 高色带等效实现）
        c.PaddingTop(6).Height(1, Unit.Point).Background("#E2E8F0");
    }

    // ================= 正文 =================
    private static void ComposeContent(IContainer c, NetworkReportData data)
    {
        c.PaddingTop(12).Column(col =>
        {
            ComposeInfoSection(col, data);
            ComposeSummarySection(col, data);
            ComposeSpeedSection(col, data);
            ComposeDetailSections(col, data);
            ComposeSlowCausesSection(col, data);
            ComposeRepairSection(col, data);
            ComposeAdviceSection(col, data);
            ComposeSignSection(col, data);
        });
    }

    private static void SectionTitle(ColumnDescriptor col, string title)
    {
        col.Item().PaddingTop(14).PaddingBottom(6).Row(r =>
        {
            r.AutoItem().Width(3, Unit.Point).Height(12, Unit.Point).Background("#2563EB").PaddingTop(0);
            r.RelativeItem().PaddingLeft(6).Text(title).FontSize(12.5f).SemiBold().FontColor("#0F172A");
        });
    }

    // ---------- 设备与位置信息 ----------
    private static void ComposeInfoSection(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "一、设备与位置信息");
        var m = data.Meta;
        (string, string)[] left =
        {
            ("学校", Empty(m.School, "未填写")),
            ($"年级 / 班级", $"{Empty(m.Grade, "未填写")} / {Empty(m.ClassName, "未填写")}"),
            ("报告人", Empty(m.Reporter, "未填写")),
            ("设备编号", Empty(m.DeviceTag, m.HostName)),
            ("设备序列号（SMBIOS）", Empty(m.SerialNumber, "未知")),
            ("MachineGuid", Empty(m.MachineGuid, "未知"))
        };
        (string, string)[] right =
        {
            ("主机名", m.HostName),
            ("操作系统", m.OsVersion),
            ("处理器", Empty(m.CpuName, "未知")),
            ("内存", m.TotalRamText),
            ("网卡型号", Empty(m.NicModel, "未知")),
            ("IP / MAC", $"{Empty(m.IpV4, "无")} / {Empty(m.MacAddress, "无")}")
        };
        col.Item().Row(row =>
        {
            row.RelativeItem().Element(cc => InfoTable(cc, left));
            row.ConstantItem(10);
            row.RelativeItem().Element(cc => InfoTable(cc, right));
        });
        col.Item().PaddingTop(6).Text(t =>
        {
            // Text(Action<TextDescriptor>) 重载返回 void，块级样式须经 DefaultTextStyle 设置（同页脚写法）
            t.DefaultTextStyle(x => x.FontSize(9));
            t.Span("诊断模式：").FontColor("#64748B");
            t.Span($"{m.ModeText}    ").SemiBold();
            if (!string.IsNullOrWhiteSpace(m.LinkSpeedText))
                t.Span($"链路速率：{m.LinkSpeedText}    ").FontColor("#64748B");
            t.Span($"起止：{m.StartedAt:HH:mm:ss} — {m.FinishedAt:HH:mm:ss}").FontColor("#64748B");
        });
    }

    private static string Empty(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

    private static void InfoTable(IContainer c, (string K, string V)[] rows)
    {
        c.Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(96);
                cd.RelativeColumn();
            });
            foreach (var (k, v) in rows)
            {
                table.Cell().BorderBottom(0.5f).BorderColor("#E2E8F0").PaddingVertical(3.5f)
                    .Text(k).FontSize(8.5f).FontColor("#64748B");
                table.Cell().BorderBottom(0.5f).BorderColor("#E2E8F0").PaddingVertical(3.5f)
                    .Text(v).FontSize(8.5f).FontColor("#1E293B");
            }
        });
    }

    // ---------- 结论概览 ----------
    private static void ComposeSummarySection(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "二、诊断结论概览");
        int pass = 0, warn = 0, fail = 0, fixedCount = 0, skip = 0;
        foreach (var r in data.Results)
        {
            switch (r.Status)
            {
                case CheckStatus.Pass: pass++; break;
                case CheckStatus.Warning: warn++; break;
                case CheckStatus.Fail: fail++; break;
                case CheckStatus.Fixed: fixedCount++; break;
                case CheckStatus.Skipped: skip++; break;
            }
        }
        col.Item().Row(row =>
        {
            Chip(row, "#047857", "#ECFDF5", "通过", pass);
            Chip(row, "#B45309", "#FFFBEB", "警告", warn);
            Chip(row, "#B91C1C", "#FEF2F2", "未通过", fail);
            Chip(row, "#1D4ED8", "#EFF6FF", "已修复", fixedCount);
            Chip(row, "#475569", "#F8FAFC", "跳过", skip);
        });

        string conclusion;
        if (fail == 0 && warn == 0)
            conclusion = "本次诊断共检查全部项目，未发现影响校园网络使用的故障，网络处于健康状态。";
        else if (fail == 0)
            conclusion = $"本次诊断发现 {warn} 项警告，网络当前可用但存在隐患，建议按处置建议在今日内处理。";
        else
            conclusion = $"本次诊断发现 {fail} 项故障、{warn} 项警告。" +
                (fixedCount > 0 ? $"其中 {fixedCount} 项已尝试自动修复。" : "") +
                "未通过的故障项请按“四、分级处置建议”逐项处理，处理后可重新运行诊断复核。";
        col.Item().PaddingTop(8).PaddingBottom(2).Text(conclusion).FontSize(9.5f).LineHeight(1.6f);
    }

    private static void Chip(RowDescriptor row, string textColor, string bgColor, string label, int count)
    {
        row.AutoItem().PaddingRight(6).Background(bgColor).Border(0.5f).BorderColor(bgColor)
            .PaddingVertical(5).PaddingHorizontal(10).Text($"{label} {count}")
            .FontSize(9.5f).SemiBold().FontColor(textColor);
    }

    // ---------- 分层明细 ----------
    private static void ComposeDetailSections(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "三、诊断明细");
        var groups = data.Results.GroupBy(r => r.Category).ToList();
        foreach (var g in groups)
        {
            col.Item().PaddingTop(6).Text(CheckCategoryText.Of(g.Key)).FontSize(10).SemiBold().FontColor("#334155");

            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.ConstantColumn(52);
                    cd.ConstantColumn(118);
                    cd.RelativeColumn(2);
                    cd.RelativeColumn(3);
                });
                table.Header(h =>
                {
                    HeadCell(h, "状态");
                    HeadCell(h, "检查项");
                    HeadCell(h, "结果说明");
                    HeadCell(h, "建议措施");
                });
                foreach (var r in g)
                {
                    var bg = r.Status switch
                    {
                        CheckStatus.Fail => "#FEF2F2",
                        CheckStatus.Warning => "#FFFBEB",
                        _ => "#FFFFFF"
                    };
                    BodyCell(table, StatusText(r.Status), bg, StatusColor(r.Status), true);
                    BodyCell(table, r.Name, bg, "#1E293B");
                    BodyCell(table, string.IsNullOrWhiteSpace(r.Summary) ? "—" : r.Summary, bg, "#334155");
                    BodyCell(table, string.IsNullOrWhiteSpace(r.Suggestion) ? "—" : r.Suggestion, bg, "#475569");
                }
            });
        }
    }

    private static string StatusText(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "通过",
        CheckStatus.Warning => "警告",
        CheckStatus.Fail => "未通过",
        CheckStatus.Fixed => "已修复",
        CheckStatus.Skipped => "跳过",
        _ => "—"
    };

    private static string StatusColor(CheckStatus s) => s switch
    {
        CheckStatus.Pass or CheckStatus.Fixed => "#047857",
        CheckStatus.Warning => "#B45309",
        CheckStatus.Fail => "#B91C1C",
        _ => "#64748B"
    };

    private static void HeadCell(TableCellDescriptor t, string text)
    {
        t.Cell().Background("#2563EB").PaddingVertical(4).PaddingHorizontal(5)
            .Text(text).FontSize(8.5f).SemiBold().FontColor("#FFFFFF");
    }

    private static void BodyCell(TableDescriptor t, string text, string bg, string color, bool semiBold = false)
    {
        var d = t.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#E2E8F0")
            .PaddingVertical(4).PaddingHorizontal(5).Text(text).FontSize(8.2f).FontColor(color);
        if (semiBold) d.SemiBold();
    }

    // ---------- 网速实测（低速专项） ----------
    private static void ComposeSpeedSection(ColumnDescriptor col, NetworkReportData data)
    {
        var s = data.SpeedTest;
        if (s == null) return;
        SectionTitle(col, "网速实测（低速专项）");
        if (!s.Success)
        {
            col.Item().PaddingTop(4).Text($"本次未测得有效吞吐数据（{s.Error}）。低速判断以分段延迟画像、链路协商与 DNS 耗时为准。")
                .FontSize(9.5f).FontColor("#B45309");
            return;
        }
        (string, string)[] left =
        {
            ("代表带宽", s.SpeedText),
            ("峰值瞬时", s.PeakText),
            ("体验档位", s.ExperienceBand),
            ("测速节点", s.EndpointLabel)
        };
        (string, string)[] right =
        {
            ("链路协商速率", s.LinkText),
            ("实测效率", s.EfficiencyText),
            ("总下载 / 用时", $"{FormatHelper.Bytes(s.BytesTotal)} / {s.DurationMs / 1000.0:0.#} s"),
            ("测速方式", $"多连接限时下载（{s.Endpoints.Count} 个节点）")
        };
        col.Item().Row(row =>
        {
            row.RelativeItem().Element(cc => InfoTable(cc, left));
            row.ConstantItem(10);
            row.RelativeItem().Element(cc => InfoTable(cc, right));
        });
        if (s.Endpoints.Count > 0)
        {
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(2);
                    cd.ConstantColumn(70);
                    cd.ConstantColumn(70);
                    cd.RelativeColumn(2);
                });
                table.Header(h => { HeadCell(h, "测速节点"); HeadCell(h, "平均速率"); HeadCell(h, "峰值"); HeadCell(h, "下载量 / 结果"); });
                foreach (var e in s.Endpoints)
                {
                    var bg = e.Success ? "#FFFFFF" : "#FFFBEB";
                    BodyCell(table, e.Label, bg, "#1E293B");
                    BodyCell(table, e.Success ? $"{e.MbpsAvg:0.#} Mbps" : "—", bg, "#047857", true);
                    BodyCell(table, e.Success ? $"{e.MbpsPeak:0.#} Mbps" : "—", bg, "#334155");
                    BodyCell(table, e.Success ? $"{FormatHelper.Bytes(e.BytesTotal)} / {e.DurationMs / 1000.0:0.#} s" : e.Error, bg, "#475569");
                }
            });
        }
        col.Item().PaddingTop(4).Text("注：下载实测受测速节点、出口拥塞与高峰时段影响，应与链路协商速率、分段延迟画像结合解读。")
            .FontSize(8).FontColor("#94A3B8");
    }

    // ---------- 低速原因分析 ----------
    private static void ComposeSlowCausesSection(ColumnDescriptor col, NetworkReportData data)
    {
        if (data.SlowCauses.Count == 0) return;
        SectionTitle(col, "低速原因分析（按嫌疑度排序）");
        col.Item().PaddingBottom(4).Text(SlowSpeedAnalyzer.Conclusion(data.SlowCauses, data.SpeedTest))
            .FontSize(9.5f).LineHeight(1.6f);
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(24);
                cd.ConstantColumn(46);
                cd.RelativeColumn(2.4f);
                cd.RelativeColumn(3);
            });
            table.Header(h => { HeadCell(h, "序"); HeadCell(h, "嫌疑度"); HeadCell(h, "嫌疑原因"); HeadCell(h, "依据与处置"); });
            int i = 1;
            foreach (var cs in data.SlowCauses)
            {
                var (bg, tc) = cs.Level switch
                {
                    "高" => ("#FEF2F2", "#B91C1C"),
                    "中" => ("#FFFBEB", "#B45309"),
                    _ => ("#FFFFFF", "#475569")
                };
                BodyCell(table, i++.ToString(), bg, tc, true);
                BodyCell(table, cs.Level, bg, tc, true);
                BodyCell(table, cs.Title, bg, "#1E293B");
                var merged = "依据：" + cs.EvidenceText + "\n处置：" + cs.ActionText;
                BodyCell(table, merged, bg, "#334155");
            }
        });
    }

    // ---------- 修复记录 ----------
    private static void ComposeRepairSection(ColumnDescriptor col, NetworkReportData data)
    {
        if (data.Repairs.Count == 0) return;
        SectionTitle(col, "修复记录（一键修复执行结果）");
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(2);
                cd.ConstantColumn(46);
                cd.RelativeColumn(3);
            });
            table.Header(h => { HeadCell(h, "修复动作"); HeadCell(h, "结果"); HeadCell(h, "输出"); });
            foreach (var r in data.Repairs)
            {
                var bg = r.Status == RepairStatus.Fail ? "#FEF2F2" : "#FFFFFF";
                BodyCell(table, r.Title, bg, "#1E293B");
                BodyCell(table, r.StatusText, bg, r.Status == RepairStatus.Ok ? "#047857" : "#B91C1C", true);
                var output = (r.Output ?? "").Replace("\r", " ").Replace("\n", " ");
                if (output.Length > 160) output = output[..160] + "…";
                BodyCell(table, string.IsNullOrEmpty(output) ? "—" : output, bg, "#475569");
            }
        });
    }

    // ---------- 处置建议 ----------
    private static void ComposeAdviceSection(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "四、分级处置建议");
        if (data.Recommendations.Count == 0)
        {
            col.Item().Text("本次无需人工处理。如问题复现，请保留本报告并联系信息中心。").FontSize(9.5f);
            return;
        }
        col.Item().Table(table =>
        {
            table.ColumnsDefinition(cd =>
            {
                cd.ConstantColumn(64);
                cd.RelativeColumn(3);
                cd.ConstantColumn(70);
            });
            table.Header(h => { HeadCell(h, "优先级"); HeadCell(h, "建议措施"); HeadCell(h, "建议对象"); });
            int index = 1;
            foreach (var (priority, text) in data.Recommendations)
            {
                var (pt, pc, pb) = priority switch
                {
                    1 => ("P1 立即", "#B91C1C", "#FEF2F2"),
                    2 => ("P2 今日", "#B45309", "#FFFBEB"),
                    _ => ("P3 观察", "#475569", "#F8FAFC")
                };
                var owner = OwnerOf(text);
                var bg = priority == 1 ? "#FEF2F2" : priority == 2 ? "#FFFBEB" : "#FFFFFF";
                BodyCell(table, $"{index++}. {pt}", bg, pc, true);
                BodyCell(table, text, bg, "#334155");
                BodyCell(table, owner, bg, "#64748B");
            }
        });

        col.Item().PaddingTop(8).Column(c2 =>
        {
            c2.Item().Text("升级路径：物理层/驱动问题 → 本校信息中心或设备供应商；认证过期 → 教体局城域网认证平台重新登录；出口/策略问题 → 教体局信息中心。")
                .FontSize(8.5f).FontColor("#64748B").LineHeight(1.5f);
        });
    }

    private static string OwnerOf(string text)
    {
        if (text.Contains("网线") || text.Contains("驱动") || text.Contains("硬件") || text.Contains("水晶头")) return "机房管理员/报修";
        if (text.Contains("认证") || text.Contains("城域网")) return "任课教师/信息中心";
        if (text.Contains("信息中心") || text.Contains("出口") || text.Contains("策略")) return "教体局信息中心";
        if (text.Contains("路由器") || text.Contains("AP") || text.Contains("交换机")) return "本校信息中心";
        return "本机使用者";
    }

    // ---------- 签名 ----------
    private static void ComposeSignSection(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "五、确认签署");
        col.Item().PaddingTop(4).Row(row =>
        {
            row.RelativeItem().Column(c1 =>
            {
                c1.Item().Text($"报告人：{Empty(data.Meta.Reporter, "__________")}").FontSize(9.5f);
                c1.Item().PaddingTop(14).Text("签名：____________________").FontSize(9.5f);
            });
            row.RelativeItem().Column(c2 =>
            {
                c2.Item().Text($"设备位置：{Empty(data.Meta.Grade, "")}{Empty(data.Meta.ClassName, "")}").FontSize(9.5f);
                c2.Item().PaddingTop(14).Text("日期：______ 年 ____ 月 ____ 日").FontSize(9.5f);
            });
        });
        col.Item().PaddingTop(10).Height(0.5f, Unit.Point).Background("#E2E8F0");
        col.Item().PaddingTop(6).Text("本报告由青枢工具箱在本机自动生成，全部诊断数据仅在本地采集与留存，不包含任何云端上传行为。诊断时间戳以本机系统时钟为准。")
            .FontSize(8).FontColor("#94A3B8").LineHeight(1.5f);
    }
}
