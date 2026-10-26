using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Popup;

namespace QingShu.Toolkit.ViewModels;

public partial class WhitelistRowVm : ObservableObject
{
    private readonly PopupGuardViewModel _owner;

    public WhitelistRowVm(string name, PopupGuardViewModel owner) { Name = name; _owner = owner; }

    public string Name { get; }

    /// <summary>行模板内经 x:Bind Owner.Xxx 触达页面级命令（x:DataType 模板内无法直接引用页面）。</summary>
    public PopupGuardViewModel Owner => _owner;
}

/// <summary>最近拦截记录行（只读展示）。</summary>
public sealed class BlockedRecordVm
{
    public BlockedRecordVm(DateTime time, string title, string process)
    {
        TimeText = time.ToString("HH:mm:ss");
        Title = string.IsNullOrWhiteSpace(title) ? "（无标题窗口）" : title;
        ProcessText = $"{process}.exe";
    }

    public string TimeText { get; }
    public string Title { get; }
    public string ProcessText { get; }
}

/// <summary>
/// 弹窗拦截独立管理页 VM（历史版本入口分散在「性能优化」卡片与状态栏小弹窗，2026-10 应用户要求
/// 拆分为独立页面并统一管理）：开关 / 拦截计数 / 白名单增删 / 最近拦截记录，均与设置页共用同一份存储。
/// </summary>
public partial class PopupGuardViewModel : ObservableObject
{
    private readonly IPopupBlockerService _popup;
    private readonly ISettingsService _settings;

    public ObservableCollection<WhitelistRowVm> Whitelist { get; } = new();
    public ObservableCollection<BlockedRecordVm> Recent { get; } = new();

    [ObservableProperty] private bool _popupEnabled;
    [ObservableProperty] private string _popupCountText = "0";
    [ObservableProperty] private string _newWhitelistName = "";
    [ObservableProperty] private string _whitelistSummary = "";
    [ObservableProperty] private bool _hasRecent;

    /// <summary>徽章/空态用的反向布尔（WinUI x:Bind 自带 bool→Visibility 映射，避免引入转换器资源）。</summary>
    public bool IsRunning => PopupEnabled;
    public bool IsPaused => !PopupEnabled;
    public bool HasNoRecent => !HasRecent;

    partial void OnPopupEnabledChanged(bool value)
    {
        _popup.SetEnabled(value);
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
    }

    private void ReloadWhitelist()
    {
        Whitelist.Clear();
        foreach (var name in _settings.Current.PopupWhitelist.OrderBy(x => x))
            Whitelist.Add(new WhitelistRowVm(name, this));
        WhitelistSummary = Whitelist.Count == 0
            ? "白名单为空：所有新出现的可见窗口都会被拦截（不推荐）。请先添加教学/办公软件所在进程。"
            : $"共 {Whitelist.Count} 项，命中白名单的进程窗口不会被拦截。";
    }

    private void ReloadRecent()
    {
        Recent.Clear();
        foreach (var (time, title, proc) in _popup.Recent.Take(50))
            Recent.Add(new BlockedRecordVm(time, title, proc));
        HasRecent = Recent.Count > 0;
        OnPropertyChanged(nameof(HasNoRecent));
    }

    public PopupGuardViewModel(IPopupBlockerService popup, ISettingsService settings)
    {
        _popup = popup;
        _settings = settings;
        _popupEnabled = settings.Current.PopupBlockEnabled;
        _popup.Blocked += count => UiDispatcher.Run(() =>
        {
            PopupCountText = count.ToString();
            ReloadRecent();
        });
        PopupCountText = popup.BlockedCount.ToString();
        ReloadWhitelist();
        ReloadRecent();
        // 白名单与开关可能与设置页双向同步修改：设置变更时自动刷新，保持两处一致
        _settings.Changed += () =>
        {
            if (_settings.Current.PopupBlockEnabled != PopupEnabled) PopupEnabled = _settings.Current.PopupBlockEnabled;
            ReloadWhitelist();
        };
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
    private void ClearRecent()
    {
        // 仅清空本页展示，不动服务端计数（累计拦截数属于当日统计口径）
        Recent.Clear();
        HasRecent = false;
        OnPropertyChanged(nameof(HasNoRecent));
    }
}
