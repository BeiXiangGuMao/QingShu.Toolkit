using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.ViewModels;

namespace QingShu.Toolkit.Views;

public sealed partial class NetworkDiagnosticsPage : Page
{
    public NetworkDiagViewModel Vm { get; }

    public NetworkDiagnosticsPage()
    {
        Vm = App.Services.GetRequiredService<NetworkDiagViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 仪表盘“开始全面诊断”一键直达
        if (App.PendingDiagAutostart)
        {
            App.PendingDiagAutostart = false;
            if (!Vm.IsRunning && Vm.Items.Count == 0)
                _ = Vm.StartCommand.ExecuteAsync(null);
        }
    }

    // ================= PDF 导出（询问式填写 年级/班级） =================

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Services.GetRequiredService<ISettingsService>().Current;
        var facts = await App.Services.GetRequiredService<Services.IHardwareProbeService>().EnsureLoadedAsync();

        var panel = new StackPanel { Spacing = 10 };
        TextBox School(string text, string header, string placeholder)
        {
            return new TextBox { Header = header, PlaceholderText = placeholder, Text = text };
        }
        var tbSchool = School(s.LastSchool, "学校名称", "如：市第一中学");
        var tbGrade = School(s.LastGrade, "年级（必填）", "如：高二");
        var tbClass = School(s.LastClassName, "班级（必填）", "如：3 班");
        var tbDevice = School(s.LastDeviceTag, "设备编号（默认主机名）", facts.HostName);
        var tbReporter = School(s.LastReporter, "报告人", "如：李老师");
        panel.Children.Add(tbSchool);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tbGrade, tbClass } });
        panel.Children.Add(tbDevice);
        panel.Children.Add(tbReporter);
        var hint = new TextBlock
        {
            Text = "报告将自动写入设备序列号（SMBIOS）与 MachineGuid，并按当日次数编号，便于追溯。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            // 主题刷必须按元素实际主题解析（Application.Resources 只按系统主题解析主题字典，
            // 系统深色 + 应用内浅色时会取到深色变体，浅色底上几乎不可见）
            Foreground = WindowBackdropService.ResolveThemeBrush(
                             "TextSecondaryBrush", ActualTheme == ElementTheme.Dark)
                         ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"]
        };
        panel.Children.Add(hint);

        var dlg = new ContentDialog
        {
            Title = "导出网络诊断 PDF 报告",
            Content = panel,
            PrimaryButtonText = "生成报告",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (string.IsNullOrWhiteSpace(tbGrade.Text) || string.IsNullOrWhiteSpace(tbClass.Text))
        {
            ReportInfo.Severity = InfoBarSeverity.Warning;
            ReportInfo.Title = "缺少必填信息";
            ReportInfo.Message = "年级与班级为必填项，请重新导出并填写。";
            ReportInfo.IsOpen = true;
            return;
        }

        try
        {
            var path = await Vm.ExportReportAsync(
                tbSchool.Text.Trim(), tbGrade.Text.Trim(), tbClass.Text.Trim(),
                tbReporter.Text.Trim(), tbDevice.Text.Trim());
            ReportInfo.Severity = InfoBarSeverity.Success;
            ReportInfo.Title = "报告已生成";
            ReportInfo.Message = path;
            ReportInfo.IsOpen = true;
        }
        catch (Exception ex)
        {
            ReportInfo.Severity = InfoBarSeverity.Error;
            ReportInfo.Title = "报告生成失败";
            ReportInfo.Message = ex.Message;
            ReportInfo.IsOpen = true;
        }
    }

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Vm.LastReportPath) || !File.Exists(Vm.LastReportPath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{Vm.LastReportPath}\"");
        }
        catch { }
    }
}
