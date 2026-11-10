using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace QingShu.Plugin.NetworkDiag;

/// <summary>
/// 网络诊断页 VM（宿主 ViewModels/NetworkDiagViewModel 同源移植）：傻瓜模式一站式“诊断 → 修复 → 导出报告”。
/// 适配点：诊断/修复/报告三服务与本插件实现对接；宿主设置服务 → NetworkDiagSettingsStore
/// （Last* 五项读写 store + 落盘，诊断设置镜像在开始诊断前刷新一次）；宿主日志服务 →
/// Action&lt;string, string&gt;；LogLocationText 由插件宿主数据根 + \logs 经构造函数传入；
/// 硬件探测（报告设备事实）经构造函数注入本插件实现。其余逻辑/护栏逐字保留：
/// 诊断与修复取消源分立、修复成功转 Fixed 状态、open_portal 不翻转、winsock 标注重启生效、
/// 修复后重建拓扑、修复提权进行中禁止重开诊断等。
/// </summary>
public partial class NetworkDiagViewModel : ObservableObject
{
    private readonly INetworkDiagnosticService _diag;
    private readonly INetworkRepairService _repair;
    private readonly IPdfReportService _pdf;
    private readonly NetworkDiagSettingsStore _settings;
    private readonly IHardwareProbeService _probe;
    private readonly Action<string, string> _log; // (level, message) → 插件日志适配（宿主日志服务同职责）

    private DiagnosticSessionReport? _lastReport;
    private CheckContext? _lastContext;
    // 报告顶替语义（插件版新增）：本诊断会话内自动生成的兜底报告路径（诊断后/修复后各一份）；
    // 手动补全信息的正式报告生成成功后移除这些兜底文件，正式版顶替，防上报时兜底版与正式版并列混淆。
    // 新诊断开始即清空：上一会话的自动版转为历史留档，不追溯删除；VM 每次导航新建，天然会话级。
    private readonly List<string> _autoReportPaths = new();
    private string _topoModeText = ""; // 拓扑卡「生成来源」用的模式名（修复后重建时复用）
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _ctsRepair; // 修复专用：与诊断分立，取消互不误伤

    public ObservableCollection<CheckItem> Items { get; } = new();
    public ObservableCollection<RepairAction> RepairActions { get; } = new();
    /// <summary>低速专项：嫌疑原因清单（按嫌疑度降序）。</summary>
    public ObservableCollection<SlowCause> SlowCauses { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _modeIndex; // 0=全面诊断 1=快速体检 2=网速与低速分析
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private int _passCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private int _fixedCount;
    [ObservableProperty] private int _skipCount;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _isRepairing;

    /// <summary>修复执行中的取消按钮可见性（bool，视图层经本地转换器转 Visibility）。</summary>
    public bool RepairCancelVis => IsRepairing;

    partial void OnIsRepairingChanged(bool value) => OnPropertyChanged(nameof(RepairCancelVis));
    [ObservableProperty] private string _repairHint = "";
    [ObservableProperty] private string _lastReportPath = "";
    [ObservableProperty] private bool _reportReady;
    /// <summary>报告强制性生成（插件版新增）：诊断/修复完成后自动生成的结果提示（成功路径或失败原因）。</summary>
    [ObservableProperty] private string _autoReportMessage = "";
    [ObservableProperty] private bool _autoReportFailed;
    public bool AutoReportVis => !string.IsNullOrEmpty(AutoReportMessage);

    partial void OnAutoReportMessageChanged(string value) => OnPropertyChanged(nameof(AutoReportVis));
    [ObservableProperty] private string _sessionText = "";
    // ---- 网络拓扑（诊断后自动生成，页面 Canvas 渲染） ----
    [ObservableProperty] private NetworkTopology _topology = NetworkTopology.Empty;

    /// <summary>有节点即显示拓扑卡（Empty 快照 0 节点 → 隐藏）。</summary>
    public bool HasTopology => Topology.Nodes.Count > 0;
    public string TopologySummary => Topology.SummaryText;
    public string TopologyBuiltText => Topology.BuiltFrom;

    partial void OnTopologyChanged(NetworkTopology value)
    {
        OnPropertyChanged(nameof(HasTopology));
        OnPropertyChanged(nameof(TopologySummary));
        OnPropertyChanged(nameof(TopologyBuiltText));
    }
    // ---- 低速专项实时测速（约 400ms 一拍） ----
    [ObservableProperty] private string _speedPhaseText = "尚未测速";
    [ObservableProperty] private string _speedMbpsText = "—";
    [ObservableProperty] private string _speedSubText = "选择「网速与低速分析」模式后点击开始";
    [ObservableProperty] private bool _hasSlowCauses;
    [ObservableProperty] private string _slowConclusionText = "";

    public string LogLocationText { get; }

    // ---- 页面展示辅助 ----
    public string PassCountText => $"通过 {PassCount}";
    public string WarnCountText => $"警告 {WarnCount}";
    public string FailCountText => $"未通过 {FailCount}";
    public string FixedCountText => $"已修复 {FixedCount}";
    public bool FixedChipVis => FixedCount > 0;
    public double ProgressMax => Math.Max(1, Items.Count);
    public bool SessionTextVis => !string.IsNullOrEmpty(SessionText);
    public bool CanRepair => RepairActions.Any(a => a.IsSelected) && !IsRepairing;
    /// <summary>测速卡仅在网速与低速分析模式显示。</summary>
    public bool HasSpeedCard => ModeIndex == 2;

    partial void OnModeIndexChanged(int value) => OnPropertyChanged(nameof(HasSpeedCard));

    partial void OnPassCountChanged(int value) => OnPropertyChanged(nameof(PassCountText));
    partial void OnWarnCountChanged(int value) => OnPropertyChanged(nameof(WarnCountText));
    partial void OnFailCountChanged(int value) => OnPropertyChanged(nameof(FailCountText));
    partial void OnFixedCountChanged(int value)
    {
        OnPropertyChanged(nameof(FixedCountText));
        OnPropertyChanged(nameof(FixedChipVis));
    }

    public NetworkDiagViewModel(INetworkDiagnosticService diag, INetworkRepairService repair,
        IPdfReportService pdf, NetworkDiagSettingsStore settings, IHardwareProbeService probe,
        Action<string, string> log, string logLocationText)
    {
        _diag = diag;
        _repair = repair;
        _pdf = pdf;
        _settings = settings;
        _probe = probe;
        _log = log;
        LogLocationText = logLocationText;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (IsRepairing) return; // 修复提权进行中禁止重开诊断：避免并发操作/UAC 弹窗交叠与列表竞态
        _settings.RefreshDiagFromHost(); // 插件适配：开始诊断前刷新宿主诊断设置镜像（门户/DNS/测速参数等）
        IsRunning = true;
        HasRun = false;
        ReportReady = false;
        _autoReportPaths.Clear(); // 新诊断周期：上一会话自动版转为历史留档，不再被本会话正式报告顶替
        RepairActions.Clear();
        RepairHint = "";
        Items.Clear();
        SlowCauses.Clear();
        HasSlowCauses = false;
        SlowConclusionText = "";
        ProgressValue = 0;

        var mode = ModeIndex switch { 0 => RunMode.Full, 1 => RunMode.Quick, _ => RunMode.Speed };
        if (mode == RunMode.Speed)
        {
            SpeedPhaseText = "准备中…";
            SpeedMbpsText = "—";
            SpeedSubText = "探测测速节点中，全程只读下载、不落盘、不上传";
        }

        var plan = _diag.CreatePlan(mode);
        foreach (var item in plan) Items.Add(item);

        _cts = new CancellationTokenSource();
        var progress = new Progress<CheckItem>(item =>
        {
            int done = Items.Count(x => x.Status is not (CheckStatus.Pending or CheckStatus.Running));
            ProgressValue = done;
            if (item.Status == CheckStatus.Running)
            {
                ProgressText = $"正在检查：{item.Name}（{CheckCategoryText.Of(item.Category)}）";
            }
            Recount();
        });
        var speedProgress = new Progress<SpeedProgress>(OnSpeedTick);

        try
        {
            _lastReport = await _diag.ExecuteAsync(plan, mode, progress, _cts.Token, speedProgress).ConfigureAwait(true);
            _lastContext = _diag.LastContext;
            HasRun = true;
            Recount();
            _topoModeText = ModeTextOf(mode);
            Topology = NetworkTopologyBuilder.Build(_lastContext, Items.ToList(), _topoModeText);
            ProgressText = $"诊断完成：通过 {PassCount} · 警告 {WarnCount} · 未通过 {FailCount}" +
                (FixedCount > 0 ? $" · 已修复 {FixedCount}" : "");
            SessionText = $"会话日志：{_lastReport.StartedAt:yyyy-MM-dd HH:mm:ss} · {_lastReport.ModeText}";
            if (mode == RunMode.Speed) FillSpeedResult();
            BuildRepairPlan();
            // 报告强制性生成（插件版新增）：诊断完成必出报告，不再依赖用户手动导出防遗忘；
            // 失败在 InfoBar 可见化（AutoReportMessage 驱动），不响诊断成功主流程。
            var ok = await AutoGenerateReportAsync(afterRepair: false).ConfigureAwait(true);
            ProgressText += ok ? " · 报告已自动生成" : " · 报告生成失败（见右下提示）";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "诊断已取消。";
            Recount();
            if (mode == RunMode.Speed) SpeedPhaseText = "已取消";
            // 部分结果拓扑：已完成节点照常着色，未完成节点灰色并标注「诊断已取消」
            if (_diag.LastContext != null)
            {
                _lastContext = _diag.LastContext;
                _topoModeText = ModeTextOf(mode);
                Topology = NetworkTopologyBuilder.Build(_lastContext, Items.ToList(), _topoModeText, cancelled: true);
            }
        }
        catch (Exception ex)
        {
            ProgressText = $"诊断异常：{ex.Message}";
            _log("error", $"[diag] 诊断执行异常：{ex}");
            if (_diag.LastContext != null)
            {
                _lastContext = _diag.LastContext;
                _topoModeText = ModeTextOf(mode);
                Topology = NetworkTopologyBuilder.Build(_lastContext, Items.ToList(), _topoModeText);
            }
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnSpeedTick(SpeedProgress p)
    {
        SpeedPhaseText = string.IsNullOrEmpty(p.Phase) ? SpeedPhaseText : p.Phase;
        SpeedMbpsText = p.AvgMbps <= 0 ? "…" : p.AvgMbps.ToString("0.#");
        SpeedSubText = $"瞬时 {p.InstantMbps:0.#} Mbps · 已下载 {FormatHelper.Bytes(p.BytesRead)} · {p.Connections} 连接";
    }

    private void FillSpeedResult()
    {
        var sp = _lastContext?.SpeedResult;
        if (sp is { Success: true })
        {
            SpeedMbpsText = sp.MbpsAvg.ToString("0.#");
            SpeedPhaseText = sp.ExperienceBand;
            SpeedSubText = $"实测 {sp.SpeedText}（峰值 {sp.PeakText}）· 链路 {sp.LinkText} · 实测效率 {sp.EfficiencyText} · 节点 {sp.EndpointLabel}";
        }
        else
        {
            SpeedMbpsText = "—";
            SpeedPhaseText = "未测得带宽";
            SpeedSubText = string.IsNullOrEmpty(sp?.Error) ? "下载测速未执行或被取消" : sp!.Error;
        }

        foreach (var c in _lastReport?.SlowCauses ?? Enumerable.Empty<SlowCause>())
            SlowCauses.Add(c);
        HasSlowCauses = SlowCauses.Count > 0;
        SlowConclusionText = SlowSpeedAnalyzer.Conclusion(SlowCauses.ToList(), sp);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning) _cts?.Cancel();
    }

    // ================= 生命周期收尾 =================
    // 与内置页同一惯例：页面 Unloaded 时取消在途长操作并释放取消源。
    // 插件版 VM 为 transient（每次导航新建），若不收尾，旧诊断/修复会在后台裸奔（白耗出口带宽）；
    // 诊断/修复服务随 VM 同生命周期新建（宿主内置诊断服务为单例 + LastContext 并发写入兜底，
    // 插件版直接每导航新实例，天然无交叉写入窗口）。

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
            _ctsRepair?.Cancel();
        }
        catch (ObjectDisposedException) { } // 与 StartAsync/FixSelectedAsync finally 的释放窗口兜底（同为 UI 线程，实际不会撞）
        _cts?.Dispose();
        _ctsRepair?.Dispose();
        _cts = null;
        _ctsRepair = null;
    }

    [RelayCommand]
    private void CancelRepair()
    {
        if (IsRepairing) _ctsRepair?.Cancel();
    }

    private void Recount()
    {
        PassCount = Items.Count(x => x.Status == CheckStatus.Pass);
        WarnCount = Items.Count(x => x.Status == CheckStatus.Warning);
        FailCount = Items.Count(x => x.Status == CheckStatus.Fail);
        FixedCount = Items.Count(x => x.Status == CheckStatus.Fixed);
        SkipCount = Items.Count(x => x.Status == CheckStatus.Skipped);
        ProgressValue = Items.Count(x => x.Status is not (CheckStatus.Pending or CheckStatus.Running));
    }

    // ================= 一键修复 =================

    private void BuildRepairPlan()
    {
        var plan = _repair.BuildPlan(Items.ToList(), _lastContext);
        RepairActions.Clear();
        foreach (var a in plan) RepairActions.Add(a);
        HasRepairPlanPublic = plan.Count > 0;
        RepairHint = plan.Count == 0
            ? "未发现可自动修复的问题。"
            : $"发现 {plan.Count} 个可执行修复动作。管理类操作将合并为一次 UAC 提升确认；危险项默认不勾选。";
    }

    [ObservableProperty] private bool _hasRepairPlanPublic;

    partial void OnHasRepairPlanPublicChanged(bool value) => OnPropertyChanged(nameof(CanRepair));

    [RelayCommand]
    private async Task FixSelectedAsync()
    {
        if (IsRepairing) return;
        var selected = RepairActions.Where(a => a.IsSelected && a.Status is RepairStatus.Pending or RepairStatus.Fail).ToList();
        if (selected.Count == 0) return;
        IsRepairing = true;
        OnPropertyChanged(nameof(CanRepair));
        var progress = new Progress<RepairAction>(_ => { });
        _ctsRepair?.Dispose();
        _ctsRepair = new CancellationTokenSource(); // 每次执行重建：上轮取消的残留状态不得带入本轮
        try
        {
            await _repair.RunAsync(selected, progress, _ctsRepair.Token).ConfigureAwait(true);

            // 修复成功 → 对应检查项转「已修复」：打通 Fixed 状态链路（计数芯片/绿勾/PDF 样式此前均已支持，
            // 但全工程从未写入该状态，导致修复后列表永远停在红/黄、无闭环反馈）。
            // open_portal 的成功仅代表门户已打开，认证是否成功须复诊确认，不翻转；
            // winsock 重置需重启才生效，翻转文案特别标注。
            if (!IsRunning) // 用户已重开诊断则不动新列表
            {
                foreach (var a in selected.Where(a => a.Status == RepairStatus.Ok && a.Id != "open_portal"))
                {
                    var suffix = a.Id == "winsock_reset" ? "，重启计算机后生效" : "";
                    foreach (var item in Items.Where(i => a.RelatedCheckIds.Contains(i.Id)
                                                          && i.Status is CheckStatus.Warning or CheckStatus.Fail))
                    {
                        item.Status = CheckStatus.Fixed;
                        item.Summary = $"已执行修复（{a.Title}{suffix}）";
                    }
                }
                Recount();
                // 修复后重建拓扑：Fixed 状态映射为绿色「正常」，断点定位随实际修复结果收敛
                Topology = NetworkTopologyBuilder.Build(_lastContext, Items.ToList(), _topoModeText);
            }
            RepairHint = "修复执行完毕。建议重新运行诊断复核结果；Winsock 重置需要重启计算机后生效。";
            // 报告强制性生成（插件版新增）：修复后重出报告，把修复结果固化进留档（用户重开诊断则跳过，新报告由新诊断产出）
            if (!IsRunning)
            {
                var ok = await AutoGenerateReportAsync(afterRepair: true).ConfigureAwait(true);
                if (ok) RepairHint += " 报告已同步重新生成。";
            }
        }
        catch (OperationCanceledException)
        {
            RepairHint = "修复已取消（已执行的动作保留生效，可重新运行诊断复核）。";
        }
        catch (Exception ex)
        {
            RepairHint = $"修复执行失败：{ex.Message}。可重试，或导出 PDF 报告后手工处理。";
        }
        finally
        {
            IsRepairing = false;
            OnPropertyChanged(nameof(CanRepair));
        }
    }

    // ================= PDF 报告生成（强制性：诊断/修复完成自动出报告 + 手动补全信息重新导出） =================

    /// <summary>
    /// 手动重新导出（视图「重新导出 PDF 报告」按钮）：可补全学校/年级/班级/报告人/设备编号，
    /// 并写入表单记忆。正式上报场景用；年级班级必填校验在视图层对话框完成。
    /// </summary>
    public Task<string> ExportReportAsync(string school, string grade, string className, string reporter, string deviceTag)
        => GenerateReportAsync(school, grade, className, reporter, deviceTag, rememberForm: true);

    /// <summary>
    /// 报告强制性自动生成（插件版新增，宿主内置版无此语义）：诊断完成与修复完成后各调用一次。
    /// 元信息取表单记忆五项，空值按当前登录用户/主机名兜底；不写表单记忆（自动生成的兜底值
    /// 不得污染用户已填的手动表单）；失败吞掉并置 AutoReportFailed（InfoBar 可见化），
    /// 绝不影响诊断/修复主流程的成败。返回是否生成成功（调用方据此追加提示文案）。
    /// </summary>
    private async Task<bool> AutoGenerateReportAsync(bool afterRepair)
    {
        try
        {
            var facts = await _probe.EnsureLoadedAsync().ConfigureAwait(true);
            var reporter = string.IsNullOrWhiteSpace(_settings.LastReporter) ? Environment.UserName : _settings.LastReporter;
            var deviceTag = string.IsNullOrWhiteSpace(_settings.LastDeviceTag) ? facts.HostName : _settings.LastDeviceTag;
            var path = await GenerateReportAsync(_settings.LastSchool, _settings.LastGrade, _settings.LastClassName,
                reporter, deviceTag, rememberForm: false).ConfigureAwait(true);
            _autoReportPaths.Add(path);
            AutoReportFailed = false;
            // 引导式要求填写信息：自动版仅兜底留档，正式上报需补全信息重新生成顶替
            //（View 侧 InfoBar 同步带「补全信息」动作按钮，点击即开导出对话框）。
            AutoReportMessage = (afterRepair ? "修复后报告已重新生成：" : "报告已自动生成（强制性留档）：") + path
                + "。如需正式上报，点「补全信息」填写学校 / 年级 / 班级，重新生成的正式报告将顶替（移除）本兜底报告。";
            _log("info", $"[report] 已自动生成诊断报告（{(afterRepair ? "修复后" : "诊断后")}）：{path}");
            return true;
        }
        catch (Exception ex)
        {
            AutoReportFailed = true;
            AutoReportMessage = "自动生成失败：" + ex.Message + "。可点「重新导出 PDF 报告」重试。";
            _log("error", $"[report] 自动生成失败：{ex}");
            return false;
        }
    }

    /// <summary>报告生成核心（手动导出与自动生成共用；rememberForm 仅手动路径为 true）。</summary>
    private async Task<string> GenerateReportAsync(string school, string grade, string className, string reporter, string deviceTag, bool rememberForm)
    {
        if (_lastReport == null) throw new InvalidOperationException("尚未执行诊断");
        var facts = await _probe.EnsureLoadedAsync().ConfigureAwait(true);
        var speed = _lastContext?.SpeedResult;
        var meta = new ReportMeta
        {
            School = school,
            Grade = grade,
            ClassName = className,
            Reporter = reporter,
            DeviceTag = string.IsNullOrWhiteSpace(deviceTag) ? facts.HostName : deviceTag,
            SerialNumber = facts.SerialNumber,
            MachineGuid = facts.MachineGuid,
            HostName = facts.HostName,
            OsVersion = facts.OsVersion,
            CpuName = facts.CpuName,
            TotalRamText = FormatHelper.Bytes((long)facts.TotalRamBytes),
            NicModel = facts.NicModel,
            IpV4 = _lastContext?.Primary?.IpV4 ?? "",
            MacAddress = facts.MacAddress,
            GeneratedAt = DateTime.Now,
            StartedAt = _lastReport.StartedAt,
            FinishedAt = _lastReport.FinishedAt,
            ToolVersion = AppVersion.DisplayText,
            ModeText = _lastReport.ModeText,
            // 准确性兜底：测速未成功（或未跑测速模式）时链路速率改取主适配器的协商速率，
            // 报告不再因测速失败缺失链路信息（协商速率是硬件事实，不依赖测速节点可达）
            LinkSpeedText = speed is { Success: true, LinkMbps: > 0 }
                ? $"{(_lastContext?.Primary?.IsWireless == true ? "无线" : "有线")} {speed.LinkText}"
                : _lastContext?.Primary is { SpeedBps: > 0 } pri
                    ? $"{(pri.IsWireless ? "无线" : "有线")} {pri.SpeedBps / 1_000_000} Mbps（链路协商）"
                    : ""
        };

        var data = new NetworkReportData
        {
            Meta = meta,
            Results = _lastReport.Results.ToList(),
            Repairs = RepairActions.ToList(),
            Recommendations = BuildRecommendations(),
            SpeedTest = speed,
            SlowCauses = _lastReport.SlowCauses,
            // 拓扑快照随报告入 PDF：页面 Canvas 与报告共用同一份实测构建结果
            Topology = Topology.Nodes.Count > 0 ? Topology : null
        };
        var path = await _pdf.GenerateAsync(data).ConfigureAwait(true);
        LastReportPath = path;
        ReportReady = true;

        // 记住上次填写，减少重复输入（插件适配：Last* 五项写入插件 store 并落盘 settings.json；
        // 自动生成路径不进来——兜底值不得覆盖用户手动填过的表单记忆）
        if (rememberForm)
        {
            _settings.SetLastReport(school, grade, className, reporter, meta.DeviceTag);
            // 顶替语义：正式报告（信息完整）已生成，移除本诊断会话自动生成的兜底报告（诊断后 + 修复后），
            // 防上报时「未填写」兜底版与正式版并列；删除前校验 QS-DIAG- 前缀防误删，失败仅记日志不响正式报告。
            foreach (var auto in _autoReportPaths)
            {
                try
                {
                    if (File.Exists(auto) && Path.GetFileName(auto).StartsWith("QS-DIAG-", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(auto);
                        _log("info", $"[report] 正式报告已顶替自动兜底报告（已删除）：{auto}");
                    }
                }
                catch (Exception ex)
                {
                    _log("warn", $"[report] 兜底报告清理失败（不影响正式报告，可手动删除）：{auto}：{ex.Message}");
                }
            }
            _autoReportPaths.Clear();
        }
        _log("info", $"[report] 已生成诊断报告：{path}");
        return path;
    }

    private static string ModeTextOf(RunMode mode) => mode switch
    {
        RunMode.Full => "全面诊断",
        RunMode.Quick => "快速体检",
        _ => "网速与低速分析"
    };

    private List<(int, string)> BuildRecommendations()
    {
        var list = new List<(int, string)>();
        foreach (var f in Items.Where(x => x.Status == CheckStatus.Fail))
            list.Add((1, $"【{f.Name}】{f.Suggestion}"));
        foreach (var w in Items.Where(x => x.Status == CheckStatus.Warning))
            list.Add((2, $"【{w.Name}】{w.Suggestion}"));
        var causes = _lastReport?.SlowCauses; // 独立方法内空流分析不继承调用方的非空守卫，需自证空安全
        if (causes is { Count: > 0 })
            list.Add((2, $"低速首要嫌疑：{causes[0].Title}——按“低速原因分析”清单逐项处置后复测对比。"));
        if (FixedCount > 0)
            list.Add((2, "已执行自动修复的项目建议在课后再次运行“快速体检”复核修复效果。"));
        list.Add((3, "建议每周运行一次全面诊断并留存报告，形成教室网络健康基线，便于故障回溯。"));
        return list;
    }
}
