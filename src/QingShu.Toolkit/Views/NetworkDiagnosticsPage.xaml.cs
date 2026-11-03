using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using QingShu.Toolkit.Models;
using QingShu.Toolkit.Services;
using QingShu.Toolkit.ViewModels;
using Windows.Foundation;

namespace QingShu.Toolkit.Views;

public sealed partial class NetworkDiagnosticsPage : Page
{
    public NetworkDiagViewModel Vm { get; }

    public NetworkDiagnosticsPage()
    {
        Vm = App.Services.GetRequiredService<NetworkDiagViewModel>();
        InitializeComponent();
        // 拓扑卡：VM.Topology 变化 → Canvas 重画（诊断完成/取消/修复后三个时机都会触发）
        Vm.PropertyChanged += OnVmPropertyChanged;
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

    // ================= 网络拓扑渲染（Canvas 手绘，无第三方依赖） =================

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkDiagViewModel.Topology) or nameof(NetworkDiagViewModel.HasTopology))
            RenderTopology();
    }

    private void RenderTopology()
    {
        TopoCanvas.Children.Clear();
        var topo = Vm.Topology;
        if (topo.Nodes.Count == 0) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        TopoCanvas.Width = topo.CanvasW;
        TopoCanvas.Height = topo.CanvasH;
        var byKey = topo.Nodes.ToDictionary(n => n.Key, n => n);

        // 先画连线与标注，再画节点（节点覆盖交叠区）
        foreach (var edge in topo.Edges)
        {
            if (!byKey.TryGetValue(edge.From, out var a) || !byKey.TryGetValue(edge.To, out var b)) continue;
            double x1 = a.X + a.W / 2, y1 = a.Y + a.H / 2;
            double x2 = b.X + b.W / 2, y2 = b.Y + b.H / 2;
            var color = HealthColor(edge.Health, dark);
            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                StrokeThickness = 2,
                Stroke = new SolidColorBrush(color)
            };
            if (edge.Dashed) line.StrokeDashArray = new DoubleCollection { 2, 2 };
            TopoCanvas.Children.Add(line);

            if (string.IsNullOrEmpty(edge.Label)) continue;
            var label = MakeLabel(edge.Label, color, dark);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(0, (x1 + x2) / 2 - label.DesiredSize.Width / 2));
            Canvas.SetTop(label, Math.Max(0, (y1 + y2) / 2 - label.DesiredSize.Height / 2 - 13));
            TopoCanvas.Children.Add(label);
        }

        foreach (var n in topo.Nodes)
        {
            var nodeColor = HealthColor(n.Health, dark);
            var border = new Border
            {
                Width = n.W,
                Height = n.H,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.4),
                BorderBrush = new SolidColorBrush(nodeColor),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(dark ? 0x2E : 0x1A),
                    nodeColor.R, nodeColor.G, nodeColor.B)),
                Padding = new Thickness(10, 7, 10, 7)
            };
            ToolTipService.SetToolTip(border, string.IsNullOrEmpty(n.TooltipText)
                ? $"{n.Badge} {n.Title}\n{n.Subtitle}\n{n.StatusText}"
                : $"{n.Badge} {n.Title}\n{n.Subtitle}\n{n.StatusText}\n——\n{n.TooltipText}");

            var head = new Grid();
            head.Children.Add(new TextBlock
            {
                Text = n.Badge,
                FontSize = 10,
                MaxWidth = n.W - 34,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResolveThemeBrush("TextSecondaryBrush", dark)
            });
            head.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(nodeColor),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            });

            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock
            {
                Text = n.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ResolveThemeBrush("TextPrimaryBrush", dark)
            });
            stack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(n.StatusText) ? n.Subtitle : $"{n.Subtitle}\n{n.StatusText}",
                FontSize = 10.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ResolveThemeBrush("TextSecondaryBrush", dark)
            });
            border.Child = stack;

            Canvas.SetLeft(border, n.X);
            Canvas.SetTop(border, n.Y);
            TopoCanvas.Children.Add(border);
        }
    }

    /// <summary>按页面实际主题解析刷子（Application.Resources 只按系统主题解析，深浅不一时会取错变体）。</summary>
    private Microsoft.UI.Xaml.Media.Brush ResolveThemeBrush(string key, bool dark)
        => WindowBackdropService.ResolveThemeBrush(key, dark)
           ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private static Border MakeLabel(string text, Windows.UI.Color accent, bool dark)
    {
        byte bg = dark ? (byte)0x2B : (byte)0xFF; // 标注底色：遮住穿过文字的连线
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(dark ? 0xE0 : 0xE6), bg, bg, bg)),
            Child = new TextBlock { Text = text, FontSize = 10, Foreground = new SolidColorBrush(accent) }
        };
    }

    private static Windows.UI.Color HealthColor(TopoHealth h, bool dark) => h switch
    {
        TopoHealth.Ok => FromHex(dark ? "#5FBF77" : "#1F8A4C"),
        TopoHealth.Warn => FromHex(dark ? "#E3B341" : "#B97E14"),
        TopoHealth.Fail => FromHex(dark ? "#F26D6D" : "#C42B1C"),
        _ => FromHex(dark ? "#8E8E93" : "#7C7C80")
    };

    // 全项目约定：WinUI 3 投影不含 Windows.UI.Colors，颜色一律本地 FromHex 构造（同 MainWindow）
    private static Windows.UI.Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        byte a = 255;
        if (s.Length == 8) { a = Convert.ToByte(s[..2], 16); s = s[2..]; }
        return Windows.UI.Color.FromArgb(a,
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16));
    }
}
