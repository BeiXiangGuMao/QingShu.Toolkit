using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Network;
using QingShu.Toolkit.Services.Reporting;

namespace QingShu.Toolkit.ViewModels;

/// <summary>网络诊断页 VM：傻瓜模式一站式“诊断 → 修复 → 导出报告”。</summary>
public partial class NetworkDiagViewModel : ObservableObject
{
    private readonly INetworkDiagnosticService _diag;
    private readonly INetworkRepairService _repair;
    private readonly IPdfReportService _pdf;
    private readonly ISettingsService _settings;
    private readonly IHardwareProbeService _probe;
    private readonly ILogService _log;

    private DiagnosticSessionReport? _lastReport;
    private CheckContext? _lastContext;
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

    /// <summary>修复执行中的取消按钮可见性（bool → x:Bind 隐式转 Visibility，与全项目 BusyVis 模式一致）。</summary>
    public bool RepairCancelVis => IsRepairing;

    partial void OnIsRepairingChanged(bool value) => OnPropertyChanged(nameof(RepairCancelVis));
    [ObservableProperty] private string _repairHint = "";
    [ObservableProperty] private string _lastReportPath = "";
    [ObservableProperty] private bool _reportReady;
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
        IPdfReportService pdf, ISettingsService settings, IHardwareProbeService probe, ILogService log)
    {
        _diag = diag;
        _repair = repair;
        _pdf = pdf;
        _settings = settings;
        _probe = probe;
        _log = log;
        LogLocationText = log.Root;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (IsRepairing) return; // 修复提权进行中禁止重开诊断：避免并发操作/UAC 弹窗交叠与列表竞态
        IsRunning = true;
        HasRun = false;
        ReportReady = false;
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
            _log.App(LogLevel.Error, "diag", "诊断执行异常", new { ex = ex.ToString() });
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
    // 与 Dashboard/PopupGuard/Settings 同一惯例：页面 Unloaded 时取消在途长操作并释放取消源。
    // VM 为 transient：切页再进是全新实例，若不收尾，旧诊断/修复会在后台裸奔（白耗出口带宽），
    // 且复入页的新 VM 与旧操作并发——诊断服务是单例，LastContext 会被两路交叉写入。

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

    // ================= PDF 报告导出 =================

    public async Task<string> ExportReportAsync(string school, string grade, string className, string reporter, string deviceTag)
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
            LinkSpeedText = speed is { Success: true, LinkMbps: > 0 }
                ? $"{(_lastContext?.Primary?.IsWireless == true ? "无线" : "有线")} {speed.LinkText}"
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
            // 拓扑快照随报告入 PDF（Task 52）：页面 Canvas 与报告共用同一份实测构建结果
            Topology = Topology.Nodes.Count > 0 ? Topology : null
        };
        var path = await _pdf.GenerateAsync(data).ConfigureAwait(true);
        LastReportPath = path;
        ReportReady = true;

        // 记住上次填写，减少重复输入
        var s = _settings.Current;
        s.LastSchool = school; s.LastGrade = grade; s.LastClassName = className;
        s.LastReporter = reporter; s.LastDeviceTag = meta.DeviceTag;
        await _settings.SaveAsync().ConfigureAwait(true);
        _log.App(LogLevel.Info, "report", $"已导出诊断报告：{path}");
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
