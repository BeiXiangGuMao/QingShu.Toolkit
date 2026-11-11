using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;

namespace QingShu.Plugin.Optimize;

// ================= 行模型 =================
// Visibility 态属性：运行时 XamlReader 模板走经典 Binding，bool→Visibility 无隐式转换
// （x:Bind 的隐式转换是编译期生成器行为），直接暴露 Microsoft.UI.Xaml.Visibility 类型属性
// （跨 ALC 类型同一）供模板绑定，免转换器。
public partial class ProcessRowVm : ObservableObject
{
    public ProcessRowVm(string name, int pid, long ws, bool ecoApplied = false)
    {
        Name = name; Pid = pid; WorkingSet = ws;
        _ecoApplied = ecoApplied;
        Purpose = ProcessKnowledge.Describe(name);
        IsSystem = ProcessKnowledge.IsSystemComponent(name);
    }

    public string Name { get; }
    public int Pid { get; }
    public long WorkingSet { get; set; }
    public string WsText => FormatHelper.Bytes(WorkingSet);
    public string PidText => Pid.ToString();
    public bool EcoVis => EcoApplied;
    public Visibility EcoVisState => EcoApplied ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>进程作用说明（让用户一眼知道这是干什么的）。</summary>
    public string Purpose { get; }
    /// <summary>系统关键组件标记（整理/降频需谨慎）。</summary>
    public bool IsSystem { get; }
    public Visibility IsSystemVisState => IsSystem ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty] private bool _isSelected;
    // 可观察化（内置页为普通属性：应用节能后徽章不刷新的潜伏缺陷，插件版顺手修正）
    [ObservableProperty] private bool _ecoApplied;

    partial void OnEcoAppliedChanged(bool value)
    {
        OnPropertyChanged(nameof(EcoVis));
        OnPropertyChanged(nameof(EcoVisState));
    }
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

    /// <summary>行模板内经 Binding Owner.ToggleStartupCommand 触达页面级命令（经典 Binding 支持子路径）。</summary>
    public OptimizeViewModel Owner => _owner;

    public bool EnabledVis => Enabled;
    public bool DisabledVis => !Enabled;
    public Visibility EnabledVisState => Enabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DisabledVisState => Enabled ? Visibility.Collapsed : Visibility.Visible;
    public string ToggleText => Enabled ? "禁用" : "启用";
}

/// <summary>
/// 性能优化页 VM（宿主 ViewModels/OptimizeViewModel 同源移植）：
/// 内存整理 / 进程 EcoQoS / 启动项管理（禁用·启用·恢复）。
/// 无设置与日志依赖（两服务均自包含），插件化无需存储适配；
/// 生命周期：每个导航新建 VM（构造即拉起进程/启动项两次刷新），视图 Unloaded 无在途写盘操作需取消。
/// </summary>
public partial class OptimizeViewModel : ObservableObject
{
    private readonly IMemoryOptimizeService _memory;
    private readonly IStartupListService _startup;

    public ObservableCollection<ProcessRowVm> MemoryProcesses { get; } = new();
    public ObservableCollection<ProcessRowVm> EcoProcesses { get; } = new();
    public ObservableCollection<StartupRowVm> StartupItems { get; } = new();

    [ObservableProperty] private bool _isMemoryBusy;
    /// <summary>内存整理确定性进度（0-100）。</summary>
    [ObservableProperty] private double _memoryProgress;
    public bool MemoryProgressVis => IsMemoryBusy && MemoryProgress > 0;
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
    partial void OnMemoryProgressChanged(double value) => OnPropertyChanged(nameof(MemoryProgressVis));
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
            // EcoReady 依赖 EcoProcesses.Count，但 ObservableCollection 填充不会自动通知绑定；
            // 不在此处补发通知的话，按钮 IsEnabled 停留在页首初始化的 false 永久禁用（进程节能「不能用」根因）
            OnPropertyChanged(nameof(EcoReady));
            if (MemoryResult.Length == 0) MemoryResult = "共展示内存占用最高的 25 个进程。勾选后点击“整理所选内存”。";
        }
        catch (Exception ex)
        {
            // 全局 UnhandledException 仅记日志且 e.Handled=true：命令内不接住的话用户点了毫无反应
            MemoryResult = $"进程列表获取失败：{ex.Message}（可重试或用任务管理器查看）";
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
        MemoryProgress = 0;
        try
        {
            // 逐进程确定性进度：进度条推进 + 当前进程名实时显示
            var progress = new Progress<TrimProgress>(p =>
            {
                MemoryProgress = p.Total <= 1 ? 0 : 100.0 * p.Done / p.Total;
                MemoryResult = $"正在整理 {p.Done + 1}/{p.Total}：{p.Name}…";
            });
            var r = await _memory.TrimAsync(pids, progress).ConfigureAwait(true);
            MemoryResult = $"整理完成：{r.Trimmed} 个进程，释放约 {FormatHelper.Bytes(r.FreedBytes)} 工作集（跳过 {r.Skipped} 个受保护进程）。进程未被结束，仅回收可回收页。";
            await RefreshMemoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex) { MemoryResult = $"内存整理失败：{ex.Message}"; }
        finally { IsMemoryBusy = false; MemoryProgress = 0; }
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
        catch (Exception ex) { EcoResult = $"节能设置失败：{ex.Message}"; }
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
        catch (Exception ex) { EcoResult = $"恢复调度失败：{ex.Message}"; }
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
        catch (Exception ex) { StartupResult = $"启动项读取失败：{ex.Message}"; }
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
        catch (Exception ex) { StartupResult = $"{(row.Enabled ? "禁用" : "启用")}失败：{ex.Message}"; }
        finally { IsStartupBusy = false; }
    }

    [RelayCommand]
    private void OpenTaskManager()
    {
        try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); } catch { }
    }
}
