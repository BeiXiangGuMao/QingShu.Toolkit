using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace QingShu.Plugin.NetworkDiag;

// ============================================================================
// 报告域（宿主 Models/Reporting.cs + Services/Reporting/* + Services/HardwareProbeService.cs
// 同源移植）：设备事实探测 / 报告元数据 / QuestPDF 正式报告模板 / 综合推断引擎 / 版本辅助。
// 报告输出目录口径（本批统一改动）：%LocalAppData%\QingShu.Toolkit\reports\yyyy-MM-dd\
// （宿主内置版写「我的文档」目录；插件版统一改本地数据根 reports 子目录，与内置页共用
// 同一报告目录口径，usb 批隔离区同款——报告编号按日累计在两版间互通延续）。
// AppVersion 语义保持宿主（InformationalVersion 优先、数字版本兜底），仅读取目标改为
// 本插件程序集（宿主版读入口程序集；报告页脚版本号标注为插件版本，注释说明）。
// ============================================================================

/// <summary>本机硬件/设备事实（WMI + 注册表，只读取）。</summary>
public sealed class DeviceFacts
{
    public string HostName { get; set; } = Environment.MachineName;
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string CpuName { get; set; } = "";
    public ulong TotalRamBytes { get; set; }
    /// <summary>BIOS 序列号（SMBIOS）</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>Windows MachineGuid（设备唯一标识）</summary>
    public string MachineGuid { get; set; } = "";
    /// <summary>主网卡型号</summary>
    public string NicModel { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string ToolVersion { get; set; } = "";
    public bool Loaded { get; set; }
}

// ================= PDF 报告模型（宿主 Models/Reporting.cs 报告区段同源移植） =================

/// <summary>诊断 PDF 报告元数据。</summary>
public sealed class ReportMeta
{
    /// <summary>报告编号：QS-DIAG-yyyyMMdd-NNN（按日累计次数）</summary>
    public string ReportId { get; set; } = "";
    /// <summary>学校名称</summary>
    public string School { get; set; } = "";
    /// <summary>年级（生成时必填）</summary>
    public string Grade { get; set; } = "";
    /// <summary>班级（生成时必填）</summary>
    public string ClassName { get; set; } = "";
    /// <summary>报告人</summary>
    public string Reporter { get; set; } = "";
    /// <summary>设备编号（默认主机名，可自定义）</summary>
    public string DeviceTag { get; set; } = "";
    /// <summary>设备序列号（SMBIOS）</summary>
    public string SerialNumber { get; set; } = "";
    /// <summary>系统 MachineGuid</summary>
    public string MachineGuid { get; set; } = "";
    public string HostName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string CpuName { get; set; } = "";
    public string TotalRamText { get; set; } = "";
    public string NicModel { get; set; } = "";
    public string IpV4 { get; set; } = "";
    public string MacAddress { get; set; } = "";
    /// <summary>链路协商速率描述（低速专项填写）</summary>
    public string LinkSpeedText { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string ToolVersion { get; set; } = "";
    public string ModeText { get; set; } = "";
}

/// <summary>PDF 报告完整数据。</summary>
public sealed class NetworkReportData
{
    public ReportMeta Meta { get; set; } = new();
    public List<CheckItem> Results { get; set; } = new();
    public List<RepairAction> Repairs { get; set; } = new();
    /// <summary>分优先级建议（1=立即处理 2=今日处理 3=持续观察）</summary>
    public List<(int Priority, string Text)> Recommendations { get; set; } = new();
    /// <summary>下载吞吐实测（低速专项，可能为 null）</summary>
    public SpeedTestResult? SpeedTest { get; set; }
    /// <summary>低速嫌疑原因分析（低速专项）</summary>
    public List<SlowCause> SlowCauses { get; set; } = new();
    /// <summary>网络拓扑快照（诊断实测自动生成；无快照时为 null，报告不渲染拓扑节）。</summary>
    public NetworkTopology? Topology { get; set; }
    /// <summary>上一份报告基线（sidecar JSON 读取；无历史报告时为 null，对比行不渲染）。</summary>
    public ReportBaseline? Baseline { get; set; }
}

// ================= 设备信息探测（宿主 Services/HardwareProbeService.cs 同源移植） =================

public interface IHardwareProbeService
{
    DeviceFacts Facts { get; }
    Task<DeviceFacts> EnsureLoadedAsync();
}

/// <summary>设备信息探测（序列号 / MachineGuid / CPU / 内存 / 网卡），供 PDF 报告使用。</summary>
public sealed class HardwareProbeService : IHardwareProbeService
{
    public DeviceFacts Facts { get; } = new();

    private Task<DeviceFacts>? _loadTask; // 缓存任务而非标志位：避免并发调用拿到半成品数据

    public Task<DeviceFacts> EnsureLoadedAsync()
    {
        if (_loadTask != null) return _loadTask;
        Facts.ToolVersion = AppVersion.DisplayText;
        Facts.HostName = Environment.MachineName;
        var (memLoad, totalPhys, _) = NativeKernel32.GetMemoryStatus();
        Facts.TotalRamBytes = totalPhys;

        _loadTask = Task.Run(async () =>
        {
            try
            {
                // 操作系统
                Facts.OsVersion = $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                    var display = key?.GetValue("DisplayVersion")?.ToString();
                    var name = key?.GetValue("ProductName")?.ToString();
                    if (!string.IsNullOrEmpty(name)) Facts.OsVersion = name! + (string.IsNullOrEmpty(display) ? "" : $" ({display})");
                }
                catch { }
                Facts.OsBuild = Environment.OSVersion.Version.ToString();

                // CPU
                Facts.CpuName = await WmiHelper.FirstValueAsync("SELECT Name FROM Win32_Processor", "Name").ConfigureAwait(false);

                // BIOS 序列号
                Facts.SerialNumber = await WmiHelper.FirstValueAsync("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber").ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(Facts.SerialNumber)) Facts.SerialNumber = "未知";

                // MachineGuid（注册表只读）
                try
                {
                    Facts.MachineGuid = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")?.GetValue("MachineGuid")?.ToString() ?? "";
                }
                catch { }

                // 主网卡
                var adapters = NetworkProbeHelper.GetAllAdapters();
                var primary = adapters.FirstOrDefault(a => a.IsUp && a.Gateway != null) ?? adapters.FirstOrDefault();
                if (primary != null)
                {
                    Facts.NicModel = primary.Description;
                    Facts.MacAddress = await WmiHelper.FirstValueAsync(
                        "SELECT MACAddress FROM Win32_NetworkAdapter WHERE PhysicalAdapter=true AND NetEnabled=true", "MACAddress")
                        .ConfigureAwait(false);
                }
            }
            catch { }
            Facts.Loaded = true;
            return Facts;
        });
        return _loadTask;
    }
}

/// <summary>
/// 版本号辅助（宿主同名静态类同源移植，读取目标改为本插件程序集）：
/// 宿主版经 GetEntryAssembly 读入口程序集版本；插件在宿主进程内运行时入口程序集是宿主，
/// 报告页脚「工具版本」须标注插件自身版本，故改为 GetExecutingAssembly（本插件程序集），
/// 数字版本与 InformationalVersion 展示语义（Beta 阶段带预发布后缀）与宿主完全一致。
/// </summary>
public static class AppVersion
{
    /// <summary>纯数字版本号（机器可读场景一律使用本值）。</summary>
    public static string Text
    {
        get
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"; }
            catch { return "1.0.0"; }
        }
    }

    /// <summary>展示用版本号：Beta 阶段带预发布后缀（取自插件 csproj 的 InformationalVersion）。</summary>
    public static string DisplayText
    {
        get
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var attr = asm is null
                    ? null
                    : System.Attribute.GetCustomAttribute(asm, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                        as System.Reflection.AssemblyInformationalVersionAttribute;
                return string.IsNullOrWhiteSpace(attr?.InformationalVersion) ? Text : attr!.InformationalVersion;
            }
            catch { return Text; }
        }
    }
}

// ================= QuestPDF 报告模板（宿主 Services/Reporting/PdfReportService.cs 同源移植） =================

public interface IPdfReportService
{
    /// <summary>生成正式网络诊断报告 PDF，返回文件完整路径。</summary>
    Task<string> GenerateAsync(NetworkReportData data);
}

/// <summary>
/// QuestPDF 报告模板（正规文档样式）：
/// 页眉品牌条 + 报告编号（按日累计次数）→ 设备与位置信息 → 结论概览 →
/// 分层诊断明细表 → 修复记录 → 综合推断（全报告证据合一的最终判断） →
/// 分级处置建议 → 签名栏 → 页脚页码。
/// </summary>
public sealed class PdfReportService : IPdfReportService
{
    private static int _cjkFontInit;

    /// <summary>
    /// 显式注册中文字体（QuestPDF 2026.9 字体管理变更，历史 bug 二连）。
    /// 第一轮修复（UseSystemFonts = true）后用户实测仍抛 DocumentDrawingException：
    /// "uses font families that are not available: 'Microsoft YaHei'"，且异常信息里
    /// System fonts 已是 enabled —— 说明系统字体枚举在本机未覆盖微软雅黑。
    /// 最大嫌疑：Windows 8.1 起雅黑是 msyh.ttc（TrueType Collection），
    /// QuestPDF/Skia 的字体扫描对 .ttc 支持不完整，导致雅黑始终不可见。
    /// 官方推荐路径 2：FontManager.RegisterFontFromFile 显式注册后按族名引用。
    /// 注册成功后 TextStyle 里的 "Microsoft YaHei" 即可直接匹配（族名匹配忽略大小写）。
    /// </summary>
    private static void EnsureCjkFontsRegistered()
    {
        if (Interlocked.Exchange(ref _cjkFontInit, 1) == 1) return;
        try
        {
            var fontsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            // 顺序即优先级：雅黑（常规/粗体）→ 宋体 → 黑体，任一存在即满足中文渲染；
            // 单个注册失败（被占用/损坏）不影响其余候选。
            string[] candidates = { "msyh.ttc", "msyh.ttf", "msyhbd.ttc", "simsun.ttc", "simhei.ttf" };
            int registered = 0;
            foreach (var name in candidates)
            {
                var path = Path.Combine(fontsDir, name);
                if (!File.Exists(path)) continue;
                try { FontManager.RegisterFontFromFile(path); registered++; } catch { }
            }
            if (registered == 0)
            {
                // 兜底已由宿主启动时的 Settings.UseSystemFonts = true 承担（QuestPDF 全局设置，
                // 经默认加载上下文共享同一份 QuestPDF 实例），此处不再抛出：字体缺失不应阻断报告
                // 其余部分（若系统字体也失效，GeneratePdf 会抛出带明确缺字族名的异常，调用方已有错误提示）。
            }
        }
        catch { }
    }

    public Task<string> GenerateAsync(NetworkReportData data)
        => Task.Run(() =>
        {
            EnsureCjkFontsRegistered();
            // 插件版口径：报告统一落 %LocalAppData%\QingShu.Toolkit\reports\yyyy-MM-dd
            // （宿主内置版写「我的文档\QingShu.Toolkit\Reports」；插件版与宿主数据根对齐，
            // 内置/插件共用同一目录，报告编号按日累计跨两版互通延续，usb 批隔离区同款）。
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingShu.Toolkit", "reports", DateTime.Now.ToString("yyyy-MM-dd"));
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

            // 历史基线对比（插件版新增）：生成前读最近一份 sidecar 作为上一份报告快照渲染进对比行；
            // sidecar 与 PDF 同名（.pdf 后追加 .summary.json），随 PDF 同生共死（顶替删除时同步删除）。
            data.Baseline = ReadLatestBaseline(dir);

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(40);
                    page.DefaultTextStyle(x => x.FontFamily("Microsoft YaHei").FontSize(9.5f).FontColor("#1D1D1F").LineHeight(1.5f));

                    page.Header().Element(c => ComposeHeader(c, data));
                    page.Content().Element(c => ComposeContent(c, data));
                    page.Footer().Row(row =>
                    {
                        row.RelativeItem().Text($"青枢工具箱 v{data.Meta.ToolVersion} 自动生成 · 报告编号 {reportId}")
                            .FontSize(8).FontColor("#8E8E93");
                        row.AutoItem().Width(120, Unit.Point).AlignRight().Text(t =>
                        {
                            t.DefaultTextStyle(x => x.FontSize(8).FontColor("#8E8E93"));
                            t.Span("第 ");
                            t.CurrentPageNumber();
                            t.Span(" 页 / 共 ");
                            t.TotalPages();
                            t.Span(" 页");
                        });
                    });
                });
            }).GeneratePdf(file);
            WriteBaselineSidecar(file, data);

            return file;
        });

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "未命名设备" : name;
    }

    // ---------- 历史基线对比（sidecar JSON 摘要，随报告自动维护） ----------

    /// <summary>
    /// 读取最近一份报告 sidecar（递归全部日期目录按修改时间取最新）。
    /// 无历史或单份损坏时跳过继续找更早的，全部不可用返回 null——对比是增强不是主流程，
    /// 任何 sidecar 异常不得影响 PDF 报告本体的生成与交付。
    /// </summary>
    private static ReportBaseline? ReadLatestBaseline(string reportRoot)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(reportRoot, "*.pdf.summary.json", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime);
            foreach (var f in candidates)
            {
                try
                {
                    var json = File.ReadAllText(f.FullName);
                    var b = JsonSerializer.Deserialize<ReportBaseline>(json, BaselineJsonOptions);
                    if (b != null && !string.IsNullOrEmpty(b.ReportId)) return b;
                }
                catch { } // 单份损坏跳过，继续找更早的
            }
        }
        catch { }
        return null;
    }

    /// <summary>把本次报告的计数与评分写入 sidecar（供下一份报告对比基线；失败不影响报告交付）。</summary>
    private static void WriteBaselineSidecar(string pdfPath, NetworkReportData data)
    {
        try
        {
            int pass = 0, warn = 0, fail = 0, fixedN = 0;
            foreach (var r in data.Results)
            {
                switch (r.Status)
                {
                    case CheckStatus.Pass: pass++; break;
                    case CheckStatus.Warning: warn++; break;
                    case CheckStatus.Fail: fail++; break;
                    case CheckStatus.Fixed: fixedN++; break;
                }
            }
            var snapshot = new ReportBaseline
            {
                ReportId = data.Meta.ReportId,
                GeneratedAt = data.Meta.GeneratedAt,
                ModeText = data.Meta.ModeText,
                Pass = pass + fixedN,   // 与报告 PassCount 口径一致（已修复并入通过）
                Warn = warn,
                Fail = fail,
                Fixed = fixedN,
                Score = ReportScoring.Calculate(data.Results).Score
            };
            File.WriteAllText(pdfPath + ".summary.json", JsonSerializer.Serialize(snapshot, BaselineJsonOptions));
        }
        catch { } // sidecar 失败不影响 PDF 交付，只是下一份少了对比
    }

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // ================= 页眉 =================
    private static void ComposeHeader(IContainer c, NetworkReportData data)
    {
        // QuestPDF 单子容器约束：一个 IContainer 只能赋一个子元素。
        // 品牌条与分隔线两个兄弟节点必须包进同一个 Column（col.Item() 每次开新槽位），
        // 不得对 c 连续调用两次链式赋值（否则运行时抛 DocumentComposeException）。
        c.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.AutoItem().Width(4, Unit.Point).Height(34, Unit.Point)
                    .Background("#007AFF");
                row.RelativeItem().PaddingLeft(10).Column(inner =>
                {
                    inner.Item().Text("网络故障诊断报告").FontSize(17).SemiBold().FontColor("#1D1D1F");
                    inner.Item().Text("青枢工具箱 QingShu.Toolkit · 校园网络专项诊断").FontSize(9).FontColor("#6E6E73");
                });
                row.RelativeItem().AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(data.Meta.ReportId).FontSize(10).SemiBold().FontColor("#007AFF");
                    right.Item().AlignRight().Text($"生成时间 {data.Meta.GeneratedAt:yyyy-MM-dd HH:mm}").FontSize(8).FontColor("#8E8E93");
                });
            });
            // 分隔线（LineHorizontal 返回 void，用 1pt 高色带等效实现）
            col.Item().PaddingTop(6).Height(1, Unit.Point).Background("#E5E5EA");
        });
    }

    // ================= 正文 =================
    private static void ComposeContent(IContainer c, NetworkReportData data)
    {
        c.PaddingTop(12).Column(col =>
        {
            ComposeInfoSection(col, data);
            ComposeSummarySection(col, data);
            ComposeTopologySnapshot(col, data);
            ComposeSpeedSection(col, data);
            ComposeDetailSections(col, data);
            ComposeSlowCausesSection(col, data);
            ComposeRepairSection(col, data);
            ComposeInferenceSection(col, data);
            ComposeAdviceSection(col, data);
            ComposeSignSection(col, data);
        });
    }

    private static void SectionTitle(ColumnDescriptor col, string title)
    {
        col.Item().PaddingTop(14).PaddingBottom(6).Row(r =>
        {
            r.AutoItem().Width(3, Unit.Point).Height(12, Unit.Point).Background("#007AFF").PaddingTop(0);
            r.RelativeItem().PaddingLeft(6).Text(title).FontSize(12.5f).SemiBold().FontColor("#1D1D1F");
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
            t.Span("诊断模式：").FontColor("#6E6E73");
            t.Span($"{m.ModeText}    ").SemiBold();
            if (!string.IsNullOrWhiteSpace(m.LinkSpeedText))
                t.Span($"链路速率：{m.LinkSpeedText}    ").FontColor("#6E6E73");
            t.Span($"起止：{m.StartedAt:HH:mm:ss} — {m.FinishedAt:HH:mm:ss}").FontColor("#6E6E73");
        });
        ComposeTopologyNote(col);
    }

    // ---------- 校园网络拓扑与压力点说明 ----------
    // 学校真实链路（用户提供）：班级/老师电脑 →（部分班级经中转设备，疑似光猫）→ 楼宇软路由
    // → 科技楼服务器 → 裸光纤 → 城域网；少量电脑直连科技楼服务器。
    // 软路由与服务器为整栋楼/全校共享压力点，诊断分段与建议均按此拓扑解读。
    private static void ComposeTopologyNote(ColumnDescriptor col)
    {
        // 顺序约定：PaddingTop(10) 是与上文的外间距（不带底色）→ Background 覆盖卡片
        // → Padding 再做内边距（底色延伸到内边距，文字与色块边缘有留白）
        col.Item().PaddingTop(10).Background("#F5F5F7")
            .PaddingVertical(8).PaddingHorizontal(10).Column(top =>
        {
            top.Item().Text("校园网络拓扑与压力点说明").FontSize(9.5f).SemiBold().FontColor("#1D1D1F");
            top.Item().PaddingTop(4).Text("本校链路：班级/教师电脑 →（部分班级经中转设备，常见为光猫或交换机）→ 楼宇软路由 → 科技楼服务器 → 裸光纤 → 城域网；另有少量电脑直连科技楼服务器。楼宇软路由与科技楼服务器为整栋楼/全校共享压力点，同一时段并发越多，接入段与核心段越容易成为瓶颈。")
                .FontSize(8.5f).FontColor("#48484A").LineHeight(1.5f);
            top.Item().PaddingTop(4).Text("分段对应关系：「分段延迟画像」按此拓扑逐跳定位——接入段（本机 → 直连网关，即班级中转设备或楼宇软路由）、核心段（楼宇软路由 → 科技楼服务器）、出口段（服务器 → 裸光纤 → 城域网）；处置建议与责任归属亦按此三段划分。")
                .FontSize(8.5f).FontColor("#48484A").LineHeight(1.5f);
            top.Item().PaddingTop(4).Text("测速说明：诊断中的下载测速为短时抽样（限时长与并发），会短暂占用少量出口带宽；建议避开上课高峰运行，避免叠加楼宇软路由与科技楼服务器的共享压力。")
                .FontSize(8).FontColor("#6E6E73").LineHeight(1.5f);
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
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E5EA").PaddingVertical(3.5f)
                    .Text(k).FontSize(8.5f).FontColor("#6E6E73");
                table.Cell().BorderBottom(0.5f).BorderColor("#E5E5EA").PaddingVertical(3.5f)
                    .Text(v).FontSize(8.5f).FontColor("#1D1D1F");
            }
        });
    }

    // ---------- 结论概览 ----------
    private static void ComposeSummarySection(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "二、诊断结论概览");
        var score = ReportScoring.Calculate(data.Results);
        // 健康评分（插件版新增）：0-100 单值 + 四档评级，扣分规则透明可复核（右侧列明扣分明细）
        col.Item().PaddingTop(8).Row(row =>
        {
            row.AutoItem().Background(score.Bg).Border(0.8f).BorderColor(score.Color)
                .PaddingVertical(7).PaddingHorizontal(12).AlignMiddle().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(17).SemiBold().FontColor(score.Color));
                    t.Span($"{score.Score}");
                    t.Span(" / 100").FontSize(9).FontColor("#6E6E73");
                });
            row.RelativeItem().PaddingLeft(12).Column(c =>
            {
                c.Item().Text($"健康评级：{score.Grade}（评分 = 100 - 未通过×15 - 警告×5；已修复不扣分，跳过不计入）")
                    .FontSize(9).SemiBold().FontColor("#1D1D1F");
                c.Item().PaddingTop(2).Text($"扣分明细：{score.DeductionLine}").FontSize(8.5f).FontColor("#6E6E73");
            });
        });
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
            Chip(row, "#248A3D", "#E9F9EE", "通过", pass);
            Chip(row, "#C93400", "#FFF4E5", "警告", warn);
            Chip(row, "#D70015", "#FFEBEA", "未通过", fail);
            Chip(row, "#0062CC", "#E8F2FF", "已修复", fixedCount);
            Chip(row, "#6E6E73", "#F5F5F7", "跳过", skip);
        });

        string conclusion;
        if (fail == 0 && warn == 0)
        {
            // 口径准确性：跳过项是「环境不适用」而非「检查通过」，结论必须区分列出数与实检数，
            // 避免快速体检/有线环境读者误以为全部项目都已检查。
            var checkedN = pass + fixedCount;
            conclusion = skip > 0
                ? $"本次诊断共列出 {checkedN + skip} 项，实际检查 {checkedN} 项，另有 {skip} 项因环境不适用自动跳过（如有线环境跳过 Wi-Fi 类检查），未发现影响校园网络使用的故障，网络处于健康状态。"
                : "本次诊断共检查全部项目，未发现影响校园网络使用的故障，网络处于健康状态。";
        }
        else
        {
            if (fail == 0)
                conclusion = $"本次诊断发现 {warn} 项警告，网络当前可用但存在隐患，建议按处置建议在今日内处理。";
            else
                conclusion = $"本次诊断发现 {fail} 项故障、{warn} 项警告。" +
                    (fixedCount > 0 ? $"其中 {fixedCount} 项已尝试自动修复。" : "") +
                    "未通过的故障项请按“四、分级处置建议”逐项处理，处理后可重新运行诊断复核。";
            if (skip > 0)
                conclusion += $"另有 {skip} 项因环境不适用自动跳过，未计入统计。";
        }
        conclusion += "全部证据的最终综合判断见下方「综合推断（最终判断）」一节。";
        col.Item().PaddingTop(8).PaddingBottom(2).Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.6f).FontColor("#1D1D1F"));
            t.Span($"本次健康评分 {score.Score} 分（{score.Grade}）。")
                .SemiBold().FontColor(score.Color);
            t.Span(conclusion);
        });

        // 历史基线对比（插件版新增）：与最近一份 sidecar 快照对比计数与评分，量化「变好/变坏」；
        // 首份报告无历史基线时不渲染本行。方向以评分为准（计数明细随行列出）。
        if (data.Baseline is { } baseLine)
        {
            int passNow = pass + fixedCount;
            var (dirWord, dirColor) = score.Score > baseLine.Score ? ("较上一份改善", "#248A3D")
                : score.Score < baseLine.Score ? ("较上一份下降", "#D70015") : ("与上一份持平", "#6E6E73");
            col.Item().PaddingTop(6).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(8.8f).LineHeight(1.55f).FontColor("#48484A"));
                t.Span("与上一份报告对比：").SemiBold().FontColor("#1D1D1F");
                t.Span($"（{Empty(baseLine.ReportId, "历史报告")}，{baseLine.GeneratedAt:MM-dd HH:mm}" +
                       (string.IsNullOrEmpty(baseLine.ModeText) ? "" : $"，{baseLine.ModeText}") + "）");
                t.Span($"通过 {baseLine.Pass}→{passNow} · 警告 {baseLine.Warn}→{warn} · 未通过 {baseLine.Fail}→{fail} · 评分 {baseLine.Score}→{score.Score} ");
                t.Span($"（{dirWord}）").SemiBold().FontColor(dirColor);
            });
        }
    }

    private static void Chip(RowDescriptor row, string textColor, string bgColor, string label, int count)
    {
        row.AutoItem().PaddingRight(6).Background(bgColor).Border(0.5f).BorderColor(bgColor)
            .PaddingVertical(5).PaddingHorizontal(10).Text($"{label} {count}")
            .FontSize(9.5f).SemiBold().FontColor(textColor);
    }

    // ---------- 网络拓扑快照（诊断实测自动生成） ----------
    // 与诊断页 Canvas 拓扑同一份数据（NetworkTopologyBuilder 构建）：主链横排节点框 +
    // 健康着色连线，DNS/认证门户作出口分支行；未诊断（无快照）时整节不渲染。
    private static void ComposeTopologySnapshot(ColumnDescriptor col, NetworkReportData data)
    {
        var topo = data.Topology;
        if (topo is not { Nodes.Count: > 0 }) return;

        SectionTitle(col, "网络拓扑快照（本次诊断实测自动生成）");
        col.Item().PaddingTop(2).Text(topo.SummaryText).FontSize(9).LineHeight(1.5f).FontColor("#48484A");

        // 主链：本机 → 网关 → 内网跳(≤3) → 校园出口 → 互联网（构建器按链序产出节点，取链上角色）
        var chain = topo.Nodes.Where(n => n.Role is TopoRole.Local or TopoRole.Gateway
            or TopoRole.InnerHop or TopoRole.Exit or TopoRole.Internet).ToList();
        var edges = topo.Edges.ToDictionary(e => (e.From, e.To));
        col.Item().PaddingTop(4).Row(row =>
        {
            for (int i = 0; i < chain.Count; i++)
            {
                if (i > 0)
                {
                    var prev = chain[i - 1].Key;
                    var cur = chain[i].Key;
                    row.ConstantItem(13).AlignMiddle()
                        .Element(cc => ConnectorBand(cc, edges.TryGetValue((prev, cur), out var e) ? e : null));
                }
                row.RelativeItem().Element(cc => TopoNodeBox(cc, chain[i]));
            }
        });

        // 出口分支：DNS 服务器 / 认证门户（逻辑上自「校园出口」引出，与页面拓扑分支一致）
        var branches = topo.Nodes.Where(n => n.Role is TopoRole.Dns or TopoRole.Portal).ToList();
        if (branches.Count > 0)
        {
            col.Item().PaddingTop(7).Text("出口分支（自「校园出口」引出：DNS 解析与城域网认证门户）")
                .FontSize(8).FontColor("#6E6E73");
            col.Item().PaddingTop(3).Row(row =>
            {
                for (int i = 0; i < branches.Count; i++)
                {
                    if (i > 0) row.ConstantItem(10);
                    row.RelativeItem().Element(cc => TopoNodeBox(cc, branches[i]));
                }
            });
        }

        col.Item().PaddingTop(7).Text(
                "连线颜色 = 该段实测健康度：绿 正常 / 黄 瓶颈 / 红 异常；灰白虚线 = 无线链路或未实测段。" +
                $"生成来源：{topo.BuiltFrom}")
            .FontSize(8).FontColor("#8E8E93").LineHeight(1.5f);
    }

    /// <summary>链路连线带：按边健康着色；虚线边（无线/未实测）分段留白，与页面拓扑图例一致。</summary>
    private static void ConnectorBand(IContainer c, TopoEdge? edge)
    {
        var color = edge?.Health switch
        {
            TopoHealth.Ok => "#248A3D",
            TopoHealth.Warn => "#C93400",
            TopoHealth.Fail => "#D70015",
            _ => "#C7C7CC"
        };
        bool dashed = edge?.Dashed ?? false;
        c.Row(rr =>
        {
            for (int i = 0; i < 7; i++)
            {
                var seg = rr.ConstantItem(1.7f).Height(1.1f);
                if (!dashed || i % 2 == 0) seg.Background(color); // 虚线奇数段留白占位
            }
        });
    }

    /// <summary>拓扑节点框：徽标 + 标题 + 地址 + 状态一句话，边框/状态色按健康档位（与结论概览色板一致）。</summary>
    private static void TopoNodeBox(IContainer c, TopoNode n)
    {
        var (color, bg) = n.Health switch
        {
            TopoHealth.Ok => ("#248A3D", "#E9F9EE"),
            TopoHealth.Warn => ("#C93400", "#FFF4E5"),
            TopoHealth.Fail => ("#D70015", "#FFEBEA"),
            _ => ("#6E6E73", "#F5F5F7")
        };
        c.Border(0.8f).BorderColor(color).Background(bg).PaddingVertical(3.5f).PaddingHorizontal(4)
            .Column(box =>
            {
                box.Item().Text(n.Badge).FontSize(6.3f).FontColor("#6E6E73");
                box.Item().Text(string.IsNullOrWhiteSpace(n.Title) ? "—" : n.Title)
                    .FontSize(7.3f).SemiBold().FontColor("#1D1D1F").LineHeight(1.25f);
                box.Item().Text(string.IsNullOrWhiteSpace(n.Subtitle) ? "—" : n.Subtitle)
                    .FontSize(6.3f).FontColor("#48484A").LineHeight(1.3f);
                if (!string.IsNullOrWhiteSpace(n.StatusText))
                    box.Item().Text(n.StatusText).FontSize(6.3f).FontColor(color).LineHeight(1.3f);
            });
    }

    // ---------- 分层明细 ----------
    private static void ComposeDetailSections(ColumnDescriptor col, NetworkReportData data)
    {
        SectionTitle(col, "三、诊断明细");
        var groups = data.Results.GroupBy(r => r.Category).ToList();
        foreach (var g in groups)
        {
            col.Item().PaddingTop(6).Text(CheckCategoryText.Of(g.Key)).FontSize(10).SemiBold().FontColor("#48484A");

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
                        CheckStatus.Fail => "#FFEBEA",
                        CheckStatus.Warning => "#FFF4E5",
                        _ => "#FFFFFF"
                    };
                    BodyCell(table, StatusText(r.Status), bg, StatusColor(r.Status), true);
                    BodyCell(table, r.Name, bg, "#1D1D1F");
                    BodyCell(table, string.IsNullOrWhiteSpace(r.Summary) ? "—" : r.Summary, bg, "#48484A");
                    BodyCell(table, string.IsNullOrWhiteSpace(r.Suggestion) ? "—" : r.Suggestion, bg, "#6E6E73");
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
        CheckStatus.Pass or CheckStatus.Fixed => "#248A3D",
        CheckStatus.Warning => "#C93400",
        CheckStatus.Fail => "#D70015",
        _ => "#6E6E73"
    };

    private static void HeadCell(TableCellDescriptor t, string text)
    {
        t.Cell().Background("#007AFF").PaddingVertical(4).PaddingHorizontal(5)
            .Text(text).FontSize(8.5f).SemiBold().FontColor("#FFFFFF");
    }

    private static void BodyCell(TableDescriptor t, string text, string bg, string color, bool semiBold = false)
    {
        var d = t.Cell().Background(bg).BorderBottom(0.5f).BorderColor("#E5E5EA")
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
                .FontSize(9.5f).FontColor("#C93400");
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
                    var bg = e.Success ? "#FFFFFF" : "#FFF4E5";
                    BodyCell(table, e.Label, bg, "#1D1D1F");
                    BodyCell(table, e.Success ? $"{e.MbpsAvg:0.#} Mbps" : "—", bg, "#248A3D", true);
                    BodyCell(table, e.Success ? $"{e.MbpsPeak:0.#} Mbps" : "—", bg, "#48484A");
                    BodyCell(table, e.Success ? $"{FormatHelper.Bytes(e.BytesTotal)} / {e.DurationMs / 1000.0:0.#} s" : e.Error, bg, "#6E6E73");
                }
            });
        }
        col.Item().PaddingTop(4).Text("注：下载实测受测速节点、出口拥塞与高峰时段影响，应与链路协商速率、分段延迟画像结合解读。")
            .FontSize(8).FontColor("#8E8E93");
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
                    "高" => ("#FFEBEA", "#D70015"),
                    "中" => ("#FFF4E5", "#C93400"),
                    _ => ("#FFFFFF", "#6E6E73")
                };
                BodyCell(table, i++.ToString(), bg, tc, true);
                BodyCell(table, cs.Level, bg, tc, true);
                BodyCell(table, cs.Title, bg, "#1D1D1F");
                var merged = "依据：" + cs.EvidenceText + "\n处置：" + cs.ActionText;
                BodyCell(table, merged, bg, "#48484A");
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
                var bg = r.Status == RepairStatus.Fail ? "#FFEBEA" : "#FFFFFF";
                BodyCell(table, r.Title, bg, "#1D1D1F");
                BodyCell(table, r.StatusText, bg, r.Status == RepairStatus.Ok ? "#248A3D" : "#D70015", true);
                var output = (r.Output ?? "").Replace("\r", " ").Replace("\n", " ");
                if (output.Length > 160) output = output[..160] + "…";
                BodyCell(table, string.IsNullOrEmpty(output) ? "—" : output, bg, "#6E6E73");
            }
        });
    }

    // ---------- 综合推断（全报告证据合一的最终判断） ----------
    // ReportInference 把结论概览/诊断明细/拓扑快照/网速实测/低速原因/修复记录的
    // 分散结论合成为一段统一结论：判定横幅 + 跨节证据链 + 交叉验证 + 责任归属。
    private static void ComposeInferenceSection(ColumnDescriptor col, NetworkReportData data)
    {
        var inf = ReportInference.Build(data);
        SectionTitle(col, "综合推断（最终判断）");

        var (fg, bg) = inf.Level switch
        {
            2 => ("#D70015", "#FFEBEA"),
            1 => ("#C93400", "#FFF4E5"),
            _ => ("#248A3D", "#E9F9EE")
        };
        col.Item().Background(bg).Border(0.8f).BorderColor(fg)
            .PaddingVertical(7).PaddingHorizontal(10)
            .Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.6f));
                t.Span($"【{inf.Badge}】 ").SemiBold().FontColor(fg);
                t.Span(inf.Verdict).FontColor("#1D1D1F");
            });

        col.Item().PaddingTop(7).Text("推断依据（跨节证据链，逐条注明来源）")
            .FontSize(9).SemiBold().FontColor("#48484A");
        col.Item().PaddingTop(2).Column(ev =>
        {
            foreach (var e in inf.Evidence)
                ev.Item().PaddingVertical(1.5f).Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8.8f).LineHeight(1.55f).FontColor("#48484A"));
                    t.Span("• ").FontColor(fg).SemiBold();
                    t.Span(e);
                });
        });

        if (!string.IsNullOrWhiteSpace(inf.CrossCheck))
            col.Item().PaddingTop(6).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(8.8f).LineHeight(1.55f));
                t.Span("交叉验证：").SemiBold().FontColor("#1D1D1F");
                t.Span(inf.CrossCheck).FontColor("#48484A");
            });

        col.Item().PaddingTop(4).Text(t =>
        {
            t.DefaultTextStyle(x => x.FontSize(8.8f).LineHeight(1.55f));
            t.Span("责任归属与下一步：").SemiBold().FontColor("#1D1D1F");
            t.Span(inf.Responsibility).FontColor("#48484A");
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
                    1 => ("P1 立即", "#D70015", "#FFEBEA"),
                    2 => ("P2 今日", "#C93400", "#FFF4E5"),
                    _ => ("P3 观察", "#6E6E73", "#F5F5F7")
                };
                var owner = OwnerOf(text);
                var bg = priority == 1 ? "#FFEBEA" : priority == 2 ? "#FFF4E5" : "#FFFFFF";
                BodyCell(table, $"{index++}. {pt}", bg, pc, true);
                BodyCell(table, text, bg, "#48484A");
                BodyCell(table, owner, bg, "#6E6E73");
            }
        });

        col.Item().PaddingTop(8).Column(c2 =>
        {
            c2.Item().Text("升级路径：物理层/驱动问题 → 本校信息中心或设备供应商；认证过期 → 教体局城域网认证平台重新登录；出口/策略问题 → 教体局信息中心。")
                .FontSize(8.5f).FontColor("#6E6E73").LineHeight(1.5f);
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
        col.Item().PaddingTop(10).Height(0.5f, Unit.Point).Background("#E5E5EA");
        col.Item().PaddingTop(6).Text("本报告由青枢工具箱在本机自动生成，全部诊断数据仅在本地采集与留存，不包含任何云端上传行为。诊断时间戳以本机系统时钟为准。")
            .FontSize(8).FontColor("#8E8E93").LineHeight(1.5f);
    }
}

// ================= 报告健康评分（插件版新增，纯函数无 IO） =================

// 动机：结论概览此前只有「通过/警告/未通过」计数，教师与信息中心需要自行换算严重程度。
// 评分把计数折算成 0-100 单一数值 + 四档评级，扣分规则透明可复核（报告中列明扣分明细）：
//   未通过 -15/项（阻断性故障）、警告 -5/项（隐患）、已修复不扣分（已闭环）、跳过不计入。
// 评分随 PDF 与 sidecar 落盘，供历史基线对比（与上一份报告的「改善/下降/持平」判定）。

/// <summary>健康评分结果（全部字段直接可渲染）。</summary>
public sealed class HealthScore
{
    public int Score { get; init; }
    /// <summary>评级：优 / 良 / 中 / 差。</summary>
    public string Grade { get; init; } = "";
    /// <summary>评级前景色（优 绿 / 良 蓝 / 中 橙 / 差 红，与报告色板一致）。</summary>
    public string Color { get; init; } = "";
    /// <summary>评级背景色（同上）。</summary>
    public string Bg { get; init; } = "";
    /// <summary>扣分明细一行（无扣分项时为「无扣分项」）。</summary>
    public string DeductionLine { get; init; } = "";
}

public static class ReportScoring
{
    /// <summary>未通过扣分/项（阻断性故障）。</summary>
    public const int PenaltyFail = 15;
    /// <summary>警告扣分/项（隐患）。</summary>
    public const int PenaltyWarn = 5;

    public static HealthScore Calculate(IReadOnlyList<CheckItem> results)
    {
        int fail = 0, warn = 0, fixedN = 0;
        foreach (var r in results)
        {
            switch (r.Status)
            {
                case CheckStatus.Fail: fail++; break;
                case CheckStatus.Warning: warn++; break;
                case CheckStatus.Fixed: fixedN++; break;
            }
        }
        int score = Math.Clamp(100 - fail * PenaltyFail - warn * PenaltyWarn, 0, 100);
        var (grade, color, bg) = score switch
        {
            >= 90 => ("优", "#248A3D", "#E9F9EE"),
            >= 75 => ("良", "#0062CC", "#E8F2FF"),
            >= 60 => ("中", "#C93400", "#FFF4E5"),
            _ => ("差", "#D70015", "#FFEBEA")
        };
        var parts = new List<string>();
        if (fail > 0) parts.Add($"未通过 {fail} 项（-{fail * PenaltyFail}）");
        if (warn > 0) parts.Add($"警告 {warn} 项（-{warn * PenaltyWarn}）");
        var deduction = parts.Count == 0 ? "无扣分项" : string.Join(" · ", parts);
        if (fixedN > 0) deduction += $"；已修复 {fixedN} 项不扣分（已闭环）";
        return new HealthScore { Score = score, Grade = grade, Color = color, Bg = bg, DeductionLine = deduction };
    }
}

/// <summary>上一份报告基线快照（sidecar JSON，与 PDF 同名 .pdf.summary.json，同生共死）。</summary>
public sealed class ReportBaseline
{
    public string ReportId { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public string ModeText { get; set; } = "";
    /// <summary>通过数（已修复并入，与报告 PassCount 口径一致）。</summary>
    public int Pass { get; set; }
    public int Warn { get; set; }
    public int Fail { get; set; }
    public int Fixed { get; set; }
    public int Score { get; set; }
}

// ================= 报告综合推断引擎（宿主 Services/Reporting/ReportInference.cs 同源移植） =================

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
        int skipN = results.Count(r => r.Status == CheckStatus.Skipped);

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
                      (skipN > 0 ? $"，另有 {skipN} 项因环境不适用自动跳过" : "") +
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
        // IP 冲突专项归属（准确性）：冲突实锤/历史记录均属同网段地址占用（网络侧问题），
        // 与本机网卡硬件和链路无关；先于其他交叉验证呈现，避免读者按责任环节误判为本机故障。
        var ipConflict = results.FirstOrDefault(r => r.Id == "ip.conflict" && r.Status is CheckStatus.Fail or CheckStatus.Warning);
        if (ipConflict != null)
            notes.Add(ipConflict.Status == CheckStatus.Fail
                ? "IP 冲突已实锤：属同网段地址占用问题，与本机网卡硬件和链路无关——本机侧改回自动获取 IP 即可避开，网络侧需信息中心定位占用设备。"
                : "系统历史冲突记录说明本网段存在间歇性地址占用：与本机硬件无关，建议信息中心核对地址分配，本机改回自动获取 IP 避开占用。");
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
