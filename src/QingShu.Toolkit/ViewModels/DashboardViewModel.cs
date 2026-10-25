using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Power;
using QingShu.Toolkit.Services.Popup;
using QingShu.Toolkit.Views;

namespace QingShu.Toolkit.ViewModels;

/// <summary>仪表盘 VM：设备信息 / 实时监控 / 功耗模式 / 最近诊断与清理统计。</summary>
public partial class DashboardViewModel : ObservableObject, IDisposable
{
    private readonly IHardwareProbeService _probe;
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

    public DashboardViewModel(IHardwareProbeService probe, IPowerService power, IPopupBlockerService popup, ILogService log)
    {
        _probe = probe;
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
        LoadStats();
    }

    private async Task LoadFactsAsync()
    {
        var f = await _probe.EnsureLoadedAsync().ConfigureAwait(true);
        OsVersion = string.IsNullOrEmpty(f.OsVersion) ? $"Windows（Build {f.OsBuild}）" : f.OsVersion;
        CpuName = string.IsNullOrEmpty(f.CpuName) ? "未知处理器" : f.CpuName;
        SerialNumber = f.SerialNumber;
        MachineGuid = string.IsNullOrEmpty(f.MachineGuid) ? "未知" : f.MachineGuid;
        var adapters = NetworkProbeHelper.GetAllAdapters();
        var primary = NetworkProbeHelper.GetPrimaryAdapter();
        NicText = primary == null ? "无活动网卡"
            : $"{primary.Name} · {primary.IpV4 ?? "无IP"} · {(primary.IsUp ? "已连接" : "断开")}";
    }

    private async Task RefreshLiveAsync()
    {
        try
        {
            // CPU
            try
            {
                _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
                if (Math.Abs(CpuPercent) < 0.001 && _cpuCounter.RawValue >= 0) _cpuCounter.NextValue(); // 首次采样预热
                var cpu = _cpuCounter.NextValue();
                CpuPercent = Math.Clamp(cpu, 0, 100);
                CpuText = $"{CpuPercent:0.#}%";
            }
            catch { CpuText = "N/A"; }

            // 内存
            var (load, total, avail) = NativeMethods.GetMemoryStatus();
            if (total > 0)
            {
                MemPercent = load;
                MemText = $"{FormatHelper.Bytes((long)(total - avail))} / {FormatHelper.Bytes((long)total)}（占用 {load}%）";
            }

            // C 盘
            var c = DriveInfo.GetDrives().FirstOrDefault(d => d.Name == @"C:\" && d.IsReady);
            if (c != null)
            {
                DiskPercent = 100.0 * (c.TotalSize - c.AvailableFreeSpace) / c.TotalSize;
                DiskText = $"C 盘剩余 {FormatHelper.Bytes(c.AvailableFreeSpace)} / 共 {FormatHelper.Bytes(c.TotalSize)}";
            }
        }
        catch { }
        await Task.CompletedTask;
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
        if (mode != App.Services.GetRequiredService<ISettingsService>().Current.PowerMode)
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

    private void LoadStats()
    {
        DiagTodayText = _log.GetDiagSessionCount(DateTime.Today).ToString();
        PopupBlockedText = _popup.BlockedCount.ToString();
        try
        {
            // 最近一次清理报告
            var dirs = _log.GetDateDirs();
            foreach (var (dir, date, _) in dirs)
            {
                var cleanup = _log.GetDateFiles(dir).FirstOrDefault(f => f.FileName.StartsWith("cleanup-", StringComparison.Ordinal));
                if (cleanup.FileName != null)
                {
                    try
                    {
                        var json = File.ReadAllText(cleanup.FullPath);
                        var report = JsonSerializer.Deserialize<CleanupReport>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (report != null)
                        {
                            LastCleanupText = $"{date} 释放 {FormatHelper.Bytes(report.FreedBytes)}（{report.DeletedFiles} 项）";
                            break;
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        LastDiagText = DiagTodayText == "0" ? "今天还没有诊断过" : $"今天已运行 {DiagTodayText} 次诊断";
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
