using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingShu.Toolkit.Helpers;
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

    public LogsViewModel(ILogService log)
    {
        _log = log;
        _rootText = log.Root;
        _retentionText = $"日志保留策略：最近 {App.Services is null ? 90 : ServicesRetention()} 天（可在设置中调整）";
        _ = RefreshDatesAsync();
    }

    private int ServicesRetention()
    {
        try { return ((ISettingsService)App.Services.GetService(typeof(ISettingsService))!).Current.LogRetentionDays; }
        catch { return 90; }
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

    partial void OnSelectedFileChanged(LogFileVm? value)
    {
        if (value == null) return;
        Task.Run(() => _log.ReadTail(value.FullPath, 600))
            .ContinueWith(t =>
            {
                PreviewText = t.Result;
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(_log.Root) { UseShellExecute = true }); } catch { }
    }
}
