using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Update;

namespace QingShu.Toolkit.ViewModels;

/// <summary>设置页 VM：外观 / 网络 / 清理 / 功耗 / 拦截 / 更新 / 日志 / 数据。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IUpdateService _update;
    private readonly ILogService _log;

    [ObservableProperty] private int _themeIndex;          // 0=跟随系统 1=浅色 2=深色
    [ObservableProperty] private bool _useAcrylic;
    [ObservableProperty] private string _portalBaseUrl = "";
    [ObservableProperty] private bool _portalAutoLogin;
    [ObservableProperty] private string _portalUsername = "";
    [ObservableProperty] private string _portalPassword = "";
    [ObservableProperty] private string _authStatusUrl = "";
    [ObservableProperty] private string _authOkKeywords = "";
    [ObservableProperty] private string _intranetProbeHost = "";
    [ObservableProperty] private string _dnsPreferred = "";
    [ObservableProperty] private string _dnsAlternate = "";
    [ObservableProperty] private string _imOldDaysText = "";
    [ObservableProperty] private string _largeMinMbText = "";
    [ObservableProperty] private bool _deleteToRecycleBin;
    [ObservableProperty] private int _powerModeIndex;      // 0=自动 1=平衡 2=高性能 3=低功耗
    [ObservableProperty] private bool _applyEcoQosToSelf;
    [ObservableProperty] private bool _popupEnabled;
    [ObservableProperty] private bool _autoCheckUpdate;
    [ObservableProperty] private string _updateRepo = "";
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private bool _isUpdateBusy;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updateDownloaded;
    [ObservableProperty] private string _logRetentionText = "";
    [ObservableProperty] private string _logRootText = "";
    [ObservableProperty] private string _savedTip = "";
    [ObservableProperty] private string _versionText = AppVersion.DisplayText;

    private bool _loading = true;

    public SettingsViewModel(ISettingsService settings, IUpdateService update, ILogService log)
    {
        _settings = settings;
        _update = update;
        _log = log;
        LoadFromSettings();

        _update.StatusChanged += OnUpdateStatus;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        _themeIndex = s.ThemeMode switch { "light" => 1, "dark" => 2, _ => 0 };
        _useAcrylic = s.UseAcrylicBackdrop;
        _portalBaseUrl = s.PortalBaseUrl;
        _portalAutoLogin = s.PortalAutoLogin;
        _portalUsername = s.PortalUsername;
        _portalPassword = s.PortalPassword;
        _authStatusUrl = s.AuthStatusUrl;
        _authOkKeywords = s.AuthOkKeywords;
        _intranetProbeHost = s.IntranetProbeHost;
        _dnsPreferred = s.DnsPreferred;
        _dnsAlternate = s.DnsAlternate;
        _imOldDaysText = s.ImOldDays.ToString();
        _largeMinMbText = s.LargeFileMinMb.ToString();
        _deleteToRecycleBin = s.DeleteToRecycleBin;
        _powerModeIndex = s.PowerMode switch { "balanced" => 1, "performance" => 2, "eco" => 3, _ => 0 };
        _applyEcoQosToSelf = s.ApplyEcoQosToSelf;
        _popupEnabled = s.PopupBlockEnabled;
        _autoCheckUpdate = s.AutoCheckUpdate;
        _updateRepo = s.UpdateRepo;
        _logRetentionText = s.LogRetentionDays.ToString();
        _logRootText = _log.Root;
        _loading = false;
    }

    private async Task SaveAsync()
    {
        if (_loading) return;
        var s = _settings.Current;
        s.ThemeMode = ThemeIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        s.UseAcrylicBackdrop = UseAcrylic;
        s.PortalBaseUrl = PortalBaseUrl?.Trim() ?? "";
        s.PortalAutoLogin = PortalAutoLogin;
        s.PortalUsername = PortalUsername?.Trim() ?? "";
        s.PortalPassword = PortalPassword ?? ""; // 密码不做 Trim（可能合法含首尾空格）
        s.AuthStatusUrl = AuthStatusUrl?.Trim() ?? "";
        s.AuthOkKeywords = AuthOkKeywords?.Trim() ?? "success|在线|已认证|认证成功";
        s.IntranetProbeHost = IntranetProbeHost?.Trim() ?? "";
        s.DnsPreferred = DnsPreferred?.Trim() ?? "223.5.5.5";
        s.DnsAlternate = DnsAlternate?.Trim() ?? "119.29.29.29";
        s.ImOldDays = ParseInt(_imOldDaysText, 180);
        s.LargeFileMinMb = ParseInt(_largeMinMbText, 100);
        s.DeleteToRecycleBin = DeleteToRecycleBin;
        s.PowerMode = PowerModeIndex switch { 1 => "balanced", 2 => "performance", 3 => "eco", _ => "auto" };
        s.ApplyEcoQosToSelf = ApplyEcoQosToSelf;
        s.PopupBlockEnabled = PopupEnabled;
        s.AutoCheckUpdate = AutoCheckUpdate;
        s.UpdateRepo = UpdateRepo?.Trim() ?? s.UpdateRepo;
        s.LogRetentionDays = Math.Clamp(ParseInt(_logRetentionText, 90), 7, 3650);
        await _settings.SaveAsync().ConfigureAwait(true);
        SavedTip = $"{DateTime.Now:HH:mm:ss} 设置已保存并即时生效。";
    }

    private static int ParseInt(string? s, int fallback) => int.TryParse(s?.Trim(), out var v) && v > 0 ? v : fallback;

    // ---- 更新 ----
    private void OnUpdateStatus(UpdateStatus st)
    {
        UpdateStatusText = st.Message;
        UpdateProgress = st.ProgressPercent;
        UpdateAvailable = st.Phase == UpdatePhase.Available;
        UpdateDownloaded = st.Phase == UpdatePhase.ReadyToInstall;
        IsUpdateBusy = st.Phase is UpdatePhase.Checking or UpdatePhase.Downloading;
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsUpdateBusy) return;
        await _update.CheckAsync(manual: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        var rel = _update.Current.Release;
        if (rel == null) return;
        await _update.DownloadAsync(rel).ConfigureAwait(true);
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (_update.Current.DownloadedFile.Length > 0)
            _update.Install(_update.Current.DownloadedFile);
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        _settings.Reset();
        LoadFromSettings();
        await _settings.SaveAsync().ConfigureAwait(true);
        SavedTip = "已恢复默认设置。";
    }

    partial void OnThemeIndexChanged(int value) => _ = SaveAsync();
    partial void OnUseAcrylicChanged(bool value) => _ = SaveAsync();
    partial void OnDeleteToRecycleBinChanged(bool value) => _ = SaveAsync();
    partial void OnPowerModeIndexChanged(int value) => _ = SaveAsync();
    partial void OnApplyEcoQosToSelfChanged(bool value) => _ = SaveAsync();
    partial void OnPopupEnabledChanged(bool value) => _ = SaveAsync();
    partial void OnAutoCheckUpdateChanged(bool value) => _ = SaveAsync();

    // ---- 文本框类设置同样即时保存（TwoWay 绑定在失焦时回写，此前缺失处理器
    //      导致门户地址/DNS/阈值/仓库/日志天数等“改了不生效”）----
    partial void OnPortalBaseUrlChanged(string value) => _ = SaveAsync();
    partial void OnPortalAutoLoginChanged(bool value) => _ = SaveAsync();
    partial void OnPortalUsernameChanged(string value) => _ = SaveAsync();
    partial void OnPortalPasswordChanged(string value) => _ = SaveAsync();
    partial void OnAuthStatusUrlChanged(string value) => _ = SaveAsync();
    partial void OnAuthOkKeywordsChanged(string value) => _ = SaveAsync();
    partial void OnIntranetProbeHostChanged(string value) => _ = SaveAsync();
    partial void OnDnsPreferredChanged(string value) => _ = SaveAsync();
    partial void OnDnsAlternateChanged(string value) => _ = SaveAsync();
    partial void OnImOldDaysTextChanged(string value) => _ = SaveAsync();
    partial void OnLargeMinMbTextChanged(string value) => _ = SaveAsync();
    partial void OnUpdateRepoChanged(string value) => _ = SaveAsync();
    partial void OnLogRetentionTextChanged(string value) => _ = SaveAsync();

    [RelayCommand]
    private async Task SaveManualAsync() => await SaveAsync().ConfigureAwait(true);
}
