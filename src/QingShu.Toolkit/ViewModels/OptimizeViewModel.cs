using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Optimize;
using QingShu.Toolkit.Services.Popup;

namespace QingShu.Toolkit.ViewModels;

// ================= 行模型 =================
public partial class ProcessRowVm : ObservableObject
{
    public ProcessRowVm(string name, int pid, long ws, bool ecoApplied = false)
    {
        Name = name; Pid = pid; WorkingSet = ws; EcoApplied = ecoApplied;
        Purpose = ProcessKnowledge.Describe(name);
        IsSystem = ProcessKnowledge.IsSystemComponent(name);
    }

    public string Name { get; }
    public int Pid { get; }
    public long WorkingSet { get; set; }
    public bool EcoApplied { get; set; }
    public string WsText => FormatHelper.Bytes(WorkingSet);
    public string PidText => Pid.ToString();
    public bool EcoVis => EcoApplied;
    /// <summary>进程作用说明（让用户一眼知道这是干什么的）。</summary>
    public string Purpose { get; }
    /// <summary>系统关键组件标记（整理/降频需谨慎）。</summary>
    public bool IsSystem { get; }

    [ObservableProperty] private bool _isSelected;
}

public partial class WhitelistRowVm : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public WhitelistRowVm(string name, OptimizeViewModel owner) { Name = name; _owner = owner; }

    public string Name { get; }

    /// <summary>行模板内经 x:Bind Owner.Xxx 触达页面级命令（x:DataType 模板内无法直接引用页面）。</summary>
    public OptimizeViewModel Owner => _owner;
}

public partial class StartupRowVm
{
    public StartupRowVm(StartupEntry e) => (Name, Command, Source) = (e.Name, e.Command, e.Source);
    public string Name { get; }
    public string Command { get; }
    public string Source { get; }
}

/// <summary>性能优化页 VM：内存整理 / 进程 EcoQoS / 弹窗拦截白名单 / 启动项只读。</summary>
public partial class OptimizeViewModel : ObservableObject
{
    private readonly IMemoryOptimizeService _memory;
    private readonly IStartupListService _startup;
    private readonly IPopupBlockerService _popup;
    private readonly ISettingsService _settings;

    public ObservableCollection<ProcessRowVm> MemoryProcesses { get; } = new();
    public ObservableCollection<ProcessRowVm> EcoProcesses { get; } = new();
    public ObservableCollection<WhitelistRowVm> Whitelist { get; } = new();
    public ObservableCollection<StartupRowVm> StartupItems { get; } = new();

    [ObservableProperty] private bool _isMemoryBusy;
    [ObservableProperty] private string _memoryResult = "";
    [ObservableProperty] private bool _isEcoBusy;
    [ObservableProperty] private string _ecoResult = "";
    [ObservableProperty] private bool _popupEnabled;
    [ObservableProperty] private string _popupCountText = "0";
    [ObservableProperty] private string _newWhitelistName = "";
    [ObservableProperty] private bool _isStartupBusy;
    [ObservableProperty] private string _startupHint = "启动项为只读展示：如需调整请使用任务管理器 → 启动应用（本工具不代改系统配置）。";

    public OptimizeViewModel(IMemoryOptimizeService memory, IStartupListService startup,
        IPopupBlockerService popup, ISettingsService settings)
    {
        _memory = memory;
        _startup = startup;
        _popup = popup;
        _settings = settings;
        _popupEnabled = settings.Current.PopupBlockEnabled;
        _popup.Blocked += c => PopupCountText = c.ToString();
        PopupCountText = popup.BlockedCount.ToString();
        ReloadWhitelist();
        // 白名单可能从右侧状态栏对话框同步修改：设置变更时自动刷新，避免两处 UI 不一致
        _settings.Changed += ReloadWhitelist;
        _ = RefreshMemoryAsync();
        _ = RefreshStartupAsync();
    }

    // ================= 内存整理 =================

    public bool MemoryReady => !IsMemoryBusy && MemoryProcesses.Count > 0;
    public bool EcoReady => !IsEcoBusy && EcoProcesses.Count > 0;

    partial void OnIsMemoryBusyChanged(bool value) => OnPropertyChanged(nameof(MemoryReady));
    partial void OnIsEcoBusyChanged(bool value) => OnPropertyChanged(nameof(EcoReady));

    [RelayCommand]
    private async Task RefreshMemoryAsync()
    {
        IsMemoryBusy = true;
        try
        {
            var list = await _memory.ListProcessesAsync().ConfigureAwait(true);
            MemoryProcesses.Clear();
            foreach (var p in list) MemoryProcesses.Add(new ProcessRowVm(p.Name, p.Pid, p.WorkingSetBytes));
            var selected = EcoProcesses.Where(x => x.IsSelected).Select(x => x.Pid).ToHashSet();
            EcoProcesses.Clear();
            foreach (var p in list) EcoProcesses.Add(new ProcessRowVm(p.Name, p.Pid, p.WorkingSetBytes, selected.Contains(p.Pid)));
            if (MemoryResult.Length == 0) MemoryResult = "共展示内存占用最高的 25 个进程。勾选后点击“整理所选内存”。";
        }
        finally { IsMemoryBusy = false; }
    }

    [RelayCommand]
    private async Task TrimMemoryAsync()
    {
        if (IsMemoryBusy) return;
        var pids = MemoryProcesses.Where(x => x.IsSelected).Select(x => x.Pid).ToList();
        if (pids.Count == 0) { MemoryResult = "请先勾选要整理的进程。"; return; }
        IsMemoryBusy = true;
        try
        {
            var r = await _memory.TrimAsync(pids).ConfigureAwait(true);
            MemoryResult = $"整理完成：{r.Trimmed} 个进程，释放约 {FormatHelper.Bytes(r.FreedBytes)} 工作集（跳过 {r.Skipped} 个受保护进程）。进程未被结束，仅回收可回收页。";
            await RefreshMemoryAsync().ConfigureAwait(true);
        }
        finally { IsMemoryBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyEcoAsync()
    {
        if (IsEcoBusy) return;
        var pids = EcoProcesses.Where(x => x.IsSelected).Select(x => x.Pid).ToList();
        if (pids.Count == 0) { EcoResult = "请先勾选要节能的进程。"; return; }
        IsEcoBusy = true;
        try
        {
            var r = await _memory.SetEcoAsync(pids, true).ConfigureAwait(true);
            foreach (var row in EcoProcesses.Where(x => x.IsSelected)) row.EcoApplied = true;
            EcoResult = $"已对 {r.Applied} 个进程启用 EcoQoS 节能（跳过 {r.Skipped} 个无权限进程）。后台闲置进程将自动降频省电。";
        }
        finally { IsEcoBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreEcoAsync()
    {
        if (IsEcoBusy) return;
        var pids = EcoProcesses.Where(x => x.IsSelected).Select(x => x.Pid).ToList();
        if (pids.Count == 0) { EcoResult = "请先勾选要恢复的进程。"; return; }
        IsEcoBusy = true;
        try
        {
            var r = await _memory.SetEcoAsync(pids, false).ConfigureAwait(true);
            foreach (var row in EcoProcesses.Where(x => x.IsSelected)) row.EcoApplied = false;
            EcoResult = $"已恢复 {r.Applied} 个进程的正常调度。";
        }
        finally { IsEcoBusy = false; }
    }

    // ================= 弹窗拦截 =================

    partial void OnPopupEnabledChanged(bool value) => _popup.SetEnabled(value);

    private void ReloadWhitelist()
    {
        Whitelist.Clear();
        foreach (var name in _settings.Current.PopupWhitelist.OrderBy(x => x)) Whitelist.Add(new WhitelistRowVm(name, this));
    }

    [RelayCommand]
    private void AddWhitelist()
    {
        var name = _newWhitelistName?.Trim().ToLowerInvariant().Replace(".exe", "");
        if (string.IsNullOrEmpty(name)) return;
        if (_settings.Current.PopupWhitelist.Contains(name)) return;
        _settings.Current.PopupWhitelist.Add(name);
        _newWhitelistName = "";
        _ = _settings.SaveAsync(); // 保存后的 Changed 事件会自动 ReloadWhitelist
    }

    [RelayCommand]
    private void RemoveWhitelist(object? param)
    {
        if (param is not string name) return;
        _settings.Current.PopupWhitelist.Remove(name);
        _ = _settings.SaveAsync(); // 保存后的 Changed 事件会自动 ReloadWhitelist
    }

    [RelayCommand]
    private void PausePopup() => PopupEnabled = false;

    // ================= 启动项 =================

    [RelayCommand]
    private async Task RefreshStartupAsync()
    {
        IsStartupBusy = true;
        try
        {
            var items = await _startup.GetAsync().ConfigureAwait(true);
            StartupItems.Clear();
            foreach (var i in items) StartupItems.Add(new StartupRowVm(i));
            if (StartupItems.Count == 0) StartupHint = "未发现注册表/启动文件夹中的自启动项。";
        }
        finally { IsStartupBusy = false; }
    }

    [RelayCommand]
    private void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }
}
