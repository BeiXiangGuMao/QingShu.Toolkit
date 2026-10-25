using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Interop;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Cleanup;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.ViewModels;

// ================= 常规清理目标行 =================
public partial class CleanTargetVm : ObservableObject
{
    public CleanTargetVm(CleanTargetDef def)
    {
        Def = def;
        _isSelected = def.DefaultSelected;
    }

    public CleanTargetDef Def { get; }
    /// <summary>最近一次扫描结果（供清理引擎携带真实大小/数量；传零值会导致回收站等目标被误判为空而跳过）。</summary>
    public CleanScanTarget? LastScan { get; set; }
    public string Name => Def.Name;
    public string Description => Def.Description;
    public string RiskText => CleanRiskText.Of(Def.Risk);
    public string AdminText => Def.NeedsAdmin ? "需管理员" : "";
    public bool AdminVis => Def.NeedsAdmin;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isScanned;
    [ObservableProperty] private string _sizeText = "待扫描";
    [ObservableProperty] private string _fileText = "";
    [ObservableProperty] private bool _isScanning;
}

// ================= IM / 大文件行 =================
public partial class ImFileVm : ObservableObject
{
    public ImFileVm(ImFileItem item)
    {
        Item = item;
        _isSelected = true;
    }

    /// <summary>账号分组头行（不可勾选，仅展示该账号汇总）。</summary>
    public ImFileVm(string headerText)
    {
        // 头行不展示文件字段（FileVis=false），仅因 required 成员而显式补齐空值
        Item = new ImFileItem
        {
            From = "",
            Account = "",
            Kind = "",
            Path = "",
            SizeBytes = 0,
            LastWrite = DateTime.MinValue
        };
        IsHeader = true;
        HeaderText = headerText;
        _isSelected = false;
    }

    public ImFileItem Item { get; }
    public bool IsHeader { get; }
    public string HeaderText { get; } = "";
    public bool HeaderVis => IsHeader;
    public bool FileVis => !IsHeader;
    public string FromText => Item.From;
    public string KindText => Item.Kind;
    public string AccountText => Item.Account;
    public string Name => System.IO.Path.GetFileName(Item.Path);
    public string PathText => Item.Path;
    public string SizeText => FormatHelper.Bytes(Item.SizeBytes);
    public string TimeText => $"{Item.LastWrite:yyyy-MM-dd}（{Item.AgeDays} 天前）";

    [ObservableProperty] private bool _isSelected;
}

public partial class LargeFileVm : ObservableObject
{
    public LargeFileVm(LargeFileInfo item)
    {
        Item = item;
        _isSelected = false;
    }

    public LargeFileInfo Item { get; }
    public string Name => Item.Name;
    public string PathText => Item.Dir;
    public string ExtText => Item.Ext;
    public string SizeText => FormatHelper.Bytes(Item.SizeBytes);
    public string TimeText => Item.LastWrite.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty] private bool _isSelected;
}

/// <summary>磁盘清理页 VM：常规清理 / 微信 / QQ / 大文件 四个标签页。</summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupService _cleanup;
    private readonly IImScanService _im;
    private readonly ILargeFileScanService _large;
    private readonly ISettingsService _settings;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CleanTargetVm> RegularTargets { get; } = new();

    // 微信 / QQ / 大文件列表整体替换而非逐条 Add：几千条逐项 CollectionChanged 会造成 UI 布局风暴（历史卡顿主因），
    // 页面绑定均为 Mode=OneWay，替换实例可正常刷新
    private ObservableCollection<ImFileVm> _weChatFiles = new();
    public ObservableCollection<ImFileVm> WeChatFiles { get => _weChatFiles; private set => SetProperty(ref _weChatFiles, value); }

    private ObservableCollection<ImFileVm> _qqFiles = new();
    public ObservableCollection<ImFileVm> QqFiles { get => _qqFiles; private set => SetProperty(ref _qqFiles, value); }

    private ObservableCollection<LargeFileVm> _largeFiles = new();
    public ObservableCollection<LargeFileVm> LargeFiles { get => _largeFiles; private set => SetProperty(ref _largeFiles, value); }

    public ObservableCollection<char> Drives { get; } = new();

    // ---- 常规 ----
    [ObservableProperty] private bool _isRegularBusy;
    [ObservableProperty] private double _regularProgress;
    [ObservableProperty] private string _regularStatus = "点击“开始扫描”统计可清理空间";
    [ObservableProperty] private string _regularSelectedText = "已选 0 项";
    [ObservableProperty] private string _regularResult = "";

    // ---- IM ----
    [ObservableProperty] private bool _isWeChatBusy;
    [ObservableProperty] private bool _isQqBusy;
    [ObservableProperty] private string _weChatStatus = "";
    [ObservableProperty] private string _qqStatus = "";
    [ObservableProperty] private string _imOldDaysText;
    [ObservableProperty] private string _imSelectedText = "";

    // ---- 大文件 ----
    [ObservableProperty] private bool _isLargeBusy;
    [ObservableProperty] private string _largeStatus = "";
    [ObservableProperty] private string _largeMinMbText;
    [ObservableProperty] private int _driveIndex;
    [ObservableProperty] private string _largeSelectedText = "";
    [ObservableProperty] private bool _hasDrives;

    // ---- 通用 ----
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _deleteToRecycleBin;

    public CleanupViewModel(ICleanupService cleanup, IImScanService im, ILargeFileScanService large,
        ISettingsService settings, ILogService log)
    {
        _cleanup = cleanup;
        _im = im;
        _large = large;
        _settings = settings;
        _log = log;
        foreach (var t in cleanup.Targets)
        {
            var row = new CleanTargetVm(t);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanTargetVm.IsScanned)) OnPropertyChanged(nameof(RegularReady));
                if (e.PropertyName == nameof(CleanTargetVm.IsSelected)) UpdateRegularSelected();
            };
            RegularTargets.Add(row);
        }
        _imOldDaysText = settings.Current.ImOldDays.ToString();
        _largeMinMbText = settings.Current.LargeFileMinMb.ToString();
        DeleteToRecycleBin = settings.Current.DeleteToRecycleBin;
        _ = LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        var drives = await _large.GetFixedDrivesAsync().ConfigureAwait(true);
        Drives.Clear();
        foreach (var d in drives) Drives.Add(d);
        HasDrives = Drives.Count > 0;
        DriveIndex = 0;
    }

    partial void OnDeleteToRecycleBinChanged(bool value)
    {
        _settings.Current.DeleteToRecycleBin = value;
        _ = _settings.SaveAsync();
    }

    // 历史 bug：清理页的阈值改动从未落盘，与设置页的同名设置各存各的（用户感知“两个设置”互不同步）。
    // 现与设置页共用同一份存储：仅在输入合法且与现值不同时写入，避免半输入态污染设置。
    partial void OnImOldDaysTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), out var v) && v > 0)
        {
            var clamped = Math.Clamp(v, 1, 3650);
            if (_settings.Current.ImOldDays != clamped)
            {
                _settings.Current.ImOldDays = clamped;
                _ = _settings.SaveAsync();
            }
        }
    }

    partial void OnLargeMinMbTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), out var v) && v > 0)
        {
            var clamped = Math.Clamp(v, 1, 1024 * 1024);
            if (_settings.Current.LargeFileMinMb != clamped)
            {
                _settings.Current.LargeFileMinMb = clamped;
                _ = _settings.SaveAsync();
            }
        }
    }

    // ================= 常规清理 =================

    [RelayCommand]
    private async Task ScanRegularAsync()
    {
        if (IsRegularBusy) return;
        IsRegularBusy = true;
        RegularProgress = 0;
        RegularStatus = "正在扫描…";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        try
        {
            var targets = RegularTargets.Select(t => t.Def).ToList();
            var progress = new Progress<CleanScanTarget>(t =>
            {
                var vm = RegularTargets.FirstOrDefault(x => x.Def.Id == t.Def.Id);
                if (vm != null)
                {
                    vm.LastScan = t;
                    vm.IsScanned = true;
                    vm.SizeText = FormatHelper.Bytes(t.SizeBytes);
                    vm.FileText = $"{t.FileCount} 项";
                    RegularProgress = 100.0 * RegularTargets.Count(x => x.IsScanned) / RegularTargets.Count;
                }
            });
            RegularProgress = 0;
            await _cleanup.ScanAsync(targets, progress, _cts.Token).ConfigureAwait(true);
            UpdateRegularSelected();
            RegularStatus = "扫描完成，请确认要清理的项目后点击“开始清理”。";
        }
        catch (OperationCanceledException) { RegularStatus = "扫描已取消。"; }
        catch (Exception ex) { RegularStatus = $"扫描失败：{ex.Message}"; }
        finally { IsRegularBusy = false; }
    }

    [RelayCommand]
    private void SelectAllRegular() { foreach (var t in RegularTargets) if (t.IsScanned) t.IsSelected = true; UpdateRegularSelected(); }

    [RelayCommand]
    private void SelectNoneRegular() { foreach (var t in RegularTargets) t.IsSelected = false; UpdateRegularSelected(); }

    [RelayCommand]
    private async Task CleanRegularAsync()
    {
        if (IsRegularBusy) return;
        var selected = RegularTargets.Where(t => t.IsSelected && t.IsScanned).ToList();
        if (selected.Count == 0) return;
        IsRegularBusy = true;
        RegularResult = "";
        var succeeded = false;
        string? completion = null;
        try
        {
            // 必须携带扫描结果：回收站目标依赖 SizeBytes 判断空/非空，传零值会被误判“已是空的”直接跳过（历史 bug）
            var scan = selected.Select(vm => vm.LastScan ?? new CleanScanTarget(vm.Def, 0, 0)).ToList();
            var log = new Progress<string>(line => RegularResult = line);
            var report = await _cleanup.CleanAsync(scan, DeleteToRecycleBin, log, CancellationToken.None).ConfigureAwait(true);
            completion = $"清理完成：{report.ToSummary()}" +
                         (DeleteToRecycleBin && report.FreedBytes > 0 ? "（已移入回收站，清空回收站后才真正释放磁盘空间）" : "");
            RegularStatus = completion;
            LastResult = $"{DateTime.Now:HH:mm} 清理释放 {FormatHelper.Bytes(report.FreedBytes)}";
            succeeded = true;
        }
        catch (Exception ex)
        {
            RegularStatus = $"清理失败：{ex.Message}";
        }
        finally { IsRegularBusy = false; }
        // 重扫必须等 busy 复位后执行：ScanRegularAsync 有 busy 保护，内联 await 会被静默跳过
        // （历史 bug：清理后列表不刷新，仍显示清理前大小，观感“没清理”）
        if (succeeded)
        {
            await ScanRegularAsync().ConfigureAwait(true);
            // 重扫会改写 RegularStatus：若重扫顺利结束（“扫描完成…”），回写清理结果，
            // 避免“清理完成”提示被覆盖（历史观感：删了也看不到结果）；重扫失败/取消则保留其提示。
            if (completion != null && RegularStatus.StartsWith("扫描完成", StringComparison.Ordinal))
                RegularStatus = completion;
        }
    }

    private void UpdateRegularSelected()
    {
        RegularSelectedText = $"已选 {RegularTargets.Count(x => x.IsSelected && x.IsScanned)} 项";
    }

    // ---- 页面展示辅助 ----
    public bool RegularReady => !IsRegularBusy && RegularTargets.Any(x => x.IsScanned);
    /// <summary>扫描按钮独立使能：首次使用（尚无扫描结果）也必须可以点（旧绑定误用 RegularReady 导致永远禁用）。</summary>
    public bool RegularCanScan => !IsRegularBusy;
    public bool RegularBusyVis => IsRegularBusy;

    partial void OnIsRegularBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(RegularBusyVis));
        OnPropertyChanged(nameof(RegularReady));
        OnPropertyChanged(nameof(RegularCanScan));
    }

    // ================= 微信 / QQ =================

    [RelayCommand]
    private async Task ScanWeChatAsync()
    {
        if (IsWeChatBusy) return;
        IsWeChatBusy = true;
        WeChatFiles.Clear();
        WeChatStatus = "正在扫描微信目录…";
        try
        {
            var files = await _im.ScanWeChatAsync(ParseDays(_imOldDaysText), CancellationToken.None).ConfigureAwait(true);
            WeChatFiles = new ObservableCollection<ImFileVm>(BuildGroupedRows(files, "微信", OnImRowSelectionChanged));
            UpdateImSelected();
            WeChatStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(_imOldDaysText)} 天的微信文件（也可能修改过存储位置，可用大文件扫描兜底）。"
                : $"发现 {files.Count} 个久远文件，共 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}。已默认全选，请逐项确认后删除。";
        }
        catch (Exception ex) { WeChatStatus = $"扫描失败：{ex.Message}"; }
        finally { IsWeChatBusy = false; }
    }

    [RelayCommand]
    private async Task ScanQqAsync()
    {
        if (IsQqBusy) return;
        IsQqBusy = true;
        QqFiles.Clear();
        QqStatus = "正在扫描 QQ 目录…";
        try
        {
            var files = await _im.ScanQQAsync(ParseDays(_imOldDaysText), CancellationToken.None).ConfigureAwait(true);
            QqFiles = new ObservableCollection<ImFileVm>(BuildGroupedRows(files, "QQ", OnImRowSelectionChanged));
            UpdateImSelected();
            QqStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(_imOldDaysText)} 天的 QQ 文件。"
                : $"发现 {files.Count} 个久远文件，共 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}。已默认全选，请逐项确认后删除。";
        }
        catch (Exception ex) { QqStatus = $"扫描失败：{ex.Message}"; }
        finally { IsQqBusy = false; }
    }

    [RelayCommand]
    private void SelectAllIm()
    {
        // 批量选择时挂起逐行事件：否则每行 PropertyChanged 都触发一次 O(n) 全表重算，全选即 O(n²) 卡顿
        _suppressImSelectionEvents = true;
        try
        {
            foreach (var f in WeChatFiles) if (!f.IsHeader) f.IsSelected = true;
            foreach (var f in QqFiles) if (!f.IsHeader) f.IsSelected = true;
        }
        finally { _suppressImSelectionEvents = false; }
        UpdateImSelected();
    }

    [RelayCommand]
    private void SelectNoneIm()
    {
        _suppressImSelectionEvents = true;
        try
        {
            foreach (var f in WeChatFiles) if (!f.IsHeader) f.IsSelected = false;
            foreach (var f in QqFiles) if (!f.IsHeader) f.IsSelected = false;
        }
        finally { _suppressImSelectionEvents = false; }
        UpdateImSelected();
    }

    private bool _suppressImSelectionEvents;

    private void OnImRowSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressImSelectionEvents) return;
        if (e.PropertyName == nameof(ImFileVm.IsSelected)) UpdateImSelected();
    }

    /// <summary>按账号分组构建行列表；调用方整体替换集合，避免逐条 Add 触发数千次 CollectionChanged 卡死 UI。</summary>
    private static List<ImFileVm> BuildGroupedRows(List<ImFileItem> files, string label, PropertyChangedEventHandler onSelectionChanged)
    {
        var rows = new List<ImFileVm>();
        foreach (var g in files.GroupBy(f => f.Account).OrderByDescending(g => g.Sum(f => f.SizeBytes)))
        {
            rows.Add(new ImFileVm($"{label}账号：{g.Key}  ·  {g.Count()} 个文件  ·  共 {FormatHelper.Bytes(g.Sum(f => f.SizeBytes))}"));
            foreach (var f in g.OrderByDescending(f => f.SizeBytes))
            {
                var row = new ImFileVm(f);
                row.PropertyChanged += onSelectionChanged;
                rows.Add(row);
            }
        }
        return rows;
    }

    private void UpdateImSelected()
    {
        var count = WeChatFiles.Count(x => x.IsSelected && !x.IsHeader) + QqFiles.Count(x => x.IsSelected && !x.IsHeader);
        var bytes = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes)
                  + QqFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes);
        ImSelectedText = $"已选 {count} 项 · {FormatHelper.Bytes(bytes)}";
    }

    private bool _imDeleteBusy;

    [RelayCommand]
    private async Task DeleteImAsync()
    {
        if (_imDeleteBusy) return;
        var paths = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)
            .Concat(QqFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)).ToList();
        if (paths.Count == 0) return;
        _imDeleteBusy = true;
        try
        {
            var (ok, failed, freed) = await _im.DeleteAsync(paths, DeleteToRecycleBin, CancellationToken.None).ConfigureAwait(true);
            var mode = DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）";
            var status = $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{mode}";
            if (failed > 0 && ok == 0)
                status += "。本次没有任何文件被删除（全部被占用或已不存在）";
            else if (failed > 0 && AnyImRunning())
                status += "；微信/QQ 正在运行，失败项多为其正在使用的文件，完全退出后重试可继续清理";
            QqStatus = "";
            // 先重扫刷新列表（内部会写扫描状态），再回写删除结果，避免“删除完成”提示被覆盖
            await ScanWeChatAsync().ConfigureAwait(true);
            _ = ScanQqAsync();
            WeChatStatus = status;
        }
        catch (Exception ex)
        {
            WeChatStatus = $"删除失败：{ex.Message}";
        }
        finally { _imDeleteBusy = false; }
    }

    // ================= 大文件 =================

    [RelayCommand]
    private async Task ScanLargeAsync()
    {
        if (IsLargeBusy || !HasDrives) return;
        IsLargeBusy = true;
        LargeFiles.Clear();
        LargeStatus = "正在扫描（大分区可能需要 1-2 分钟）…";
        try
        {
            var drive = Drives.ElementAtOrDefault(DriveIndex);
            if (drive == default) return;
            var files = await _large.ScanAsync(drive, ParseInt(_largeMinMbText, 100), CancellationToken.None).ConfigureAwait(true);
            LargeFiles = new ObservableCollection<LargeFileVm>(files.Take(200).Select(f => new LargeFileVm(f)));
            UpdateLargeSelected();
            LargeStatus = $"发现 {files.Count} 个 ≥{ParseInt(_largeMinMbText, 100)}MB 的文件，展示前 {LargeFiles.Count} 个。请勾选确认后删除。";
        }
        catch (Exception ex) { LargeStatus = $"扫描失败：{ex.Message}"; }
        finally { IsLargeBusy = false; }
    }

    [RelayCommand]
    private void OpenContainingFolder(object? param)
    {
        if (param is string path && File.Exists(path))
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
    }

    private bool _largeDeleteBusy;

    [RelayCommand]
    private async Task DeleteLargeAsync()
    {
        if (_largeDeleteBusy) return;
        var paths = LargeFiles.Where(x => x.IsSelected).Select(x => x.Item.Path).ToList();
        if (paths.Count == 0) return;
        _largeDeleteBusy = true;
        try
        {
            var (ok, failed, freed) = await _large.DeleteAsync(paths, DeleteToRecycleBin, CancellationToken.None).ConfigureAwait(true);
            LargeStatus = $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{(DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）")}"
                          + (failed > 0 && ok == 0 ? "。本次没有任何文件被删除（文件被占用或已不存在）" : "");
            _ = ScanLargeAsync();
        }
        catch (Exception ex)
        {
            LargeStatus = $"删除失败：{ex.Message}";
        }
        finally { _largeDeleteBusy = false; }
    }

    private void UpdateLargeSelected()
    {
        var count = LargeFiles.Count(x => x.IsSelected);
        var bytes = LargeFiles.Where(x => x.IsSelected).Sum(x => x.Item.SizeBytes);
        LargeSelectedText = $"已选 {count} 项 · {FormatHelper.Bytes(bytes)}";
    }

    // ================= 工具 =================

    private static int ParseDays(string s) => Math.Clamp(ParseInt(s, 180), 1, 3650);
    private static int ParseInt(string s, int fallback)
        => int.TryParse(s?.Trim(), out var v) && v > 0 ? v : fallback;

    /// <summary>检测微信 / QQ 是否正在运行（新版微信进程名为 Weixin，新版 QQ 为 QQNT）。</summary>
    private static bool AnyImRunning()
    {
        try
        {
            foreach (var name in new[] { "WeChat", "Weixin", "QQ", "QQNT" })
                if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) return true;
        }
        catch { }
        return false;
    }

    [RelayCommand]
    private void RefreshSelection()
    {
        UpdateRegularSelected();
        UpdateImSelected();
        UpdateLargeSelected();
    }
}
