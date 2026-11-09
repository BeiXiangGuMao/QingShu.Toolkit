using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace QingShu.Plugin.UsbTool;

/// <summary>
/// U盘工具导航页（纯代码构建，零 XAML 编译依赖——XAML 页面在插件 ALC 内的资源加载是硬约束，
/// 与 gala / 弹窗拦截同一模式）。布局与功能同内置 UsbToolPage.xaml 逐区对应：
/// 设备选择卡（下拉 + 刷新/健康检测/扩容检测）→ 进度行（环 + 文本 + 取消 + 提示）→
/// 左卡（检测结论 + 修复动作）| 右卡（查杀 + 隔离/解除隐藏/免疫）。
/// 列表用手工行重建（popup-guard 模式：集合变化重建、复选框事件写回），
/// 视图状态用程序化 Binding 挂 VM（DataContext + OneWay，bool→Visibility 经本地转换器）。
/// 破坏性操作（扩容检测 / 格式化重建）沿用内置页的 ContentDialog 二次确认。
/// 页面每次导航重建（宿主不缓存视图）：Loaded 首次自动刷新设备，Unloaded 取消在途破坏性操作。
/// 样式与色刷构造时从宿主 Application.Resources 取当前主题变体，取不到逐项本地兜底。
/// </summary>
public sealed class UsbToolView : UserControl
{
    private readonly UsbToolViewModel _vm;

    // 手工行容器（集合变化时整体重建）
    private readonly StackPanel _checkList;
    private readonly StackPanel _fixList;
    private readonly StackPanel _threatList;

    // 修复行动作状态变化订阅管理（重建时换绑，防旧行滞留）
    private readonly List<UsbFixAction> _watchedFixes = new();

    public UsbToolView(UsbToolViewModel vm)
    {
        _vm = vm;
        DataContext = _vm;

        _checkList = new StackPanel { Spacing = 6 };
        _fixList = new StackPanel { Spacing = 4 };
        _threatList = new StackPanel { Spacing = 6 };

        _vm.CheckResults.CollectionChanged += (_, _) => RebuildCheckRows();
        _vm.FixActions.CollectionChanged += (_, _) => RebuildFixRows();
        _vm.Threats.CollectionChanged += (_, _) => RebuildThreatRows();

        Loaded += OnLoaded;
        Unloaded += (_, _) => _vm.Dispose(); // 离开页面收尾：取消在途破坏性操作（与内置页 Unloaded 同语义）

        Content = BuildLayout();
        RebuildCheckRows();
        RebuildFixRows();
        RebuildThreatRows();
    }

    // ================= 布局 =================

    private UIElement BuildLayout()
    {
        var root = new Grid { RowSpacing = 10, Padding = new Thickness(28, 20, 28, 12) };
        var transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 12, IsStaggeringEnabled = true }
        };
        root.Transitions = transitions;
        for (int i = 0; i < 4; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // ---- 页头 ----
        var header = new StackPanel { Spacing = 0 };
        var title = new TextBlock { Text = "U盘工具（插件）" };
        ApplyStyle(title, "PageTitleTextBlock");
        var subtitle = new TextBlock
        {
            Text = "修复与查杀二合一，直连物理盘扇区做硬件级检测：表面坏区扫描、扩容盘鉴别、写速度实测；查杀覆盖U盘蠕虫的 autorun 自启、伪装文档、快捷方式劫持等落盘特征；修复提供设备栈重置、文件系统查错到分区表重建。只对 USB 设备生效，绝不触碰系统盘。",
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
        ApplyStyle(subtitle, "PageSubtitleTextBlock");
        header.Children.Add(title);
        header.Children.Add(subtitle);
        header.SetValue(Grid.RowProperty, 0);

        // ---- 设备选择卡 ----
        var combo = new ComboBox
        {
            MinWidth = 400,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = nameof(UsbDriveRow.Summary)
        };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(UsbToolViewModel.Devices)));
        combo.SetBinding(Microsoft.UI.Xaml.Controls.Primitives.Selector.SelectedItemProperty, TwoWay(nameof(UsbToolViewModel.SelectedDevice)));
        combo.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.IsNotBusy)));

        var refreshBtn = TextButton("\uE72C", "刷新设备", vm => vm.RefreshDevicesCommand);
        refreshBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.IsNotBusy)));
        refreshBtn.Padding = new Thickness(14, 5, 14, 5);

        var healthBtn = TextButton("\uE90F", "健康检测（只读，1-2分钟）", vm => vm.RunHealthCommand, accent: true);
        healthBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        healthBtn.Padding = new Thickness(14, 5, 14, 5);

        var capacityBtn = TextButton("\uE7BC", "扩容盘检测（破坏性）", null);
        capacityBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        capacityBtn.Padding = new Thickness(14, 5, 14, 5);
        capacityBtn.Click += async (_, _) => await ShowCapacityDialogAsync();

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnRow.Children.Add(refreshBtn);
        btnRow.Children.Add(healthBtn);
        btnRow.Children.Add(capacityBtn);

        var devicePicker = new StackPanel { Spacing = 6, MinWidth = 430 };
        devicePicker.Children.Add(combo);
        devicePicker.Children.Add(btnRow);

        // 设备详情（HasDevice 时可见）
        var infoSummary = new TextBlock(); ApplyStyle(infoSummary, "BodyTextBlock");
        infoSummary.SetBinding(TextBlock.TextProperty, OneWay("SelectedDevice.Summary"));
        var infoBus = Caption(Secondary());
        infoBus.SetBinding(TextBlock.TextProperty, OneWay("SelectedDevice.BusText"));
        var infoSerial = Caption(Secondary());
        infoSerial.SetBinding(TextBlock.TextProperty, OneWay("SelectedDevice.SerialText"));
        var infoVolumes = Caption(Secondary());
        infoVolumes.TextWrapping = TextWrapping.Wrap;
        infoVolumes.MaxWidth = 430;
        infoVolumes.SetBinding(TextBlock.TextProperty, OneWay("SelectedDevice.VolumesText"));

        var deviceInfo = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        deviceInfo.Children.Add(infoSummary);
        deviceInfo.Children.Add(infoBus);
        deviceInfo.Children.Add(infoSerial);
        deviceInfo.Children.Add(infoVolumes);
        deviceInfo.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(UsbToolViewModel.HasDevice)));

        // 无设备说明（IsEmptyResult 时可见）
        var emptyHint = new TextBlock
        {
            Text = "无设备时说明：未识别到 USBSTOR 设备。手机 MTP 模式不是U盘，需以「U盘模式/文件传输」或读卡器接入；部分劣质读卡器不在支持范围。",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        };
        ApplyStyle(emptyHint, "CaptionTextBlock");
        emptyHint.Foreground = TryBrush("PlaceholderTextBrush") ?? Secondary();
        emptyHint.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(UsbToolViewModel.IsEmptyResult)));

        var deviceGrid = new Grid { ColumnSpacing = 16 };
        deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        deviceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        devicePicker.SetValue(Grid.ColumnProperty, 0);
        deviceInfo.SetValue(Grid.ColumnProperty, 1);
        emptyHint.SetValue(Grid.ColumnProperty, 2);
        deviceGrid.Children.Add(devicePicker);
        deviceGrid.Children.Add(deviceInfo);
        deviceGrid.Children.Add(emptyHint);

        var deviceCard = MakeCard(14);
        ((StackPanel)deviceCard.Child).Children.Add(deviceGrid);
        deviceCard.SetValue(Grid.RowProperty, 1);

        // ---- 进度 + 提示行 ----
        var ring = new ProgressRing { Width = 18, Height = 18 };
        ring.SetBinding(ProgressRing.IsActiveProperty, OneWay(nameof(UsbToolViewModel.IsBusy)));

        var progressText = Caption(Secondary());
        progressText.VerticalAlignment = VerticalAlignment.Center;
        progressText.SetBinding(TextBlock.TextProperty, OneWay(nameof(UsbToolViewModel.ProgressText)));

        var cancelBtn = new Button { Content = "取消操作", Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        cancelBtn.Command = _vm.CancelOperationCommand;

        var progressRow = new Grid { ColumnSpacing = 10 };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ring.SetValue(Grid.ColumnProperty, 0);
        progressText.SetValue(Grid.ColumnProperty, 1);
        cancelBtn.SetValue(Grid.ColumnProperty, 2);
        progressRow.Children.Add(ring);
        progressRow.Children.Add(progressText);
        progressRow.Children.Add(cancelBtn);
        progressRow.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(UsbToolViewModel.IsBusy)));

        var hintText = Caption(Secondary());
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.SetBinding(TextBlock.TextProperty, OneWay(nameof(UsbToolViewModel.Hint)));

        var progressCol = new StackPanel { Spacing = 6 };
        progressCol.Children.Add(progressRow);
        progressCol.Children.Add(hintText);
        progressCol.SetValue(Grid.RowProperty, 2);

        // ---- 双卡：检测与修复 | 查杀 ----

        // 左卡：检测结论 + 修复动作
        var checkScroll = new ScrollViewer
        {
            Content = _checkList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var fixLabel = Caption(Secondary());
        fixLabel.Text = "修复动作（管理类合并为一次 UAC；不会触碰系统盘）";
        fixLabel.Margin = new Thickness(0, 4, 0, 0);

        var fixScroll = new ScrollViewer
        {
            Content = _fixList,
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var fixBtn = TextButton("\uE768", "修复所选", vm => vm.FixSelectedCommand, accent: true);
        fixBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        fixBtn.Padding = new Thickness(16, 6, 16, 6);

        var rebuildBtn = new Button { Padding = new Thickness(14, 6, 14, 6) };
        rebuildBtn.Content = new TextBlock { Text = "格式化重建（高级）" };
        rebuildBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        rebuildBtn.Click += async (_, _) => await ShowRebuildDialogAsync();

        var fixBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        fixBtnRow.Children.Add(fixBtn);
        fixBtnRow.Children.Add(rebuildBtn);

        var leftPanel = new Grid { RowSpacing = 8 };
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var leftTitle = new TextBlock { Text = "检测结论与修复" };
        ApplyStyle(leftTitle, "SectionTitleTextBlock");
        leftTitle.SetValue(Grid.RowProperty, 0);
        checkScroll.SetValue(Grid.RowProperty, 1);
        fixLabel.SetValue(Grid.RowProperty, 2);
        fixScroll.SetValue(Grid.RowProperty, 3);
        fixBtnRow.SetValue(Grid.RowProperty, 4);
        leftPanel.Children.Add(leftTitle);
        leftPanel.Children.Add(checkScroll);
        leftPanel.Children.Add(fixLabel);
        leftPanel.Children.Add(fixScroll);
        leftPanel.Children.Add(fixBtnRow);

        var leftCard = MakeCard(14);
        ((StackPanel)leftCard.Child).Children.Add(leftPanel);

        // 右卡：查杀
        var scanBtn = TextButton("\uE71E", "开始查杀", vm => vm.RunScanCommand, accent: true);
        scanBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        scanBtn.Padding = new Thickness(14, 5, 14, 5);

        var quarantineBtn = new Button { Content = new TextBlock { Text = "隔离所选" }, Padding = new Thickness(12, 5, 12, 5) };
        quarantineBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        quarantineBtn.Command = _vm.QuarantineSelectedCommand;

        var unhideBtn = new Button { Content = new TextBlock { Text = "解除隐藏" }, Padding = new Thickness(12, 5, 12, 5) };
        unhideBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        unhideBtn.Command = _vm.UnhideCommand;

        var immuneBtn = new Button { Content = new TextBlock { Text = "U盘免疫" }, Padding = new Thickness(12, 5, 12, 5) };
        immuneBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(UsbToolViewModel.CanOperate)));
        immuneBtn.Command = _vm.ImmuneCommand;

        var scanBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        scanBtnRow.Children.Add(scanBtn);
        scanBtnRow.Children.Add(quarantineBtn);
        scanBtnRow.Children.Add(unhideBtn);
        scanBtnRow.Children.Add(immuneBtn);

        var threatScroll = new ScrollViewer
        {
            Content = _threatList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var threatSummary = Caption(Primary());
        threatSummary.TextWrapping = TextWrapping.Wrap;
        threatSummary.SetBinding(TextBlock.TextProperty, OneWay(nameof(UsbToolViewModel.ThreatSummary)));

        var rightPanel = new Grid { RowSpacing = 8 };
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rightTitle = new TextBlock { Text = "U盘查杀（蠕虫落盘特征 · 离线扫描）" };
        ApplyStyle(rightTitle, "SectionTitleTextBlock");
        rightTitle.SetValue(Grid.RowProperty, 0);
        scanBtnRow.SetValue(Grid.RowProperty, 1);
        threatScroll.SetValue(Grid.RowProperty, 2);
        threatSummary.SetValue(Grid.RowProperty, 3);
        rightPanel.Children.Add(rightTitle);
        rightPanel.Children.Add(scanBtnRow);
        rightPanel.Children.Add(threatScroll);
        rightPanel.Children.Add(threatSummary);

        var rightCard = MakeCard(14);
        ((StackPanel)rightCard.Child).Children.Add(rightPanel);
        rightCard.SetValue(Grid.ColumnProperty, 1);

        var dualGrid = new Grid { ColumnSpacing = 12 };
        dualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dualGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftCard.SetValue(Grid.ColumnProperty, 0);
        dualGrid.Children.Add(leftCard);
        dualGrid.Children.Add(rightCard);
        dualGrid.SetValue(Grid.RowProperty, 3);

        root.Children.Add(header);
        root.Children.Add(deviceCard);
        root.Children.Add(progressCol);
        root.Children.Add(dualGrid);
        return root;
    }

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 与内置页 OnNavigatedTo 同语义：进入页面且无设备时自动刷新一次
        if (_vm.Devices.Count == 0 && !_vm.IsBusy)
            _ = _vm.RefreshDevicesCommand.ExecuteAsync(null);
    }

    // ================= 手工行重建 =================

    private void RebuildCheckRows()
    {
        _checkList.Children.Clear();
        foreach (var r in _vm.CheckResults)
        {
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (r.PassVis) chipRow.Children.Add(Chip("正常", "SuccessChipFillBrush", "SuccessTextBrush"));
            if (r.WarnVis) chipRow.Children.Add(Chip("注意", "WarningChipFillBrush", "WarningTextBrush"));
            if (r.FailVis) chipRow.Children.Add(Chip("异常", "ErrorChipFillBrush", "ErrorTextBrush"));
            if (r.SkipVis) chipRow.Children.Add(Chip("跳过", "InfoChipFillBrush", "InfoTextBrush"));

            var titleText = Caption(Primary());
            titleText.Text = r.Title;
            titleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            chipRow.Children.Add(titleText);

            var cell = new StackPanel { Spacing = 1, Padding = new Thickness(2, 3, 2, 3) };
            cell.Children.Add(chipRow);

            var summaryText = Caption(Secondary());
            summaryText.Text = r.Summary;
            summaryText.TextWrapping = TextWrapping.Wrap;
            cell.Children.Add(summaryText);

            if (r.SuggestionVis)
            {
                var sug = Caption(Secondary());
                sug.Text = r.Suggestion;
                sug.TextWrapping = TextWrapping.Wrap;
                cell.Children.Add(sug);
            }
            if (r.DetailsVis)
            {
                var det = Caption(TryBrush("PlaceholderTextBrush") ?? Secondary());
                det.Text = r.Details;
                det.TextWrapping = TextWrapping.Wrap;
                cell.Children.Add(det);
            }
            _checkList.Children.Add(cell);
        }
        if (_vm.CheckResults.Count == 0)
        {
            var empty = Caption(TryBrush("PlaceholderTextBrush") ?? Secondary());
            empty.Text = "尚未执行检测。选择设备后点「健康检测」或「扩容盘检测」。";
            empty.Margin = new Thickness(2, 6, 2, 6);
            _checkList.Children.Add(empty);
        }
    }

    private void RebuildFixRows()
    {
        foreach (var a in _watchedFixes) a.PropertyChanged -= OnFixActionChanged;
        _watchedFixes.Clear();
        _fixList.Children.Clear();
        foreach (var a in _vm.FixActions)
        {
            a.PropertyChanged += OnFixActionChanged;
            _watchedFixes.Add(a);
            _fixList.Children.Add(BuildFixRow(a));
        }
    }

    private void OnFixActionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RebuildFixRows(); // 状态/勾选变化 → 整表重建（≤6 行，代价可忽略；IsSelected 保留在行对象）

    private UIElement BuildFixRow(UsbFixAction a)
    {
        var chk = new CheckBox { IsChecked = a.IsSelected, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        chk.Checked += (_, _) => a.IsSelected = true;
        chk.Unchecked += (_, _) => a.IsSelected = false;

        var titleText = Caption(Primary());
        titleText.Text = a.Title;

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(titleText);
        if (a.Risky)
        {
            var risky = new Border
            {
                Background = TryBrush("WarningChipFillBrush") ?? FallbackWarnChipBrush(),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 0)
            };
            risky.Child = new TextBlock { Text = "清空数据", FontSize = 10, Foreground = TryBrush("WarningTextBrush") ?? FallbackWarnBrush() };
            titleRow.Children.Add(risky);
        }

        var detailText = Caption(Secondary());
        detailText.Text = a.Detail;
        detailText.TextTrimming = TextTrimming.CharacterEllipsis;

        var nameCol = new StackPanel { Spacing = 1 };
        nameCol.Children.Add(titleRow);
        nameCol.Children.Add(detailText);

        var statusCol = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var adminText = new TextBlock { Text = a.AdminText, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        adminText.Foreground = TryBrush("PlaceholderTextBrush") ?? Secondary();
        statusCol.Children.Add(adminText);
        if (a.OkVis) statusCol.Children.Add(Chip("成功", "SuccessChipFillBrush", "SuccessTextBrush"));
        if (a.FailVis) statusCol.Children.Add(Chip("失败", "ErrorChipFillBrush", "ErrorTextBrush"));
        if (a.RunningVis) statusCol.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });

        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 2, 2, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLengthHelper.FromAutoLength() });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLengthHelper.FromStar(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLengthHelper.FromAutoLength() });
        chk.SetValue(Grid.ColumnProperty, 0);
        nameCol.SetValue(Grid.ColumnProperty, 1);
        statusCol.SetValue(Grid.ColumnProperty, 2);
        grid.Children.Add(chk);
        grid.Children.Add(nameCol);
        grid.Children.Add(statusCol);
        return grid;
    }

    private void RebuildThreatRows()
    {
        _threatList.Children.Clear();
        foreach (var t in _vm.Threats)
        {
            var chk = new CheckBox { IsChecked = t.IsSelected, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            chk.Checked += (_, _) => t.IsSelected = true;
            chk.Unchecked += (_, _) => t.IsSelected = false;

            var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            chipRow.Children.Add(t.MalVis
                ? Chip("恶意", "ErrorChipFillBrush", "ErrorTextBrush", pad: new Thickness(5, 0, 5, 0))
                : Chip("可疑", "WarningChipFillBrush", "WarningTextBrush", pad: new Thickness(5, 0, 5, 0)));

            var nameText = Caption(Primary());
            nameText.Text = t.FileName;
            nameText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            chipRow.Children.Add(nameText);

            var reasonText = Caption(Secondary());
            reasonText.Text = t.Reason;
            chipRow.Children.Add(reasonText);

            var detailText = Caption(Secondary());
            detailText.Text = t.Detail;
            detailText.TextWrapping = TextWrapping.Wrap;

            var pathText = new TextBlock { Text = t.FilePath, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
            pathText.Foreground = TryBrush("PlaceholderTextBrush") ?? Secondary();

            var nameCol = new StackPanel { Spacing = 1 };
            nameCol.Children.Add(chipRow);
            nameCol.Children.Add(detailText);
            nameCol.Children.Add(pathText);

            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 2, 2, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLengthHelper.FromAutoLength() });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLengthHelper.FromStar(1) });
            chk.SetValue(Grid.ColumnProperty, 0);
            nameCol.SetValue(Grid.ColumnProperty, 1);
            grid.Children.Add(chk);
            grid.Children.Add(nameCol);
            _threatList.Children.Add(grid);
        }
        if (_vm.Threats.Count == 0)
        {
            var empty = Caption(TryBrush("PlaceholderTextBrush") ?? Secondary());
            empty.Text = _vm.ThreatSummary.Length > 0 ? "未发现威胁。" : "尚未执行查杀。选择设备后点「开始查杀」。";
            empty.Margin = new Thickness(2, 6, 2, 6);
            _threatList.Children.Add(empty);
        }
    }

    // ================= 破坏性操作二次确认（同内置页 ContentDialog） =================

    private async System.Threading.Tasks.Task ShowCapacityDialogAsync()
    {
        if (_vm.SelectedDevice == null) return;
        if (XamlRoot == null)
        {
            _vm.Hint = "页面尚未就绪，请重试。";
            return;
        }
        var chk = new CheckBox
        {
            Content = "我已确认U盘数据可全部丢失（探针区域将被覆盖，检测期间U盘不可用）"
        };
        var dlg = new ContentDialog
        {
            Title = "扩容盘检测（硬件级写校验）",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "检测原理：向盘内 4 处区域（8MB / 中点 / 95% / 末尾内缩）各写入 4MB 特征数据并回读比对。\n" +
                               "· 虚标容量的劣质U盘（黑片/升级片）会在超出真实容量的位置暴露：回读失败或内容不符。\n" +
                               "· 写入区域的原有数据将永久丢失；检测后U盘可能需要重新插拔恢复访问。\n" +
                               "· 请先备份重要文件。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    chk
                }
            },
            PrimaryButtonText = "开始检测",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        chk.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        chk.Unchecked += (_, _) => dlg.IsPrimaryButtonEnabled = false;
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await _vm.RunCapacityVerifiedAsync();
    }

    private async System.Threading.Tasks.Task ShowRebuildDialogAsync()
    {
        var d = _vm.SelectedDevice;
        if (d == null) return;
        if (XamlRoot == null)
        {
            _vm.Hint = "页面尚未就绪，请重试。";
            return;
        }

        var rbExfat = new RadioButton { Content = $"exFAT（大容量推荐{(d.Drive.SizeBytes > 32L * 1024 * 1024 * 1024 ? "，FAT32 不支持超 32GB" : "")}）", IsChecked = true };
        var rbFat32 = new RadioButton { Content = "FAT32（兼容性最好，仅 ≤32GB 生效，超出自动转 exFAT）" };
        var rbNtfs = new RadioButton { Content = "NTFS（支持权限与大文件，部分设备/老系统读取慢）" };
        var chk = new CheckBox { Content = $"我已确认：{d.Summary} 上的全部数据将被清空" };

        var dlg = new ContentDialog
        {
            Title = "格式化重建分区表（高级）",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "用于：分区表损坏、容量识别异常、无法格式化、隐性坏区需重新划分。\n" +
                               "过程 = clean 清空分区表 → 新建主分区 → 快速格式化 → 重新分配盘符（需管理员授权）。\n" +
                               "注意：这是修复的最后手段，会清空U盘全部数据，请先备份。",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = "选择文件系统：", FontSize = 12 },
                    rbExfat, rbFat32, rbNtfs,
                    chk
                }
            },
            PrimaryButtonText = "执行重建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };
        chk.Checked += (_, _) => dlg.IsPrimaryButtonEnabled = true;
        chk.Unchecked += (_, _) => dlg.IsPrimaryButtonEnabled = false;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var fs = rbNtfs.IsChecked == true ? "ntfs" : rbFat32.IsChecked == true ? "fat32" : "exfat";
        await _vm.RebuildPartitionVerifiedAsync(fs);
    }

    // ================= 构建辅助 =================

    /// <summary>图标 + 文本的命令按钮（accent 可选）。commandSelector 为 null 时由调用方自行接 Click。</summary>
    private Button TextButton(string glyph, string text, Func<UsbToolViewModel, System.Windows.Input.ICommand>? commandSelector, bool accent = false)
    {
        var b = new Button();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new SymbolIcon { Symbol = GlyphToSymbol(glyph) });
        row.Children.Add(new TextBlock { Text = text });
        b.Content = row;
        if (commandSelector != null) b.Command = commandSelector(_vm);
        if (accent) ApplyStyle(b, "AccentButtonStyle");
        return b;
    }

    // SymbolIcon.Symbol 需枚举；本页四处图标（刷新/修复/报告/播放/查找）直接用枚举映射
    private static Symbol GlyphToSymbol(string glyph) => glyph switch
    {
        "\uE72C" => Symbol.Refresh,
        "\uE90F" => Symbol.Repair,
        "\uE768" => Symbol.Play,
        "\uE71E" => Symbol.Find,
        _ => Symbol.ReportHacked // \uE7BC 扩容检测（ReportHacked 图标语义一致）
    };

    /// <summary>状态徽章（宿主 Chip 资源色；取不到本地兜底）。</summary>
    private static Border Chip(string text, string fillKey, string textKey, Thickness? pad = null)
    {
        var b = new Border
        {
            Background = TryBrush(fillKey) ?? FallbackInfoChipBrush(),
            CornerRadius = new CornerRadius(4),
            Padding = pad ?? new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        b.Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            Foreground = TryBrush(textKey) ?? FallbackInfoBrush()
        };
        return b;
    }

    /// <summary>标准卡片容器（宿主 CardStyle；取不到时本地兜底：圆角+描边）。初始内容为空 StackPanel。</summary>
    private static Border MakeCard(double padding)
    {
        var b = new Border { Padding = new Thickness(padding) };
        var style = TryStyle("CardStyle");
        if (style != null)
        {
            b.Style = style;
        }
        else
        {
            b.CornerRadius = new CornerRadius(12);
            b.Background = TryBrush("CardBackgroundBrush") ?? new SolidColorBrush(ColorsTransparent);
            b.BorderBrush = TryBrush("DividerStrokeBrush") ?? new SolidColorBrush(ColorsTransparent);
            b.BorderThickness = new Thickness(1);
        }
        b.Child = new StackPanel { Spacing = 10 };
        return b;
    }

    private static TextBlock Caption(Brush fg)
    {
        var t = new TextBlock();
        ApplyStyle(t, "CaptionTextBlock");
        t.Foreground = fg;
        return t;
    }

    private static void ApplyStyle(FrameworkElement e, string key)
    {
        try
        {
            var s = TryStyle(key);
            if (s != null) e.Style = s; // 样式 TargetType 不符会抛，吞掉保持本地兜底外观
        }
        catch { }
    }

    private static Brush? TryBrush(string key)
    {
        try { return Application.Current.Resources[key] as Brush; } catch { return null; }
    }

    private static Style? TryStyle(string key)
    {
        try { return Application.Current.Resources[key] as Style; } catch { return null; }
    }

    private static Brush Primary() => TryBrush("TextPrimaryBrush") ?? new SolidColorBrush(ColorsTransparent);
    private static Brush Secondary() => TryBrush("TextSecondaryBrush") ?? new SolidColorBrush(ColorsTransparent);
    private static Brush FallbackInfoBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 98, 204));
    private static Brush FallbackInfoChipBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(26, 0, 122, 255));
    private static Brush FallbackWarnBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 201, 52, 0));
    private static Brush FallbackWarnChipBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 149, 0));

    private static readonly Windows.UI.Color ColorsTransparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    // ================= Binding 辅助 =================

    private static Binding OneWay(string path) => new()
    {
        Path = new PropertyPath(path),
        Mode = BindingMode.OneWay
    };

    private static Binding TwoWay(string path) => new()
    {
        Path = new PropertyPath(path),
        Mode = BindingMode.TwoWay
    };

    /// <summary>bool → Visibility（经典 Binding 不做隐式转换，经本地转换器显式转换）。</summary>
    private static Binding Vis(string path) => new()
    {
        Path = new PropertyPath(path),
        Mode = BindingMode.OneWay,
        Converter = BoolToVisConverter.Instance
    };

    private sealed class BoolToVisConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public static readonly BoolToVisConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
