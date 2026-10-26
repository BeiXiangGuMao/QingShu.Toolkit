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

public partial class StartupRowVm : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public StartupRowVm(StartupEntry e, OptimizeViewModel owner)
    {
        Name = e.Name; Command = e.Command; Source = e.Source; Origin = e.Origin; Enabled = e.Enabled;
        _owner = owner;
    }

    public string Name { get; }
    public string Command { get; }
    public string Source { get; }
    public string Origin { get; }
    public bool Enabled { get; }

    /// <summary>行模板内经 x:Bind Owner.Xxx 触达页面级命令（x:DataType 模板内无法直接引用页面）。</summary>
    public OptimizeViewModel Owner => _owner;

    public bool EnabledVis => Enabled;
    public bool DisabledVis => !Enabled;
    public string ToggleText => Enabled ? "禁用" : "启用";
}

/// <summary>性能优化页 VM：内存整理 / 进程 EcoQoS / 启动项管理（禁用·启用·恢复）。</summary>
public partial class OptimizeViewModel : ObservableObject
{
    private readonly IMemoryOptimizeService _memory;
    private readonly IStartupListService _startup;

    public ObservableCollection<ProcessRowVm> MemoryProcesses { get; } = new();
    public ObservableCollection<ProcessRowVm> EcoProcesses { get; } = new();
    public ObservableCollection<StartupRowVm> StartupItems { get; } = new();

    [ObservableProperty] private bool _isMemoryBusy;
    [ObservableProperty] private string _memoryResult = "";
    [ObservableProperty] private bool _isEcoBusy;
    [ObservableProperty] private string _ecoResult = "";
    [ObservableProperty] private bool _isStartupBusy;
    [ObservableProperty] private string _startupHint = "支持在应用内直接禁用/启用开机自启动项：禁用前自动备份，可随时恢复原样；不触碰计划任务与系统服务。";
    [ObservableProperty] private string _startupResult = "";

    public bool HasStartupResult => StartupResult.Length > 0;

    partial void OnStartupResultChanged(string value) => OnPropertyChanged(nameof(HasStartupResult));

    public OptimizeViewModel(IMemoryOptimizeService memory, IStartupListService startup)
    {
        _memory = memory;
        _startup = startup;
        _ = RefreshMemoryAsync();
        _ = RefreshStartupAsync();
    }

    // ================= 内存整理 =================

    public bool MemoryReady => !IsMemoryBusy && MemoryProcesses.Count > 0;
    public bool EcoReady => !IsEcoBusy && EcoProcesses.Count > 0 && _memory.IsEcoSupported();
    public bool EcoSupported => _memory.IsEcoSupported();

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
            // EcoReady 依赖 EcoProcesses.Count，但 ObservableCollection 填充不会自动通知 x:Bind；
            // 不在此处补发通知的话，按钮 IsEnabled 停留在页首初始化的 false 永久禁用（进程节能「不能用」根因）
            OnPropertyChanged(nameof(EcoReady));
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
        if (!_memory.IsEcoSupported()) { EcoResult = "本系统不支持 EcoQoS（需要 Windows 10 1709 及以上），入口已停用。"; return; }
        var pids = EcoProcesses.Where(x => x.IsSelected).Select(x => x.Pid).ToList();
        if (pids.Count == 0) { EcoResult = "请先勾选要节能的进程。"; return; }
        IsEcoBusy = true;
        try
        {
            var r = await _memory.SetEcoAsync(pids, true).ConfigureAwait(true);
            foreach (var row in EcoProcesses.Where(x => x.IsSelected)) row.EcoApplied = true;
            EcoResult = $"已对 {r.Applied} 个进程启用 EcoQoS 节能。" +
                        (r.Detail.Length > 0 ? $"跳过：{r.Detail}。" : "") +
                        "前台正在使用的进程会被系统自动豁免，降频省电主要体现在转入后台之后。";
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
            EcoResult = $"已恢复 {r.Applied} 个进程的正常调度。" + (r.Detail.Length > 0 ? $"跳过：{r.Detail}。" : "");
        }
        finally { IsEcoBusy = false; }
    }

    // ================= 启动项管理 =================

    [RelayCommand]
    private async Task RefreshStartupAsync()
    {
        IsStartupBusy = true;
        try
        {
            var items = await _startup.GetAsync().ConfigureAwait(true);
            StartupItems.Clear();
            foreach (var i in items) StartupItems.Add(new StartupRowVm(i, this));
            if (StartupItems.Count == 0) StartupHint = "未发现注册表 Run 键 / 启动文件夹中的自启动项。";
        }
        finally { IsStartupBusy = false; }
    }

    /// <summary>禁用/启用单个启动项（按钮文本随状态切换），成功后自动刷新列表。</summary>
    [RelayCommand]
    private async Task ToggleStartupAsync(object? param)
    {
        if (IsStartupBusy || param is not StartupRowVm row) return;
        IsStartupBusy = true;
        try
        {
            var entry = new StartupEntry(row.Name, row.Command, row.Source, row.Origin, row.Enabled);
            var (ok, err) = row.Enabled
                ? await _startup.DisableAsync(entry).ConfigureAwait(true)
                : await _startup.EnableAsync(entry).ConfigureAwait(true);
            StartupResult = ok ? $"已{(row.Enabled ? "禁用" : "启用")}「{row.Name}」。"
                               : err;
            if (ok) await RefreshStartupAsync().ConfigureAwait(true);
        }
        finally { IsStartupBusy = false; }
    }

    [RelayCommand]
    private void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }
}
