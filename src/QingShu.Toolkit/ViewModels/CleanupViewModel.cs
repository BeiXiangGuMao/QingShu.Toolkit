using System;
using System.Collections.ObjectModel;
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
        IsSelected = def.DefaultSelected;
        SizeText = "待扫描"; FileText = ""; // partial 属性默认值移入构造器
    }

    public CleanTargetDef Def { get; }
    public string Name => Def.Name;
    public string Description => Def.Description;
    public string RiskText => CleanRiskText.Of(Def.Risk);
    public string AdminText => Def.NeedsAdmin ? "需管理员" : "";
    public bool AdminVis => Def.NeedsAdmin;

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsScanned { get; set; }
    [ObservableProperty] public partial string SizeText { get; set; }
    [ObservableProperty] public partial string FileText { get; set; }
    [ObservableProperty] public partial bool IsScanning { get; set; }
}

// ================= IM / 大文件行 =================
public partial class ImFileVm : ObservableObject
{
    public ImFileVm(ImFileItem item)
    {
        Item = item;
        IsSelected = true;
    }

    /// <summary>账号分组头行（不可勾选，仅展示该账号汇总）。</summary>
    public ImFileVm(string headerText)
    {
        Item = new ImFileItem();
        IsHeader = true;
        HeaderText = headerText;
        IsSelected = false;
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

    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public partial class LargeFileVm : ObservableObject
{
    public LargeFileVm(LargeFileInfo item)
    {
        Item = item;
        IsSelected = false;
    }

    public LargeFileInfo Item { get; }
    public string Name => Item.Name;
    public string PathText => Item.Dir;
    public string ExtText => Item.Ext;
    public string SizeText => FormatHelper.Bytes(Item.SizeBytes);
    public string TimeText => Item.LastWrite.ToString("yyyy-MM-dd HH:mm");

    [ObservableProperty] public partial bool IsSelected { get; set; }
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
    public ObservableCollection<ImFileVm> WeChatFiles { get; } = new();
    public ObservableCollection<ImFileVm> QqFiles { get; } = new();
    public ObservableCollection<LargeFileVm> LargeFiles { get; } = new();
    public ObservableCollection<char> Drives { get; } = new();

    // ---- 常规 ----
    [ObservableProperty] public partial bool IsRegularBusy { get; set; }
    [ObservableProperty] public partial double RegularProgress { get; set; }
    [ObservableProperty] public partial string RegularStatus { get; set; }
    [ObservableProperty] public partial string RegularSelectedText { get; set; }
    [ObservableProperty] public partial string RegularResult { get; set; }

    // ---- IM ----
    [ObservableProperty] public partial bool IsWeChatBusy { get; set; }
    [ObservableProperty] public partial bool IsQqBusy { get; set; }
    [ObservableProperty] public partial string WeChatStatus { get; set; }
    [ObservableProperty] public partial string QqStatus { get; set; }
    [ObservableProperty] public partial string ImOldDaysText { get; set; }
    [ObservableProperty] public partial string ImSelectedText { get; set; }

    // ---- 大文件 ----
    [ObservableProperty] public partial bool IsLargeBusy { get; set; }
    [ObservableProperty] public partial string LargeStatus { get; set; }
    [ObservableProperty] public partial string LargeMinMbText { get; set; }
    [ObservableProperty] public partial int DriveIndex { get; set; }
    [ObservableProperty] public partial string LargeSelectedText { get; set; }
    [ObservableProperty] public partial bool HasDrives { get; set; }

    // ---- 通用 ----
    [ObservableProperty] public partial string LastResult { get; set; }
    [ObservableProperty] public partial bool DeleteToRecycleBin { get; set; }

    public CleanupViewModel(ICleanupService cleanup, IImScanService im, ILargeFileScanService large,
        ISettingsService settings, ILogService log)
    {
        _cleanup = cleanup;
        _im = im;
        _large = large;
        _settings = settings;
        _log = log;
        // partial 属性无初始化器：原字段默认值移入构造器
        RegularStatus = "点击“开始扫描”统计可清理空间";
        RegularSelectedText = "已选 0 项";
        RegularResult = ""; WeChatStatus = ""; QqStatus = "";
        ImSelectedText = ""; LargeStatus = ""; LargeSelectedText = ""; LastResult = "";
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
        ImOldDaysText = settings.Current.ImOldDays.ToString();
        LargeMinMbText = settings.Current.LargeFileMinMb.ToString();
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
                    vm.IsScanned = true;
                    vm.SizeText = FormatHelper.Bytes(t.SizeBytes);
                    vm.FileText = $"{t.FileCount} 项";
                    RegularProgress++;
                }
            });
            RegularProgress = 0;
            await _cleanup.ScanAsync(targets, progress, _cts.Token).ConfigureAwait(true);
            UpdateRegularSelected();
            RegularStatus = $"扫描完成：共可清理 {FormatHelper.Bytes(RegularTargets.Sum(x => x.Def.IsRecycleBin ? ParseSize(x.SizeText) : 0))}（含回收站）";
            RegularStatus = "扫描完成，请确认要清理的项目后点击“开始清理”。";
        }
        catch (OperationCanceledException) { RegularStatus = "扫描已取消。"; }
        catch (Exception ex) { RegularStatus = $"扫描失败：{ex.Message}"; }
        finally { IsRegularBusy = false; }
    }

    private static long ParseSize(string _) => 0; // 汇总改由选中项实时计算展示

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
        try
        {
            var scan = selected.Select(vm => new CleanScanTarget(vm.Def, 0, 0)).ToList();
            var log = new Progress<string>(line => RegularResult = line);
            var report = await _cleanup.CleanAsync(scan, DeleteToRecycleBin, log, CancellationToken.None).ConfigureAwait(true);
            RegularStatus = $"清理完成：{report.ToSummary()}";
            LastResult = $"{DateTime.Now:HH:mm} 清理释放 {FormatHelper.Bytes(report.FreedBytes)}";
            await ScanRegularAsync().ConfigureAwait(true); // 重新扫描显示新状态
        }
        catch (Exception ex)
        {
            RegularStatus = $"清理失败：{ex.Message}";
        }
        finally { IsRegularBusy = false; }
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
            var files = await _im.ScanWeChatAsync(ParseDays(ImOldDaysText), CancellationToken.None).ConfigureAwait(true);
            FillGrouped(WeChatFiles, files, "微信");
            UpdateImSelected();
            WeChatStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(ImOldDaysText)} 天的微信文件（也可能修改过存储位置，可用大文件扫描兜底）。"
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
            var files = await _im.ScanQQAsync(ParseDays(ImOldDaysText), CancellationToken.None).ConfigureAwait(true);
            FillGrouped(QqFiles, files, "QQ");
            UpdateImSelected();
            QqStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(ImOldDaysText)} 天的 QQ 文件。"
                : $"发现 {files.Count} 个久远文件，共 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}。已默认全选，请逐项确认后删除。";
        }
        catch (Exception ex) { QqStatus = $"扫描失败：{ex.Message}"; }
        finally { IsQqBusy = false; }
    }

    [RelayCommand]
    private void SelectAllIm()
    {
        foreach (var f in WeChatFiles) if (!f.IsHeader) f.IsSelected = true;
        foreach (var f in QqFiles) if (!f.IsHeader) f.IsSelected = true;
        UpdateImSelected();
    }

    [RelayCommand]
    private void SelectNoneIm()
    {
        foreach (var f in WeChatFiles) if (!f.IsHeader) f.IsSelected = false;
        foreach (var f in QqFiles) if (!f.IsHeader) f.IsSelected = false;
        UpdateImSelected();
    }

    /// <summary>按账号文件夹分组展示：组头行（账号 · 文件数 · 总量）+ 组内按大小降序。</summary>
    private void FillGrouped(ObservableCollection<ImFileVm> target, List<ImFileItem> files, string label)
    {
        target.Clear();
        foreach (var g in files.GroupBy(f => f.Account).OrderByDescending(g => g.Sum(f => f.SizeBytes)))
        {
            target.Add(new ImFileVm($"{label}账号：{g.Key}  ·  {g.Count()} 个文件  ·  共 {FormatHelper.Bytes(g.Sum(f => f.SizeBytes))}"));
            foreach (var f in g.OrderByDescending(f => f.SizeBytes))
            {
                var row = new ImFileVm(f);
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ImFileVm.IsSelected)) UpdateImSelected();
                };
                target.Add(row);
            }
        }
    }

    private void UpdateImSelected()
    {
        var count = WeChatFiles.Count(x => x.IsSelected && !x.IsHeader) + QqFiles.Count(x => x.IsSelected && !x.IsHeader);
        var bytes = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes)
                  + QqFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes);
        ImSelectedText = $"已选 {count} 项 · {FormatHelper.Bytes(bytes)}";
    }

    [RelayCommand]
    private async Task DeleteImAsync()
    {
        var paths = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)
            .Concat(QqFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)).ToList();
        if (paths.Count == 0) return;
        var bytes = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes)
                  + QqFiles.Where(x => x.IsSelected && !x.IsHeader).Sum(x => x.Item.SizeBytes);
        var (ok, failed, freed) = await _im.DeleteAsync(paths, DeleteToRecycleBin, CancellationToken.None).ConfigureAwait(true);
        WeChatStatus = $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{(DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）")}";
        QqStatus = "";
        await ScanWeChatAsync().ConfigureAwait(true);
        _ = ScanQqAsync();
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
            var files = await _large.ScanAsync(drive, ParseInt(LargeMinMbText, 100), CancellationToken.None).ConfigureAwait(true);
            foreach (var f in files.Take(200)) LargeFiles.Add(new LargeFileVm(f));
            UpdateLargeSelected();
            LargeStatus = $"发现 {files.Count} 个 ≥{ParseInt(LargeMinMbText, 100)}MB 的文件，展示前 {LargeFiles.Count} 个。请勾选确认后删除。";
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

    [RelayCommand]
    private async Task DeleteLargeAsync()
    {
        var paths = LargeFiles.Where(x => x.IsSelected).Select(x => x.Item.Path).ToList();
        if (paths.Count == 0) return;
        var (ok, failed, freed) = await _large.DeleteAsync(paths, DeleteToRecycleBin, CancellationToken.None).ConfigureAwait(true);
        LargeStatus = $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{(DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）")}";
        _ = ScanLargeAsync();
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

    [RelayCommand]
    private void RefreshSelection()
    {
        UpdateRegularSelected();
        UpdateImSelected();
        UpdateLargeSelected();
    }
}
