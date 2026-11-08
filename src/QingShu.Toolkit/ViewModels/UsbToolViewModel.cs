using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Services.Logging;
using QingShu.Toolkit.Services.Usb;

namespace QingShu.Toolkit.ViewModels;

/// <summary>
/// U盘工具 VM：设备探查 → 硬件级健康检测 / 扩容盘检测 → 查杀与免疫 → 修复。
/// 安全铁律：只对 USBSTOR 设备操作；系统卷绝不参与破坏性动作；破坏性操作由页面二次确认后调用。
/// </summary>
public partial class UsbToolViewModel : ObservableObject
{
    private readonly IUsbProbeService _probe;
    private readonly IUsbHealthService _health;
    private readonly IUsbScanService _scan;
    private readonly IUsbRepairService _repair;
    private readonly ILogService _log;
    private CancellationTokenSource? _cts;

    public ObservableCollection<UsbDriveRow> Devices { get; } = new();
    public ObservableCollection<UsbCheckRow> CheckResults { get; } = new();
    public ObservableCollection<UsbThreatRow> Threats { get; } = new();
    public ObservableCollection<UsbFixAction> FixActions { get; } = new();

    [ObservableProperty] private UsbDriveRow? _selectedDevice;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _hint = "插入U盘后点「刷新设备」。本工具只对 USB 设备生效，绝不触碰系统盘；破坏性操作均有二次确认。";
    [ObservableProperty] private string _threatSummary = "";
    [ObservableProperty] private bool _hasDevice;
    [ObservableProperty] private bool _isEmptyResult;

    public bool CanOperate => !IsBusy && SelectedDevice != null;
    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(IsNotBusy));
    }
    partial void OnSelectedDeviceChanged(UsbDriveRow? value)
    {
        OnPropertyChanged(nameof(CanOperate));
        _ = BuildFixPlanAsync();
    }

    public UsbToolViewModel(IUsbProbeService probe, IUsbHealthService health, IUsbScanService scan,
        IUsbRepairService repair, ILogService log)
    {
        _probe = probe;
        _health = health;
        _scan = scan;
        _repair = repair;
        _log = log;
    }

    // ================= 设备 =================

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshDevicesCoreAsync().ConfigureAwait(true); }
        finally { IsBusy = false; }
    }

    /// <summary>枚举核心（不碰 IsBusy 守卫）：修复/重建完成后在忙碌态内也要能调用，
    /// 此前直接调 RefreshDevicesAsync 会被自身 IsBusy 守卫空跑，「执行后自动刷新」是死代码。</summary>
    private async Task RefreshDevicesCoreAsync()
    {
        try
        {
            ProgressText = "正在枚举 USB 存储设备…";
            var drives = await _probe.ListDrivesAsync().ConfigureAwait(true);
            var keep = SelectedDevice?.Drive.DiskIndex;
            Devices.Clear();
            foreach (var d in drives) Devices.Add(new UsbDriveRow(d));
            SelectedDevice = keep is int k ? Devices.FirstOrDefault(x => x.Drive.DiskIndex == k) ?? Devices.FirstOrDefault() : Devices.FirstOrDefault();
            HasDevice = drives.Count > 0;
            IsEmptyResult = drives.Count == 0;
            ProgressText = drives.Count == 0 ? "未发现 USB 存储设备（请插入U盘后点刷新）。" : $"发现 {drives.Count} 个 USB 存储设备。";
            _log.App(LogLevel.Info, "usb", $"USB 设备枚举完成：{drives.Count} 个",
                drives.Select(d => new { d.DiskIndex, d.Model, letters = d.LettersText }).ToArray());
        }
        catch (Exception ex)
        {
            ProgressText = $"设备枚举失败：{ex.Message}";
            _log.App(LogLevel.Error, "usb", "USB 设备枚举异常", new { ex = ex.ToString() });
        }
    }

    private async Task BuildFixPlanAsync()
    {
        FixActions.Clear();
        var d = SelectedDevice?.Drive;
        if (d == null) return;
        try
        {
            foreach (var a in await _repair.BuildPlanAsync(d).ConfigureAwait(true)) FixActions.Add(a);
        }
        catch (Exception ex) { Hint = $"生成修复清单失败：{ex.Message}"; }
    }

    // ================= 硬件检测 =================

    [RelayCommand]
    private async Task RunHealthAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        await RunGuardedAsync("健康检测中…", async ct =>
        {
            CheckResults.Clear();
            var outcomes = await _health.RunHealthCheckAsync(SelectedDevice.Drive, new Progress<string>(p => ProgressText = p), ct)
                .ConfigureAwait(true);
            foreach (var o in outcomes) CheckResults.Add(new UsbCheckRow(o));
            ProgressText = $"健康检测完成：异常 {outcomes.Count(o => o.Status == "Fail")} · 注意 {outcomes.Count(o => o.Status == "Warning")}";
            _log.App(LogLevel.Info, "usb", "健康检测完成",
                outcomes.Select(o => $"[{o.Status}] {o.Title}: {o.Summary}").ToArray());
        });
    }

    /// <summary>扩容盘写校验（破坏性）。仅供页面在完成二次确认后调用。</summary>
    public async Task RunCapacityVerifiedAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        await RunGuardedAsync("扩容盘检测中…", async ct =>
        {
            CheckResults.Clear();
            Hint = "扩容检测会覆盖探针区域数据（约 4 处 × 4 MB），且检测期间U盘不可用。";
            var outcomes = await _health.RunCapacityVerifyAsync(SelectedDevice.Drive, new Progress<string>(p => ProgressText = p), ct)
                .ConfigureAwait(true);
            foreach (var o in outcomes) CheckResults.Add(new UsbCheckRow(o));
            ProgressText = "扩容检测完成。若提示容量真实，请重新插拔U盘恢复访问。";
            _log.App(LogLevel.Info, "usb", "扩容盘检测完成",
                outcomes.Select(o => $"[{o.Status}] {o.Title}: {o.Summary}").ToArray());
        });
    }

    // ================= 查杀 =================

    [RelayCommand]
    private async Task RunScanAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        await RunGuardedAsync("查杀扫描中…", async ct =>
        {
            Threats.Clear();
            int scanned = 0;
            var allNotes = new System.Collections.Generic.List<string>();
            foreach (var vol in SelectedDevice.Drive.Volumes)
            {
                var (threats, n, notes) = await _scan.ScanVolumeAsync(vol, new Progress<string>(p => ProgressText = p), ct)
                    .ConfigureAwait(true);
                foreach (var t in threats) Threats.Add(new UsbThreatRow(t));
                scanned += n;
                allNotes.AddRange(notes);
            }
            ThreatSummary = Threats.Count == 0
                ? $"扫描完成（{scanned} 个文件）：未发现威胁。"
                : $"发现 {Threats.Count} 项威胁（恶意 {Threats.Count(t => t.Level == "Malicious")} / 可疑 {Threats.Count(t => t.Level != "Malicious")}），已自动勾选恶意项。";
            if (allNotes.Count > 0) ThreatSummary += $"（{string.Join("；", allNotes.Take(3))}）";
            ProgressText = "查杀扫描完成。";
            _log.App(LogLevel.Info, "usb", "查杀扫描完成", new { files = scanned, threats = Threats.Count });
        });
    }

    [RelayCommand]
    private async Task QuarantineSelectedAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        var selected = Threats.Where(t => t.IsSelected).Select(t => t.FilePath).ToList();
        if (selected.Count == 0) { Hint = "请先勾选要隔离的文件。"; return; }
        await RunGuardedAsync("隔离中…", async _ =>
        {
            var letter = selected[0].Length > 0 ? selected[0][0].ToString() : "";
            var (ok, fail, notes, quarantined) = await _scan.QuarantineAsync(selected, letter).ConfigureAwait(true);
            foreach (var n in notes.Take(3)) _log.App(ok > 0 ? LogLevel.Info : LogLevel.Error, "usb", "隔离结果", n);
            // 只移除真正隔离成功的行（此前按全局成功数删全部勾选行，失败文件被误标已清理）
            foreach (var path in quarantined)
            {
                var row = Threats.FirstOrDefault(t => t.FilePath == path);
                if (row != null) Threats.Remove(row);
            }
            ThreatSummary = $"隔离完成：成功 {ok} · 失败 {fail}。";
            Hint = $"隔离完成：成功 {ok} · 失败 {fail}。被隔离文件存放在本机隔离区（见日志），可人工恢复。";
            ProgressText = "隔离完成。建议点「解除隐藏」找回被蠕虫藏起来的真实文件。";
        });
    }

    [RelayCommand]
    private async Task UnhideAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        var letter = SelectedDevice.Drive.Volumes.FirstOrDefault()?.Letter;
        if (letter == null) { Hint = "该设备没有可用卷（无盘符），无法执行解除隐藏。"; return; }
        await RunGuardedAsync("解除隐藏中…", async _ =>
        {
            var ok = await _scan.UnhideFoldersAsync(letter).ConfigureAwait(true);
            Hint = ok ? $"已对 {letter}: 全盘解除隐藏/系统属性：被蠕虫藏起的真实文件夹应已重新可见。"
                      : "解除隐藏失败（attrib 执行异常），请检查U盘是否可写。";
            ProgressText = ok ? "解除隐藏完成。" : "解除隐藏失败。";
        });
    }

    [RelayCommand]
    private async Task ImmuneAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        var letter = SelectedDevice.Drive.Volumes.FirstOrDefault()?.Letter;
        if (letter == null) { Hint = "该设备没有可用卷（无盘符），无法创建免疫目录。"; return; }
        await RunGuardedAsync("创建免疫中…", async _ =>
        {
            var (ok, msg) = await _scan.CreateImmunityAsync(letter).ConfigureAwait(true);
            Hint = msg;
            ProgressText = ok ? "免疫目录已创建。" : "免疫创建失败。";
        });
    }

    // ================= 修复 =================

    [RelayCommand]
    private async Task FixSelectedAsync()
    {
        if (IsBusy || SelectedDevice == null) return;
        var selected = FixActions.Where(a => a.IsSelected && a.Status is "Pending" or "Fail").ToList();
        if (selected.Count == 0) { Hint = "没有可执行的修复动作（危险项需先手动勾选）。"; return; }
        await RunGuardedAsync("修复执行中…", async ct =>
        {
            await _repair.RunAsync(SelectedDevice.Drive, selected, new Progress<UsbFixAction>(_ => { }), ct).ConfigureAwait(true);
            int ok = selected.Count(a => a.Status == "Ok");
            Hint = $"修复执行完毕（成功 {ok}/{selected.Count}）。建议「刷新设备」后重新健康检测复核；设备栈重置若生效会短暂掉线重连。";
            if (ok > 0) await RefreshDevicesCoreAsync().ConfigureAwait(true);
        });
    }

    /// <summary>重建分区表（破坏性）。仅供页面在完成二次确认后调用。</summary>
    public async Task RebuildPartitionVerifiedAsync(string fileSystem)
    {
        if (IsBusy || SelectedDevice == null) return;
        await RunGuardedAsync("重建分区中…", async ct =>
        {
            CheckResults.Clear();
            var a = await _repair.RebuildPartitionAsync(SelectedDevice.Drive, fileSystem, new Progress<UsbFixAction>(_ => { }), ct)
                .ConfigureAwait(true);
            CheckResults.Add(new UsbCheckRow(new UsbCheckOutcome(
                "重建分区表", a.Status == "Ok" ? "Pass" : "Fail", a.Output,
                a.Status == "Ok" ? "" : "可尝试：重新插拔 → 重置设备栈 → 再重建；diskpart 失败多为设备离线或写保护。",
                Array.Empty<string>())));
            Hint = a.Status == "Ok" ? "分区已重建并格式化，请「刷新设备」识别新卷。" : "重建失败，详见检查结果与日志。";
            if (a.Status == "Ok") await RefreshDevicesCoreAsync().ConfigureAwait(true);
        });
    }

    // ================= 公共 =================

    /// <summary>取消当前长操作（健康检测/表面扫描/查杀/修复在步骤边界生效）。
    /// 此前 _cts 与取消捕获齐备却没有触发入口，用户面对 150s 表面扫描只能干等。</summary>
    [RelayCommand]
    private void CancelOperation()
    {
        if (IsBusy) _cts?.Cancel();
    }

    // ================= 生命周期收尾 =================
    // 页面 Unloaded 时取消在途长操作（取消在步骤边界生效，不会打断已开始的写入块）。
    // Task 52 实锤：VM 为 transient 且 USB 服务层无重入护栏——不收尾则旧破坏性操作
    // （扩容检测/表面扫描/重建分区）在切页后继续写盘，复入页的新 VM IsBusy=false，
    // 可对同一U盘并发发起第二次破坏性操作。

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { } // 与 RunGuardedAsync finally 的释放窗口兜底（同为 UI 线程，实际不会撞）
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunGuardedAsync(string busyText, Func<CancellationToken, Task> body)
    {
        IsBusy = true;
        ProgressText = busyText;
        _cts = new CancellationTokenSource();
        try { await body(_cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { ProgressText = "操作已取消。"; }
        catch (Exception ex)
        {
            ProgressText = $"操作失败：{ex.Message}";
            _log.App(LogLevel.Error, "usb", "USB 工具操作异常", new { ex = ex.ToString() });
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}

// ================= XAML 绑定行模型 =================

/// <summary>设备下拉行。</summary>
public sealed class UsbDriveRow
{
    public UsbDriveRow(UsbDrive drive) { Drive = drive; }
    public UsbDrive Drive { get; }
    public string Summary => $"{Drive.Model}（{Drive.SizeText}）· {Drive.LettersText}";
    public string ModelText => Drive.Model;
    public string SizeText => Drive.SizeText;
    public string BusText => $"{Drive.BusTypeText}{(Drive.BusRemovable ? " · 可移动介质" : "")}";
    public string SerialText => Drive.Serial.Length > 0 ? Drive.Serial : "（设备未上报）";
    public string PnpText => Drive.PnpId;
    public string VolumesText => Drive.Volumes.Count == 0
        ? "无卷（可能未分区或分区表损坏）"
        : string.Join("\n", Drive.Volumes.Select(v => $"{v.Letter}: [{v.FileSystem}] {v.Label} · {v.SizeText}"));
    public override string ToString() => Summary;
}

/// <summary>健康检测结论行。</summary>
public sealed class UsbCheckRow
{
    public UsbCheckRow(UsbCheckOutcome o)
    {
        Title = o.Title; Status = o.Status; Summary = o.Summary; Suggestion = o.Suggestion;
        Details = string.Join("\n", o.Details);
    }
    public string Title { get; }
    public string Status { get; }
    public string Summary { get; }
    public string Suggestion { get; }
    public string Details { get; }
    public string StatusText => Status switch { "Pass" => "正常", "Warning" => "注意", "Fail" => "异常", _ => "跳过" };
    public bool PassVis => Status == "Pass";
    public bool WarnVis => Status == "Warning";
    public bool FailVis => Status == "Fail";
    public bool SkipVis => Status == "Skipped";
    public bool SuggestionVis => !string.IsNullOrWhiteSpace(Suggestion);
    public bool DetailsVis => Details.Length > 0;
}

/// <summary>查杀威胁行。</summary>
public sealed partial class UsbThreatRow : ObservableObject
{
    public UsbThreatRow(UsbThreat t)
    {
        FilePath = t.FilePath; Level = t.Level; Reason = t.Reason; Detail = t.Detail;
        _isSelected = t.Level == "Malicious";
    }
    public string FilePath { get; }
    public string Level { get; }
    public string Reason { get; }
    public string Detail { get; }
    public string FileName => Path.GetFileName(FilePath);
    public string DirText => Path.GetDirectoryName(FilePath) ?? "";
    public string LevelText => Level == "Malicious" ? "恶意" : "可疑";
    public bool MalVis => Level == "Malicious";
    public bool SuspVis => Level != "Malicious";
    [ObservableProperty] private bool _isSelected;
}
