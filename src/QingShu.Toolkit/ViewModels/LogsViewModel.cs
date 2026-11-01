using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.Services.Logging;

namespace QingShu.Toolkit.ViewModels;

public partial class LogDateVm
{
    public LogDateVm(string dirPath, string dateText, int files)
    {
        DirPath = dirPath; DateText = dateText; FileCount = files;
    }
    public string DirPath { get; }
    public string DateText { get; }
    public int FileCount { get; }
    public string Header => $"{DateText}（{FileCount} 个文件）";
}

public partial class LogFileVm
{
    public LogFileVm(string fullPath, string fileName, long size)
    {
        FullPath = fullPath; FileName = fileName; SizeBytes = size;
    }
    public string FullPath { get; }
    public string FileName { get; }
    public long SizeBytes { get; }
    public string SizeText => FormatHelper.Bytes(SizeBytes);
}

/// <summary>日志查看页 VM：按日期浏览 → 选择文件 → 尾部预览。</summary>
public partial class LogsViewModel : ObservableObject
{
    private readonly ILogService _log;

    public ObservableCollection<LogDateVm> Dates { get; } = new();
    public ObservableCollection<LogFileVm> Files { get; } = new();

    [ObservableProperty] private LogDateVm? _selectedDate;
    [ObservableProperty] private LogFileVm? _selectedFile;
    [ObservableProperty] private string _previewText = "选择左侧日期与日志文件进行预览。";
    [ObservableProperty] private string _retentionText = "";
    [ObservableProperty] private string _rootText;

    public LogsViewModel(ILogService log, ISettingsService settings)
    {
        _log = log;
        _rootText = log.Root;
        _retentionText = $"日志保留策略：最近 {settings.Current.LogRetentionDays} 天（可在设置中调整）";
        _ = RefreshDatesAsync();
    }

    [RelayCommand]
    private async Task RefreshDatesAsync()
    {
        Dates.Clear();
        foreach (var (dir, date, files) in _log.GetDateDirs()) Dates.Add(new LogDateVm(dir, date, files));
        await Task.CompletedTask;
        if (Dates.Count > 0 && SelectedDate == null) SelectedDate = Dates[0];
    }

    partial void OnSelectedDateChanged(LogDateVm? value)
    {
        Files.Clear();
        if (value == null) return;
        foreach (var (full, name, size) in _log.GetDateFiles(value.DirPath)) Files.Add(new LogFileVm(full, name, size));
        if (Files.Count > 0) SelectedFile = Files[0];
    }

    private int _previewSeq; // 预览序号：连续快速切换文件时丢弃过期结果（旧读慢于新读会反向覆盖）

    partial void OnSelectedFileChanged(LogFileVm? value)
    {
        if (value == null) return;
        var seq = ++_previewSeq;
        PreviewText = "正在读取…";
        Task.Run(() => _log.ReadTail(value.FullPath, 600))
            .ContinueWith(t =>
            {
                if (seq != _previewSeq) return; // 已切换到其它文件，过期结果不回写
                // 此前直接 t.Result：读取失败时延续任务故障被静默吞掉，预览区永远停在旧内容
                PreviewText = t.Status == TaskStatus.RanToCompletion
                    ? t.Result
                    : "日志读取失败：文件可能被占用或已被清理。";
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(_log.Root) { UseShellExecute = true }); } catch { }
    }
}
