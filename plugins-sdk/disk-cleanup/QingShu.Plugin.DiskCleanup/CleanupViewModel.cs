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
using Microsoft.UI.Xaml;

namespace QingShu.Plugin.DiskCleanup;

// ================= 常规清理目标行 =================
// Visibility 态属性（AdminVisState 等）：运行时 XamlReader 模板走经典 Binding，
// bool→Visibility 无隐式转换（x:Bind 的隐式转换是编译期生成器行为），直接暴露
// Microsoft.UI.Xaml.Visibility 类型属性（跨 ALC 类型同一）供模板绑定，免转换器。
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
    public Visibility AdminVisState => Def.NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;

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
    public Visibility HeaderVisState => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisState => IsHeader ? Visibility.Collapsed : Visibility.Visible;
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
    /// <summary>按扩展名归类的人类可读类型名（模板徽章展示，帮助快速判断可删性）。</summary>
    public string KindText => KindOf(Item.Ext);
    public string SizeText => FormatHelper.Bytes(Item.SizeBytes);
    public string TimeText => Item.LastWrite.ToString("yyyy-MM-dd HH:mm");

    /// <summary>扩展名 → 类型徽章文案（覆盖机房常见大文件；未知扩展名回退原始后缀或「其他」）。</summary>
    public static string KindOf(string ext) => ext switch
    {
        ".MP4" or ".AVI" or ".MKV" or ".MOV" or ".WMV" or ".FLV" or ".M4V" or ".TS" or ".WEBM" => "视频",
        ".MP3" or ".WAV" or ".FLAC" or ".M4A" or ".APE" or ".OGG" => "音频",
        ".ZIP" or ".RAR" or ".7Z" or ".TAR" or ".GZ" or ".BZ2" or ".XZ" => "压缩包",
        ".ISO" or ".IMG" or ".VHD" or ".VHDX" or ".GHO" or ".WIM" => "镜像",
        ".VMDK" or ".VDI" or ".QCOW2" => "虚拟机磁盘",
        ".EXE" or ".MSI" or ".MSIX" => "安装包",
        ".DOC" or ".DOCX" or ".PDF" or ".PPT" or ".PPTX" or ".XLS" or ".XLSX" => "文档",
        ".BAK" or ".DMP" or ".EML" => "备份/转储",
        ".PST" or ".OST" => "邮件存档",
        ".DB" or ".MDF" or ".NDF" or ".LDF" or ".ACCDB" => "数据库",
        "" => "无后缀",
        _ => ext.TrimStart('.').ToLowerInvariant() + " 文件"
    };

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 磁盘清理页 VM（宿主 ViewModels/CleanupViewModel 同源移植）：常规清理 / 微信 / QQ / 大文件 四个标签页。
/// 设置适配：宿主设置服务 → 插件私有 CleanupSettingsStore（首次启动从宿主 config.json 迁移）。
/// 日志适配：宿主日志服务 → Action&lt;string,string&gt;（level, message）。
/// 生命周期：每个导航新建 VM；视图 Unloaded 时 Dispose 取消全部在途扫描/删除（transient 语义同内置页）。
/// </summary>
public partial class CleanupViewModel : ObservableObject
{
    private readonly ICleanupService _cleanup;
    private readonly IImScanService _im;
    private readonly ILargeFileScanService _large;
    private readonly CleanupSettingsStore _settings;
    private readonly Action<string, string> _log; // (level, message) → context.Log

    // 四路扫描各自独立的取消源（扫描服务内部循环均检查 ct.IsCancellationRequested）
    private CancellationTokenSource? _ctsRegular;
    private CancellationTokenSource? _ctsWeChat;
    private CancellationTokenSource? _ctsQq;
    private CancellationTokenSource? _ctsLarge;
    // 删除链路取消源：三个删除服务均为逐文件检查 ct（break 返回部分结果），此前 VM 传 None 接不上
    private CancellationTokenSource? _ctsImDelete;
    private CancellationTokenSource? _ctsLargeDelete;

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
    /// <summary>清理阶段确定性进度（目标级 0-100；扫描阶段为 0，进度条切不定态）。</summary>
    [ObservableProperty] private double _regularCleanProgress;
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
    /// <summary>微信/QQ 删除确定性进度（0-100）。</summary>
    [ObservableProperty] private double _imDeleteProgress;

    // ---- 大文件 ----
    [ObservableProperty] private bool _isLargeBusy;
    [ObservableProperty] private string _largeStatus = "";
    [ObservableProperty] private string _largeMinMbText;
    [ObservableProperty] private int _driveIndex;
    [ObservableProperty] private string _largeSelectedText = "";
    [ObservableProperty] private bool _hasDrives;
    /// <summary>大文件删除确定性进度（0-100）。</summary>
    [ObservableProperty] private double _largeDeleteProgress;

    // ---- 通用 ----
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _deleteToRecycleBin;

    public CleanupViewModel(ICleanupService cleanup, IImScanService im, ILargeFileScanService large,
        CleanupSettingsStore settings, Action<string, string> log)
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
        _imOldDaysText = settings.ImOldDays.ToString();
        _largeMinMbText = settings.LargeFileMinMb.ToString();
        DeleteToRecycleBin = settings.DeleteToRecycleBin;
        _ = LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        try
        {
            var drives = await _large.GetFixedDrivesAsync().ConfigureAwait(true);
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
            HasDrives = Drives.Count > 0;
            DriveIndex = 0;
            if (!HasDrives) LargeStatus = "未发现可用分区，大文件扫描不可用。";
        }
        catch (Exception ex)
        {
            // 此前静默吞掉：枚举失败时 HasDrives 恒 false，大文件页无盘可选且无任何提示
            LargeStatus = $"磁盘分区枚举失败：{ex.Message}";
            _log("error", $"固定分区枚举异常：{ex}");
        }
    }

    partial void OnDeleteToRecycleBinChanged(bool value)
    {
        _settings.UpdateDeleteToRecycleBin(value);
    }

    // 历史 bug：清理页的阈值改动从未落盘，与设置页的同名设置各存各的（用户感知“两个设置”互不同步）。
    // 现与插件私有设置存储共用同一份：仅在输入合法且与现值不同时写入，避免半输入态污染设置。
    partial void OnImOldDaysTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), out var v) && v > 0)
        {
            var clamped = Math.Clamp(v, 1, 3650);
            if (_settings.ImOldDays != clamped)
            {
                _settings.UpdateImOldDays(clamped);
            }
        }
    }

    partial void OnLargeMinMbTextChanged(string value)
    {
        if (int.TryParse(value?.Trim(), out var v) && v > 0)
        {
            var clamped = Math.Clamp(v, 1, 1024 * 1024);
            if (_settings.LargeFileMinMb != clamped)
            {
                _settings.UpdateLargeFileMinMb(clamped);
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
        _ctsRegular?.Dispose();
        _ctsRegular = new CancellationTokenSource();
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
            await _cleanup.ScanAsync(targets, progress, _ctsRegular.Token).ConfigureAwait(true);
            UpdateRegularSelected();
            RegularStatus = "扫描完成，请确认要清理的项目后点击“开始清理”。";
        }
        catch (OperationCanceledException) { RegularStatus = "扫描已取消。"; }
        catch (Exception ex) { RegularStatus = $"扫描失败：{ex.Message}"; }
        finally { IsRegularBusy = false; }
    }

    /// <summary>取消常规清理扫描（此前 _cts 与取消捕获齐备但没有触发入口，大目录扫描只能干等）。</summary>
    [RelayCommand]
    private void CancelRegularScan()
    {
        if (IsRegularBusy) _ctsRegular?.Cancel(); // 扫描与清理共用：清理阶段点击同样生效（CleanAsync 在目标间响应）
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
        _ctsRegular?.Dispose();
        _ctsRegular = new CancellationTokenSource(); // 清理与扫描共用取消源：进入前重建，防上轮取消状态残留
        try
        {
            // 必须携带扫描结果：回收站目标依赖 SizeBytes 判断空/非空，传零值会被误判“已是空的”直接跳过（历史 bug）
            var scan = selected.Select(vm => vm.LastScan ?? new CleanScanTarget(vm.Def, 0, 0)).ToList();
            var log = new Progress<string>(line => RegularResult = line);
            // 清理阶段确定性进度：目标级推进 + 当前目标名进状态行（扫描/清理共用取消源）
            RegularCleanProgress = 0;
            var progress = new Progress<CleanProgress>(p =>
            {
                RegularCleanProgress = p.Total <= 1 ? 0 : 100.0 * p.Done / p.Total;
                RegularStatus = p.Done < p.Total ? $"正在清理 {p.Done + 1}/{p.Total}：{p.Current}…" : "清理收尾…";
            });
            var report = await _cleanup.CleanAsync(scan, DeleteToRecycleBin, log, progress, _ctsRegular.Token).ConfigureAwait(true);
            completion = $"清理完成：{report.ToSummary()}" +
                         (DeleteToRecycleBin && report.FreedBytes > 0 ? "（已移入回收站，清空回收站后才真正释放磁盘空间）" : "");
            RegularStatus = completion;
            LastResult = $"{DateTime.Now:HH:mm} 清理释放 {FormatHelper.Bytes(report.FreedBytes)}";
            succeeded = true;
        }
        catch (OperationCanceledException)
        {
            // CleanAsync 在目标间响应取消（ThrowIfCancellationRequested）：已完成目标保留生效，
            // 不自动重扫（列表停留扫描时快照），引导用户手动重扫确认
            RegularStatus = "清理已取消（已执行目标保留生效，可重新扫描确认实际状态）。";
        }
        catch (Exception ex)
        {
            RegularStatus = $"清理失败：{ex.Message}";
        }
        finally { IsRegularBusy = false; RegularCleanProgress = 0; }
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
        _ctsWeChat?.Dispose();
        _ctsWeChat = new CancellationTokenSource();
        try
        {
            // 扫描实时进度：Status 行滚动刷新计数，不再“正在扫描…干等”
            var scanProgress = new Progress<ScanProgress>(p =>
                WeChatStatus = p.Found > 0
                    ? $"正在扫描微信目录…已发现 {p.Found} 个久远文件 · 累计 {FormatHelper.Bytes(p.Bytes)}"
                    : "正在扫描微信目录…");
            var files = await _im.ScanWeChatAsync(ParseDays(_imOldDaysText), scanProgress, _ctsWeChat.Token).ConfigureAwait(true);
            // 扫描服务取消时是 break 返回部分结果而非抛 OCE：这里如实报取消，避免误标“扫描完成”
            if (_ctsWeChat.IsCancellationRequested) { WeChatStatus = "扫描已取消（以下为中止前已找到的部分结果）。"; return; }
            WeChatFiles = new ObservableCollection<ImFileVm>(BuildGroupedRows(files, "微信", OnImRowSelectionChanged));
            UpdateImSelected();
            WeChatStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(_imOldDaysText)} 天的微信文件（也可能修改过存储位置，可用大文件扫描兜底）。"
                : $"发现 {files.Count} 个久远文件（{files.Select(f => f.Account).Distinct().Count()} 个账号）· 共 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}。已按账号分组、默认全选，请逐项确认后删除。";
        }
        catch (OperationCanceledException) { WeChatStatus = "扫描已取消。"; }
        catch (Exception ex) { WeChatStatus = $"扫描失败：{ex.Message}"; }
        finally { IsWeChatBusy = false; }
    }

    [RelayCommand]
    private void CancelWeChatScan()
    {
        _ctsWeChat?.Cancel();
    }

    [RelayCommand]
    private async Task ScanQqAsync()
    {
        if (IsQqBusy) return;
        IsQqBusy = true;
        QqFiles.Clear();
        QqStatus = "正在扫描 QQ 目录…";
        _ctsQq?.Dispose();
        _ctsQq = new CancellationTokenSource();
        try
        {
            QqStatus = "正在扫描 QQ 目录…";
            var scanProgress = new Progress<ScanProgress>(p =>
                QqStatus = p.Found > 0
                    ? $"正在扫描 QQ 目录…已发现 {p.Found} 个久远文件 · 累计 {FormatHelper.Bytes(p.Bytes)}"
                    : "正在扫描 QQ 目录…");
            var files = await _im.ScanQQAsync(ParseDays(_imOldDaysText), scanProgress, _ctsQq.Token).ConfigureAwait(true);
            if (_ctsQq.IsCancellationRequested) { QqStatus = "扫描已取消（以下为中止前已找到的部分结果）。"; return; }
            QqFiles = new ObservableCollection<ImFileVm>(BuildGroupedRows(files, "QQ", OnImRowSelectionChanged));
            UpdateImSelected();
            QqStatus = files.Count == 0
                ? $"未发现超过 {ParseDays(_imOldDaysText)} 天的 QQ 文件。"
                : $"发现 {files.Count} 个久远文件（{files.Select(f => f.Account).Distinct().Count()} 个账号）· 共 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}。已按账号分组、默认全选，请逐项确认后删除。";
        }
        catch (OperationCanceledException) { QqStatus = "扫描已取消。"; }
        catch (Exception ex) { QqStatus = $"扫描失败：{ex.Message}"; }
        finally { IsQqBusy = false; }
    }

    [RelayCommand]
    private void CancelQqScan()
    {
        _ctsQq?.Cancel();
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

    [ObservableProperty] private bool _imDeleteBusy;

    /// <summary>微信/QQ 删除执行中的取消按钮可见性。</summary>
    public bool ImDeleteCancelVis => _imDeleteBusy;

    partial void OnImDeleteBusyChanged(bool value) => OnPropertyChanged(nameof(ImDeleteCancelVis));

    [RelayCommand]
    private void CancelImDelete()
    {
        if (_imDeleteBusy) _ctsImDelete?.Cancel();
    }

    [RelayCommand]
    private async Task DeleteImAsync()
    {
        if (_imDeleteBusy) return;
        var paths = WeChatFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)
            .Concat(QqFiles.Where(x => x.IsSelected && !x.IsHeader).Select(x => x.Item.Path)).ToList();
        if (paths.Count == 0) return;
        _imDeleteBusy = true;
        ImDeleteProgress = 0;
        try
        {
            _ctsImDelete?.Dispose();
            _ctsImDelete = new CancellationTokenSource(); // 每次执行重建，防上轮取消状态残留
            // 删除确定性进度：逐文件推进 + 当前文件名实时显示
            var deleteProgress = new Progress<DeleteProgress>(p =>
            {
                ImDeleteProgress = p.Total <= 1 ? 0 : 100.0 * p.Done / p.Total;
                WeChatStatus = $"正在删除 {p.Done + 1}/{p.Total}：{p.Current}（已处理 {FormatHelper.Bytes(p.Bytes)}）";
            });
            var (ok, failed, freed) = await _im.DeleteAsync(paths, DeleteToRecycleBin, deleteProgress, _ctsImDelete.Token).ConfigureAwait(true);
            bool canceled = _ctsImDelete.IsCancellationRequested; // 服务逐文件 break 不抛 OCE，需显式判定
            var mode = DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）";
            var status = canceled
                ? $"删除已取消（中止前已处理：成功 {ok}，失败/跳过 {failed}，{FormatHelper.Bytes(freed)}{mode}）"
                : $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{mode}";
            if (!canceled)
            {
                if (failed > 0 && ok == 0)
                    status += "。本次没有任何文件被删除（全部被占用或已不存在）";
                else if (failed > 0 && AnyImRunning())
                    status += "；微信/QQ 正在运行，失败项多为其正在使用的文件，完全退出后重试可继续清理";
            }
            QqStatus = "";
            // 先重扫刷新列表（内部会写扫描状态），再回写删除结果，避免“删除完成/已取消”提示被覆盖
            await ScanWeChatAsync().ConfigureAwait(true);
            _ = ScanQqAsync();
            WeChatStatus = status;
        }
        catch (Exception ex)
        {
            WeChatStatus = $"删除失败：{ex.Message}";
        }
        finally { _imDeleteBusy = false; ImDeleteProgress = 0; }
    }

    // ================= 大文件 =================

    [RelayCommand]
    private async Task ScanLargeAsync()
    {
        if (IsLargeBusy || !HasDrives) return;
        IsLargeBusy = true;
        LargeFiles.Clear();
        LargeStatus = "正在扫描（大分区可能需要 1-2 分钟）…";
        _ctsLarge?.Dispose();
        _ctsLarge = new CancellationTokenSource();
        try
        {
            var drive = Drives.ElementAtOrDefault(DriveIndex);
            if (drive == default) return;
            // 扫描实时进度：已扫目录数/已发现/累计体积滚动刷新，不再静默干等
            var scanProgress = new Progress<ScanProgress>(p =>
                LargeStatus = $"正在扫描 {drive}: …已遍历 {p.Dirs} 个目录" +
                              (p.Found > 0 ? $" · 发现 {p.Found} 个大文件 · 累计 {FormatHelper.Bytes(p.Bytes)}" : ""));
            var files = await _large.ScanAsync(drive, ParseInt(_largeMinMbText, 100), scanProgress, _ctsLarge.Token).ConfigureAwait(true);
            if (_ctsLarge.IsCancellationRequested) { LargeStatus = "扫描已取消（以下为中止前已找到的部分结果）。"; return; }
            LargeFiles = new ObservableCollection<LargeFileVm>(files.Take(200).Select(f => new LargeFileVm(f)));
            UpdateLargeSelected();
            LargeStatus = $"发现 {files.Count} 个 ≥{ParseInt(_largeMinMbText, 100)}MB 的文件 · 合计 {FormatHelper.Bytes(files.Sum(f => f.SizeBytes))}，展示前 {LargeFiles.Count} 个最大的。请勾选确认后删除。";
        }
        catch (OperationCanceledException) { LargeStatus = "扫描已取消。"; }
        catch (Exception ex) { LargeStatus = $"扫描失败：{ex.Message}"; }
        finally { IsLargeBusy = false; }
    }

    [RelayCommand]
    private void CancelLargeScan()
    {
        if (IsLargeBusy) _ctsLarge?.Cancel();
    }

    // busy 可见性（取消按钮仅在扫描中显示）
    public bool WeChatBusyVis => IsWeChatBusy;
    public bool QqBusyVis => IsQqBusy;
    public bool LargeBusyVis => IsLargeBusy;

    // 删除确定性进度条可见性（仅删除执行中且有进度时显示，避免空条占位）
    public bool ImDeleteProgressVis => _imDeleteBusy && ImDeleteProgress > 0;
    public bool LargeDeleteProgressVis => _largeDeleteBusy && LargeDeleteProgress > 0;
    /// <summary>常规清理确定性进度条可见性（清理阶段有目标推进时显示；扫描阶段走不定态条）。</summary>
    public bool RegularCleanBarVis => IsRegularBusy && RegularCleanProgress > 0;

    partial void OnIsWeChatBusyChanged(bool value) => OnPropertyChanged(nameof(WeChatBusyVis));
    partial void OnIsQqBusyChanged(bool value) => OnPropertyChanged(nameof(QqBusyVis));
    partial void OnIsLargeBusyChanged(bool value) => OnPropertyChanged(nameof(LargeBusyVis));
    partial void OnImDeleteProgressChanged(double value) => OnPropertyChanged(nameof(ImDeleteProgressVis));
    partial void OnLargeDeleteProgressChanged(double value) => OnPropertyChanged(nameof(LargeDeleteProgressVis));
    partial void OnRegularCleanProgressChanged(double value) => OnPropertyChanged(nameof(RegularCleanBarVis));

    [RelayCommand]
    private void OpenContainingFolder(object? param)
    {
        if (param is string path && File.Exists(path))
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
    }

    [ObservableProperty] private bool _largeDeleteBusy;

    /// <summary>大文件删除执行中的取消按钮可见性。</summary>
    public bool LargeDeleteCancelVis => _largeDeleteBusy;

    partial void OnLargeDeleteBusyChanged(bool value) => OnPropertyChanged(nameof(LargeDeleteCancelVis));

    [RelayCommand]
    private void CancelLargeDelete()
    {
        if (_largeDeleteBusy) _ctsLargeDelete?.Cancel();
    }

    [RelayCommand]
    private async Task DeleteLargeAsync()
    {
        if (_largeDeleteBusy) return;
        var paths = LargeFiles.Where(x => x.IsSelected).Select(x => x.Item.Path).ToList();
        if (paths.Count == 0) return;
        _largeDeleteBusy = true;
        LargeDeleteProgress = 0;
        try
        {
            _ctsLargeDelete?.Dispose();
            _ctsLargeDelete = new CancellationTokenSource(); // 每次执行重建，防上轮取消状态残留
            // 删除确定性进度：逐文件推进 + 当前文件名/已处理字节实时显示
            var deleteProgress = new Progress<DeleteProgress>(p =>
            {
                LargeDeleteProgress = p.Total <= 1 ? 0 : 100.0 * p.Done / p.Total;
                LargeStatus = $"正在删除 {p.Done + 1}/{p.Total}：{p.Current}（已处理 {FormatHelper.Bytes(p.Bytes)}）";
            });
            var (ok, failed, freed) = await _large.DeleteAsync(paths, DeleteToRecycleBin, deleteProgress, _ctsLargeDelete.Token).ConfigureAwait(true);
            bool canceled = _ctsLargeDelete.IsCancellationRequested; // 服务逐文件 break 不抛 OCE，需显式判定
            var tail = canceled ? "）" : "";
            LargeStatus = (canceled
                ? $"删除已取消（中止前已处理：成功 {ok}，失败/跳过 {failed}，{FormatHelper.Bytes(freed)}{(DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）")}"
                : $"删除完成：成功 {ok}，失败/跳过 {failed}，处理 {FormatHelper.Bytes(freed)}{(DeleteToRecycleBin ? "（已移入回收站）" : "（已永久删除）")}")
                          + tail
                          + (!canceled && failed > 0 && ok == 0 ? "。本次没有任何文件被删除（文件被占用或已不存在）" : "");
            _ = ScanLargeAsync();
        }
        catch (Exception ex)
        {
            LargeStatus = $"删除失败：{ex.Message}";
        }
        finally { _largeDeleteBusy = false; LargeDeleteProgress = 0; }
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
                if (Process.GetProcessesByName(name).Length > 0) return true;
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

    /// <summary>视图 Unloaded 收尾：取消全部在途扫描/删除（transient 语义，切页后不再写盘/删除）。</summary>
    public void Dispose()
    {
        try { if (IsRegularBusy) _ctsRegular?.Cancel(); } catch { }
        try { if (IsWeChatBusy) _ctsWeChat?.Cancel(); } catch { }
        try { if (IsQqBusy) _ctsQq?.Cancel(); } catch { }
        try { if (IsLargeBusy) _ctsLarge?.Cancel(); } catch { }
        try { if (_imDeleteBusy) _ctsImDelete?.Cancel(); } catch { }
        try { if (_largeDeleteBusy) _ctsLargeDelete?.Cancel(); } catch { }
    }
}
