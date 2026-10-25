using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Power;
using QingShu.Toolkit.Services.Popup;
using QingShu.Toolkit.Views;

namespace QingShu.Toolkit.ViewModels;

/// <summary>仪表盘 VM：设备信息 / 实时监控 / 功耗模式 / 最近诊断与清理统计。</summary>
public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IHardwareProbeService _probe;
    private readonly ISettingsService _settings;
    private readonly IPowerService _power;
    private readonly IPopupBlockerService _popup;
    private readonly ILogService _log;
    private readonly DispatcherTimer _timer;
    private PerformanceCounter? _cpuCounter;

    [ObservableProperty] private string _hostName = Environment.MachineName;
    [ObservableProperty] private string _osVersion = "…";
    [ObservableProperty] private string _cpuName = "…";
    [ObservableProperty] private string _serialNumber = "…";
    [ObservableProperty] private string _machineGuid = "…";
    [ObservableProperty] private string _nicText = "…";

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _cpuText = "0%";
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _memText = "";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskText = "";

    [ObservableProperty] private string _powerStateText = "平衡";
    [ObservableProperty] private string _powerModeText = "自动切换";
    [ObservableProperty] private string _powerDesc = "";
    [ObservableProperty] private int _powerModeIndex; // 0=自动 1=平衡 2=高性能 3=低功耗

    [ObservableProperty] private string _diagTodayText = "0";
    [ObservableProperty] private string _popupBlockedText = "0";
    [ObservableProperty] private string _lastCleanupText = "尚未执行过清理";
    [ObservableProperty] private string _lastDiagText = "尚未执行过诊断";

    public DashboardViewModel(IHardwareProbeService probe, IPowerService power, IPopupBlockerService popup, ILogService log, ISettingsService settings)
    {
        _probe = probe;
        _settings = settings;
        _power = power;
        _popup = popup;
        _log = log;

        _power.Changed += OnPowerChanged;
        _popup.Blocked += OnPopupBlocked;
        UpdatePower(_power.Current, _power.IsOnBattery);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => _ = RefreshLiveAsync();
        _timer.Start();
        _ = RefreshLiveAsync();
        _ = LoadFactsAsync();
        _ = LoadStatsAsync();
    }

    private async Task LoadFactsAsync()
    {
        var f = await _probe.EnsureLoadedAsync().ConfigureAwait(true);
        // WMI/netsh 类探测放在线程池；（同步上下文已安装）await 后回到 UI 线程再更新绑定
        var nic = await Task.Run(() =>
        {
            var primary = NetworkProbeHelper.GetPrimaryAdapter();
            return primary == null ? "无活动网卡"
                : $"{primary.Name} · {primary.IpV4 ?? "无IP"} · {(primary.IsUp ? "已连接" : "断开")}";
        }).ConfigureAwait(true);
        OsVersion = string.IsNullOrEmpty(f.OsVersion) ? $"Windows（Build {f.OsBuild}）" : f.OsVersion;
        CpuName = string.IsNullOrEmpty(f.CpuName) ? "未知处理器" : f.CpuName;
        SerialNumber = f.SerialNumber;
        MachineGuid = string.IsNullOrEmpty(f.MachineGuid) ? "未知" : f.MachineGuid;
        NicText = nic;
    }

    private bool _sampling; // 采样重入保护：耗时超过 2s 时跳过一拍

    private async Task RefreshLiveAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var s = await Task.Run(SampleLive).ConfigureAwait(true);
            CpuPercent = s.CpuPct;
            CpuText = s.CpuText;
            if (s.MemTotal > 0)
            {
                MemPercent = s.MemPct;
                MemText = s.MemText;
            }
            if (s.DiskOk)
            {
                DiskPercent = s.DiskPct;
                DiskText = s.DiskText;
            }
        }
        catch { }
        finally { _sampling = false; }
    }

    private (double CpuPct, string CpuText, double MemPct, string MemText, long MemTotal, bool DiskOk, double DiskPct, string DiskText) SampleLive()
    {
        double cpu = CpuPercent;
        string cpuText = CpuText;
        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            if (Math.Abs(CpuPercent) < 0.001 && _cpuCounter.RawValue >= 0) _cpuCounter.NextValue(); // 首次采样预热
            cpu = Math.Clamp(_cpuCounter.NextValue(), 0, 100);
            cpuText = $"{cpu:0.#}%";
        }
        catch { cpuText = "N/A"; }

        var (load, total, avail) = NativeMethods.GetMemoryStatus();
        string memText = total > 0
            ? $"{FormatHelper.Bytes((long)(total - avail))} / {FormatHelper.Bytes((long)total)}（占用 {load}%）"
            : "";

        var c = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == @"C:\" && d.IsReady);
        bool diskOk = c != null && c.TotalSize > 0;
        double diskPct = 0;
        string diskText = "";
        if (c != null && c.TotalSize > 0)
        {
            diskPct = 100.0 * (c.TotalSize - c.AvailableFreeSpace) / c.TotalSize;
            diskText = $"C 盘剩余 {FormatHelper.Bytes(c.AvailableFreeSpace)} / 共 {FormatHelper.Bytes(c.TotalSize)}";
        }
        return (cpu, cpuText, load, memText, (long)total, diskOk, diskPct, diskText);
    }

    private void OnPowerChanged(PowerUpdate u) => UpdatePower(u.State, u.IsOnBattery, u.ModeText, u.Description);

    private void UpdatePower(PowerUiState state, bool onBattery, string? modeText = null, string? desc = null)
    {
        PowerStateText = state switch
        {
            PowerUiState.Eco => "低功耗",
            PowerUiState.High => "高性能",
            _ => "平衡"
        };
        PowerModeText = modeText ?? (_power.Current switch
        {
            PowerUiState.Eco => "低功耗",
            PowerUiState.High => "高性能",
            _ => "平衡"
        }) + "（当前）";
        PowerDesc = desc ?? (onBattery ? "当前使用电池供电" : "当前已接通电源");
    }

    partial void OnPowerModeIndexChanged(int value)
    {
        var mode = value switch { 1 => "balanced", 2 => "performance", 3 => "eco", _ => "auto" };
        if (mode != _settings.Current.PowerMode)
        {
            _power.SetMode(mode);
            PowerModeText = value switch { 1 => "手动·平衡", 2 => "手动·高性能", 3 => "手动·低功耗", _ => "自动切换" };
        }
    }

    private void OnPopupBlocked(int count)
    {
        PopupBlockedText = count.ToString();
        // 同步今日动态
        PopupBlockedText = _popup.BlockedCount.ToString();
    }

    private async Task LoadStatsAsync()
    {
        DiagTodayText = _log.GetDiagSessionCount(DateTime.Today).ToString();
        PopupBlockedText = _popup.BlockedCount.ToString();
        LastDiagText = DiagTodayText == "0" ? "今天还没有诊断过" : $"今天已运行 {DiagTodayText} 次诊断";
        // 最近一次清理报告（磁盘 IO，线程池执行）
        var lastCleanup = await Task.Run(FindLastCleanupText).ConfigureAwait(true);
        if (lastCleanup != null) LastCleanupText = lastCleanup;
    }

    private string? FindLastCleanupText()
    {
        try
        {
            var dirs = _log.GetDateDirs();
            foreach (var (dir, date, _) in dirs)
            {
                var cleanup = _log.GetDateFiles(dir).FirstOrDefault(f => f.FileName.StartsWith("cleanup-", StringComparison.Ordinal));
                if (cleanup.FileName == null) continue;
                try
                {
                    var json = File.ReadAllText(cleanup.FullPath);
                    var report = JsonSerializer.Deserialize<CleanupReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (report != null) return $"{date} 释放 {FormatHelper.Bytes(report.FreedBytes)}（{report.DeletedFiles} 项）";
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    [RelayCommand]
    private void GoNetwork() => App.NavigateTo(typeof(NetworkDiagnosticsPage));

    [RelayCommand]
    private void GoCleanup() => App.NavigateTo(typeof(DiskCleanupPage));

    [RelayCommand]
    private void OpenLogs()
    {
        try { Process.Start(new ProcessStartInfo(_log.Root) { UseShellExecute = true }); } catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _power.Changed -= OnPowerChanged;
        _popup.Blocked -= OnPopupBlocked;
        _cpuCounter?.Dispose();
    }
}
