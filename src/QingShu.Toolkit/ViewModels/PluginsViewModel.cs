using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Plugins;

namespace QingShu.Toolkit.ViewModels;

/// <summary>
/// 插件管理 VM：扫描插件目录 → 官方验签 → 加载/卸载/启用禁用 → 快捷操作。
/// 全部命令自带 try/catch 并把结果写入界面可见文案（全局异常兜底只记日志，不可依赖）。
/// </summary>
public partial class PluginsViewModel : ObservableObject
{
    private readonly PluginManager _manager;
    private readonly ILogService _log;

    public ObservableCollection<PluginItemVm> Plugins { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在初始化…";

    // 插件自绘面板（IQToolkitUiProvider）：同一时刻最多挂载一个
    [ObservableProperty] private bool _panelOpen;
    [ObservableProperty] private string _panelTitle = "";
    [ObservableProperty] private FrameworkElement? _panelContent;
    private string? _openPanelFolder;

    public bool PanelOpenVis => PanelOpen;

    public string PluginsRoot => _manager.PluginsRoot;
    public string KeyFingerprint => _manager.KeyFingerprint;
    public bool IsNotBusy => !IsBusy;
    public bool EmptyVis => Plugins.Count == 0 && !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(EmptyVis));
    }

    public PluginsViewModel(PluginManager manager, ILogService log)
    {
        _manager = manager;
        _log = log;
    }

    // ================= 扫描 =================

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "正在扫描插件目录并验签…";
            var entries = await _manager.ScanAsync().ConfigureAwait(true);
            Rebuild(entries);
            StatusText = $"扫描完成：共 {entries.Count} 个插件（运行中 {entries.Count(e => e.Loaded)} · 官方验签通过 {entries.Count(e => e.VerifyState == PluginVerifyState.Ok)}）。";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
            _log.App(LogLevel.Error, "plugins", "插件扫描异常", new { ex = ex.ToString() });
        }
        finally { IsBusy = false; }
    }

    private void Rebuild(List<PluginEntry> entries)
    {
        var carry = Plugins.ToDictionary(p => p.FolderName, p => p, StringComparer.OrdinalIgnoreCase);
        Plugins.Clear();
        foreach (var e in entries)
        {
            var row = new PluginItemVm(e, this);
            if (carry.TryGetValue(e.FolderName, out var old)) row.CarryOver(old);
            Plugins.Add(row);
        }
        OnPropertyChanged(nameof(EmptyVis));
        // 已打开面板的插件若不再运行（被卸载/目录移除/验签失效），同步收起面板
        if (_openPanelFolder is { } open
            && !entries.Any(e => e.FolderName.Equals(open, StringComparison.OrdinalIgnoreCase) && e.Loaded))
        {
            ClosePanel();
            StatusText = "面板所属插件已不在运行，面板已收起。";
        }
    }

    // ================= 插件面板 =================

    /// <summary>打开/收起指定插件的自绘面板（已开同一插件时再次点击即收起）。</summary>
    internal void TogglePanel(PluginItemVm row)
    {
        try
        {
            if (PanelOpen && string.Equals(_openPanelFolder, row.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                ClosePanel();
                StatusText = $"已收起「{row.DisplayName}」面板。";
                return;
            }
            var view = _manager.CreatePanelView(row.FolderName);
            if (view == null)
            {
                StatusText = $"「{row.DisplayName}」无法提供面板（未运行、无面板能力或创建失败，详见应用日志）。";
                return;
            }
            PanelContent = view;
            PanelTitle = $"{row.DisplayName} · 面板";
            _openPanelFolder = row.FolderName;
            PanelOpen = true;
            StatusText = $"已打开「{row.DisplayName}」面板（面板关闭不影响插件后台运行）。";
        }
        catch (Exception ex)
        {
            StatusText = $"打开插件面板失败：{ex.Message}";
            _log.App(LogLevel.Error, "plugins", "打开插件面板异常", new { id = row.FolderName, ex = ex.ToString() });
        }
    }

    /// <summary>收起当前面板（卸载插件前必须调用：视图引用会阻止 ALC 完整回收）。</summary>
    internal void ClosePanelFor(PluginItemVm row)
    {
        if (_openPanelFolder != null
            && _openPanelFolder.Equals(row.FolderName, StringComparison.OrdinalIgnoreCase))
        {
            ClosePanel();
        }
    }

    private void ClosePanel()
    {
        PanelContent = null;
        PanelTitle = "";
        _openPanelFolder = null;
        PanelOpen = false;
    }

    /// <summary>页面「收起面板」按钮入口（UI 线程调用）。</summary>
    public void ClosePanelFromUi()
    {
        try
        {
            if (!PanelOpen) return;
            ClosePanel();
            StatusText = "已收起插件面板。";
        }
        catch (Exception ex)
        {
            StatusText = $"收起面板失败：{ex.Message}";
        }
    }

    // ================= 批量加载 =================

    [RelayCommand]
    private async Task LoadEnabledAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var pending = Plugins.Where(p => p.Enabled && p.VerifyOk && !p.IsLoaded).ToList();
            if (pending.Count == 0) { StatusText = "没有待加载的插件（全部已加载，或未启用/未通过验签）。"; return; }
            int ok = 0, fail = 0;
            foreach (var row in pending)
            {
                try
                {
                    var msg = await _manager.LoadAsync(row.FolderName).ConfigureAwait(true);
                    row.MarkLoaded(msg);
                    ok++;
                }
                catch (Exception ex)
                {
                    row.MarkError(ex.Message);
                    fail++;
                }
            }
            StatusText = $"批量加载完成：成功 {ok} · 失败 {fail}。失败原因见对应卡片红字。";
        }
        catch (Exception ex)
        {
            StatusText = $"批量加载失败：{ex.Message}";
            _log.App(LogLevel.Error, "plugins", "插件批量加载异常", new { ex = ex.ToString() });
        }
        finally { IsBusy = false; }
    }

    // ================= 目录 =================

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_manager.PluginsRoot);
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_manager.PluginsRoot}\"",
                UseShellExecute = true
            });
            StatusText = "已在资源管理器中打开插件目录。";
        }
        catch (Exception ex)
        {
            StatusText = $"打开插件目录失败：{ex.Message}";
            _log.App(LogLevel.Error, "plugins", "打开插件目录异常", new { ex = ex.ToString() });
        }
    }

    // ================= 行操作（供行模型回调） =================

    internal Task<string> ManagerLoadAsync(PluginItemVm row) => _manager.LoadAsync(row.FolderName);
    internal Task<string> ManagerUnloadAsync(PluginItemVm row) => _manager.UnloadAsync(row.FolderName);

    internal async Task SetEnabledAsync(PluginItemVm row, bool enabled)
    {
        try
        {
            await _manager.SetEnabledAsync(row.FolderName, enabled).ConfigureAwait(true);
            StatusText = $"「{row.DisplayName}」已{(enabled ? "启用" : "停用")}（停用后重新扫描/重启即不再自动加载）。";
        }
        catch (Exception ex)
        {
            row.RollbackEnabled();
            StatusText = $"保存启用状态失败：{ex.Message}";
            _log.App(LogLevel.Error, "plugins", "插件启用状态保存异常",
                new { id = row.FolderName, ex = ex.ToString() });
        }
    }

    /// <summary>页面 Unloaded 收尾（当前无在途句柄，保持与其它页一致的生命周期约定）。</summary>
    public void Dispose() { }
}

// ================= XAML 绑定行模型 =================

/// <summary>插件卡片行。</summary>
public sealed partial class PluginItemVm : ObservableObject
{
    private readonly PluginsViewModel _parent;
    private bool _suspendToggle;

    public string FolderName { get; }
    public string Folder { get; }
    public string DisplayName { get; }
    public string IdText { get; }
    public string VersionText { get; }
    public string AuthorText { get; }
    public string MetaText => $"id {IdText} · 作者 {AuthorText}";
    public string Description { get; }
    public string PermissionsText { get; }
    public bool VerifyOk { get; }
    public string VerifyText { get; }
    public bool HasPanel { get; }
    public ObservableCollection<QuickActionVm> QuickActions { get; } = new();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _runtimeText = "";
    [ObservableProperty] private string _errorText = "";

    public bool VerifyOkVis => VerifyOk;
    public bool VerifyBadVis => !VerifyOk;
    public bool LoadedVis => IsLoaded;
    public bool UnloadedVis => !IsLoaded;
    public bool CanLoad => !IsLoaded && VerifyOk;
    public bool CanUnload => IsLoaded;
    public bool HasRuntime => RuntimeText.Length > 0;
    public bool HasError => ErrorText.Length > 0;

    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanUnload));
        OnPropertyChanged(nameof(LoadedVis));
        OnPropertyChanged(nameof(UnloadedVis));
    }
    partial void OnRuntimeTextChanged(string value) => OnPropertyChanged(nameof(HasRuntime));
    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsEnabledChanged(bool value)
    {
        if (_suspendToggle) return;
        _ = _parent.SetEnabledAsync(this, value);
    }

    public PluginItemVm(PluginEntry e, PluginsViewModel parent)
    {
        _parent = parent;
        FolderName = e.FolderName;
        Folder = e.Folder;
        DisplayName = !string.IsNullOrWhiteSpace(e.Manifest?.Name) ? e.Manifest!.Name : e.FolderName;
        IdText = e.Manifest?.Id ?? "—";
        VersionText = string.IsNullOrWhiteSpace(e.Manifest?.Version) ? "—" : e.Manifest!.Version;
        AuthorText = e.Manifest?.Author ?? "";
        Description = e.Manifest?.Description ?? "";
        PermissionsText = e.Manifest is { Permissions.Count: > 0 }
            ? "权限：" + string.Join("、", e.Manifest.Permissions)
            : "权限：无（仅基础生命周期）";
        VerifyOk = e.VerifyState == PluginVerifyState.Ok;
        VerifyText = VerifyOk ? "官方签名 · 验签通过" : e.VerifyError;
        HasPanel = e.HasUiPanel;
        _suspendToggle = true;
        IsEnabled = e.Enabled;
        _suspendToggle = false;
        IsLoaded = e.Loaded;
        foreach (var a in e.QuickActions) QuickActions.Add(new QuickActionVm(a));
    }

    /// <summary>重扫描后把上一行瞬态文案接过来（运行结果/动作输出不丢）。</summary>
    public void CarryOver(PluginItemVm old)
    {
        RuntimeText = old.RuntimeText;
        if (ErrorText.Length == 0) ErrorText = old.ErrorText;
        foreach (var qa in QuickActions)
        {
            var match = old.QuickActions.FirstOrDefault(o => o.Name == qa.Name);
            if (match != null) qa.LastText = match.LastText;
        }
    }

    public void MarkLoaded(string message)
    {
        IsLoaded = true;
        RuntimeText = message;
        ErrorText = "";
    }

    public void MarkUnloaded(string message)
    {
        IsLoaded = false;
        RuntimeText = message;
    }

    public void MarkError(string message) => ErrorText = message;

    /// <summary>启用开关保存失败时由 VM 回滚勾选（不触发保存）。</summary>
    public void RollbackEnabled()
    {
        _suspendToggle = true;
        IsEnabled = !IsEnabled;
        _suspendToggle = false;
    }

    [RelayCommand]
    private void Panel() => _parent.TogglePanel(this);

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!CanLoad) return;
        ErrorText = "";
        RuntimeText = "正在加载…";
        try
        {
            var msg = await _parent.ManagerLoadAsync(this).ConfigureAwait(true);
            MarkLoaded(msg);
        }
        catch (Exception ex)
        {
            RuntimeText = "";
            MarkError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task UnloadAsync()
    {
        if (!CanUnload) return;
        _parent.ClosePanelFor(this); // 面板视图持有插件对象引用，卸载前必须摘除
        try
        {
            var msg = await _parent.ManagerUnloadAsync(this).ConfigureAwait(true);
            MarkUnloaded(msg);
        }
        catch (Exception ex)
        {
            MarkError(ex.Message);
        }
    }
}

/// <summary>插件快捷动作（由插件经 IPluginContext.RegisterQuickAction 注册）。</summary>
public sealed partial class QuickActionVm : ObservableObject
{
    private readonly PluginQuickAction _action;

    public string Name { get; }

    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private bool _isRunning;

    public bool HasLastText => LastText.Length > 0;
    public bool NotRunning => !IsRunning;

    partial void OnLastTextChanged(string value) => OnPropertyChanged(nameof(HasLastText));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(NotRunning));

    public QuickActionVm(PluginQuickAction action)
    {
        _action = action;
        Name = action.Name;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        LastText = "执行中…";
        try
        {
            // 插件回调可能同步阻塞 → 丢线程池；结果回 UI 线程渲染（SynchronizationContext 已安装）
            var res = await Task.Run(() => _action.Invoke(CancellationToken.None)).ConfigureAwait(true);
            LastText = string.IsNullOrWhiteSpace(res) ? "完成。" : res!.Trim();
        }
        catch (Exception ex)
        {
            LastText = $"执行失败：{ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }
}
