using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace QingShu.Plugin.NetworkDiag;

/// <summary>
/// 网络诊断导航页（纯代码构建，零 XAML 编译依赖——XAML 页面在插件 ALC 内的资源加载是硬约束，
/// 与弹窗拦截 / U盘工具 / 磁盘清理 / 性能优化同一模式）。布局与功能同内置 NetworkDiagnosticsPage.xaml
/// 逐区对应：页头（标题+副标题+模式/开始/取消）→ 进度卡（进度文本+四统计芯片+进度条+会话行）→
/// 实时测速卡（仅网速模式）→ 低速原因分析卡 → 网络拓扑卡（Canvas 手绘）→ 检查结果列表 → 底部
/// 修复 + 导出双卡。检查项 / 修复动作 / 低速嫌疑三个列表保留 ListView + DataTemplate 虚拟化：
/// 模板经 XamlReader.Load 运行时解析走经典 Binding（x:Bind 是编译期生成器行为，运行时模板不可用；
/// 内置页低速嫌疑用 ItemsControl，插件统一为 ListView + 行容器样式，视觉等价、小列表无性能差异）；
/// 行模型直接暴露 Visibility 类型 XxxVisState 属性（NetworkDiagModels 已齐备，绑定免转换器），
/// 视图层（非模板）bool→Visibility 沿用本地 IValueConverter（Vis 辅助）。
/// 样式两级降级：模板内 StaticResource/ThemeResource 在宿主 Application.Resources 取不到时
/// 解析抛错 → 自动降级无资源模板（默认前景 + 半透明兜底色），构造期一次性选定并留插件日志。
/// Canvas 拓扑逐行移植内置 code-behind（RenderTopology/节点边绘制/标注/图例）：VM.Topology 变化
/// 触发重画，主题切换按新主题重画；颜色沿用内置页本地 FromHex 调色板（宿主内部主题解析服务不进
/// 插件，文本刷退化为 Application.Resources 当前变体 + 本地兜底色，Canvas 颜色恒为本地兜底调色板）。
/// PDF 导出 ContentDialog 五字段表单（学校/年级/班级/报告人/设备编号）逐字对应内置页：表单记忆
/// Last* 五项在插件 store（VM 不暴露 store），由构造函数直接传入同一实例读取；成功后 InfoBar 提示
/// 路径，「打开所在文件夹」explorer /select 选中。未移植内置页 OnNavigatedTo 的仪表盘一键直达
/// （App.PendingDiagAutostart 为宿主内部状态，插件无此入口）。Unloaded → VM.Dispose 收尾。
/// </summary>
public sealed class NetworkDiagView : UserControl
{
    private readonly NetworkDiagViewModel _vm;
    private readonly NetworkDiagSettingsStore _settings; // 报告表单记忆（Last* 五项）来源（内置页取宿主设置服务的插件对应物）
    private readonly IHardwareProbeService _probe;       // 设备事实探测（导出表单「设备编号」占位主机名）
    private readonly Action<string, string> _log;        // (level, message) → context.Log（模板降级等视图级日志）
    private readonly bool _hasStyles;                    // 宿主样式键齐全 → 尝试样式模板；否则直接走无资源降级模板
    private Canvas? _topoCanvas;                         // 拓扑手绘画布（BuildLayout 装配；重画前判空防御）
    private InfoBar? _reportInfo;                        // 导出结果提示（成功路径 / 失败原因 / 缺必填）

    public NetworkDiagView(NetworkDiagViewModel vm, NetworkDiagSettingsStore settings,
        IHardwareProbeService probe, Action<string, string> log)
    {
        _vm = vm;
        _settings = settings;
        _probe = probe;
        _log = log;
        DataContext = _vm;
        _hasStyles = TryStyle("CardStyle") != null && TryStyle("CaptionTextBlock") != null && TryBrush("TextPrimaryBrush") != null;

        // 拓扑卡：VM.Topology 变化 → Canvas 重画（诊断完成/取消/修复后三个时机都会触发）
        _vm.PropertyChanged += OnVmPropertyChanged;
        // 离开页面收尾：取消在途诊断/修复并释放取消源（与内置页 Unloaded 同语义；
        // VM 为 transient，不收尾则旧操作后台裸奔且与复入页的新 VM 并发竞争）
        Unloaded += (_, _) => _vm.Dispose();
        // 主题切换时按新主题重画拓扑：节点/连线颜色是本地 FromHex 构造，不随 ThemeResource 自动切换
        ActualThemeChanged += (_, _) => RenderTopology();

        Content = BuildLayout();
    }

    // ================= 布局（内置 NetworkDiagnosticsPage.xaml 七行逐区对应） =================

    private UIElement BuildLayout()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        var root = new Grid { RowSpacing = 12, Padding = new Thickness(28, 20, 28, 12) };
        root.Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 12, IsStaggeringEnabled = true }
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 页头
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1 进度卡
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 实时测速卡
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 3 低速原因分析卡
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 网络拓扑卡
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 5 检查结果列表
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6 修复 + 导出

        // ---- 行 0：页头 + 启动区 ----
        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel { Spacing = 0 };
        var title = new TextBlock { Text = "网络诊断" };
        ApplyStyle(title, "PageTitleTextBlock");
        var subtitle = new TextBlock
        {
            Text = "傻瓜模式：点一下就开始。全面诊断覆盖物理链路、认证、内外网与系统栈全链路；网速模式专门分析“变慢”的原因并给出可执行的提速修复；发现问题可一键修复，修复不了可导出 PDF 报告。",
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
        ApplyStyle(subtitle, "PageSubtitleTextBlock");
        titlePanel.Children.Add(title);
        titlePanel.Children.Add(subtitle);
        headerGrid.Children.Add(titlePanel);

        var startCluster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        var modeCombo = new ComboBox { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
        modeCombo.Items.Add(new ComboBoxItem { Content = "全面诊断（约2-4分钟）" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "快速体检（约30-60秒）" });
        modeCombo.Items.Add(new ComboBoxItem { Content = "网速与低速分析（约1-2分钟）" });
        modeCombo.SetBinding(ComboBox.SelectedIndexProperty, TwoWay(nameof(NetworkDiagViewModel.ModeIndex)));
        startCluster.Children.Add(modeCombo);

        var startBtn = new Button { Padding = new Thickness(22, 8, 22, 8) };
        var startRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        startRow.Children.Add(new SymbolIcon { Symbol = Symbol.Play });
        startRow.Children.Add(new TextBlock { Text = "开始诊断" });
        startBtn.Content = startRow;
        startBtn.Command = _vm.StartCommand;
        ApplyStyle(startBtn, "AccentButtonStyle");
        startCluster.Children.Add(startBtn);

        var cancelBtn = new Button { Content = "取消", VerticalAlignment = VerticalAlignment.Center };
        cancelBtn.Command = _vm.CancelCommand;
        cancelBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(NetworkDiagViewModel.IsRunning)));
        startCluster.Children.Add(cancelBtn);
        startCluster.SetValue(Grid.ColumnProperty, 1);
        headerGrid.Children.Add(startCluster);
        headerGrid.SetValue(Grid.RowProperty, 0);
        root.Children.Add(headerGrid);

        // ---- 行 1：进度卡（进度文本 + 通过/警告/未通过/已修复四芯片 + 进度条 + 会话行） ----
        var progressText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ApplyStyle(progressText, "BodyTextBlock");
        progressText.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.ProgressText)));

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        chipRow.Children.Add(CountChip(FillBrush("SuccessChipFillBrush", "#1A107C10"), FillBrush("SuccessTextBrush", "#FF107C10"),
            nameof(NetworkDiagViewModel.PassCountText)));
        chipRow.Children.Add(CountChip(FillBrush("WarningChipFillBrush", "#1AFF9500"), FillBrush("WarningTextBrush", "#FFC93400"),
            nameof(NetworkDiagViewModel.WarnCountText)));
        chipRow.Children.Add(CountChip(FillBrush("ErrorChipFillBrush", "#1AC42B1C"), FillBrush("ErrorTextBrush", "#FFC42B1C"),
            nameof(NetworkDiagViewModel.FailCountText)));
        var fixedChip = CountChip(FillBrush("InfoChipFillBrush", "#1A0062CC"), FillBrush("InfoTextBrush", "#FF0062CC"),
            nameof(NetworkDiagViewModel.FixedCountText));
        fixedChip.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.FixedChipVis))); // FixedCount>0 才显示
        chipRow.Children.Add(fixedChip);

        // 内置页：进度文本与右侧芯片行同格叠放（无列定义，芯片行右对齐覆盖空白区）
        var progressLine = new Grid();
        progressLine.Children.Add(progressText);
        progressLine.Children.Add(chipRow);

        var progress = new ProgressBar { CornerRadius = new CornerRadius(2) };
        progress.SetBinding(ProgressBar.ValueProperty, OneWay(nameof(NetworkDiagViewModel.ProgressValue)));
        progress.SetBinding(ProgressBar.MaximumProperty, OneWay(nameof(NetworkDiagViewModel.ProgressMax)));
        progress.SetBinding(ProgressBar.IsIndeterminateProperty, OneWay(nameof(NetworkDiagViewModel.IsRunning)));

        var sessionText = new TextBlock();
        ApplyStyle(sessionText, "CaptionTextBlock");
        sessionText.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.SessionText)));
        sessionText.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.SessionTextVis)));

        var progressStack = new StackPanel { Spacing = 8 };
        progressStack.Children.Add(progressLine);
        progressStack.Children.Add(progress);
        progressStack.Children.Add(sessionText);
        var progressCard = MakeCard(new Thickness(16, 12, 16, 12), progressStack);
        progressCard.SetValue(Grid.RowProperty, 1);
        root.Children.Add(progressCard);

        // ---- 行 2：实时测速卡（仅网速与低速分析模式显示） ----
        var speedNumber = new TextBlock { FontSize = 34, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        speedNumber.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.SpeedMbpsText)));
        speedNumber.Foreground = FillBrush("AccentTextFillColorPrimaryBrush", dark ? "#FF99EBFF" : "#FF0062CC");
        var mbpsUnit = new TextBlock { Text = "Mbps", FontSize = 13, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9) };
        mbpsUnit.Foreground = SecondaryBrush(dark);
        var speedLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        speedLeft.Children.Add(speedNumber);
        speedLeft.Children.Add(mbpsUnit);

        var speedPhase = new TextBlock { FontWeight = FontWeights.SemiBold };
        ApplyStyle(speedPhase, "BodyTextBlock");
        speedPhase.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.SpeedPhaseText)));
        var speedSub = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        ApplyStyle(speedSub, "CaptionTextBlock");
        speedSub.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.SpeedSubText)));
        var speedHint = new TextBlock { Text = "下行带宽实测：多节点多连接限时下载；全程只读、不落盘、不上传。" };
        ApplyStyle(speedHint, "CaptionTextBlock");
        speedHint.Foreground = SecondaryBrush(dark);
        var speedRight = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        speedRight.Children.Add(speedPhase);
        speedRight.Children.Add(speedSub);
        speedRight.Children.Add(speedHint);

        var speedGrid = new Grid { ColumnSpacing = 20 };
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedGrid.Children.Add(speedLeft);
        speedRight.SetValue(Grid.ColumnProperty, 1);
        speedGrid.Children.Add(speedRight);
        var speedCard = MakeCard(new Thickness(16, 12, 16, 12), speedGrid);
        speedCard.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.HasSpeedCard)));
        speedCard.SetValue(Grid.RowProperty, 2);
        root.Children.Add(speedCard);

        // ---- 行 3：低速原因分析卡（嫌疑清单 ListView，按嫌疑度排序） ----
        var slowTitle = new TextBlock { Text = "低速原因分析（按嫌疑度排序）" };
        ApplyStyle(slowTitle, "SectionTitleTextBlock");
        var slowConclusion = new TextBlock();
        ApplyStyle(slowConclusion, "CaptionTextBlock");
        slowConclusion.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.SlowConclusionText)));
        var causeList = MakeList(LoadTemplate(TemplateCauseStyled(), TemplateCausePlain()));
        causeList.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(NetworkDiagViewModel.SlowCauses)));
        var slowStack = new StackPanel { Spacing = 6 };
        slowStack.Children.Add(slowTitle);
        slowStack.Children.Add(slowConclusion);
        slowStack.Children.Add(causeList);
        var slowCard = MakeCard(new Thickness(16, 12, 16, 12), slowStack);
        slowCard.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.HasSlowCauses)));
        slowCard.SetValue(Grid.RowProperty, 3);
        root.Children.Add(slowCard);

        // ---- 行 4：网络拓扑卡（摘要 + 生成来源 + Canvas 手绘 + 图例） ----
        var topoTitle = new TextBlock { Text = "网络拓扑图", VerticalAlignment = VerticalAlignment.Center };
        ApplyStyle(topoTitle, "SectionTitleTextBlock");
        var topoSummary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ApplyStyle(topoSummary, "CaptionTextBlock");
        topoSummary.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.TopologySummary)));
        var topoBuilt = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        ApplyStyle(topoBuilt, "CaptionTextBlock");
        topoBuilt.Foreground = SecondaryBrush(dark);
        topoBuilt.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.TopologyBuiltText)));
        var topoHead = new Grid { ColumnSpacing = 12 };
        topoHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topoHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topoHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topoHead.Children.Add(topoTitle);
        topoSummary.SetValue(Grid.ColumnProperty, 1);
        topoHead.Children.Add(topoSummary);
        topoBuilt.SetValue(Grid.ColumnProperty, 2);
        topoHead.Children.Add(topoBuilt);

        _topoCanvas = new Canvas { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
        var topoBox = new Viewbox
        {
            Height = 230,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _topoCanvas
        };
        var topoScroller = new ScrollViewer
        {
            Content = topoBox,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };

        // 图例（主题刷构造期解析一次：样式齐全时取宿主当前变体，缺失时本地兜底色）
        var secondary = SecondaryBrush(dark);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        legend.Children.Add(LegendItem(FillBrush("SuccessChipFillBrush", "#1A107C10"), FillBrush("SuccessTextBrush", "#FF107C10"), "正常", secondary));
        legend.Children.Add(LegendItem(FillBrush("WarningChipFillBrush", "#1AFF9500"), FillBrush("WarningTextBrush", "#FFC93400"), "警告", secondary));
        legend.Children.Add(LegendItem(FillBrush("ErrorChipFillBrush", "#1AC42B1C"), FillBrush("ErrorTextBrush", "#FFC42B1C"), "未通过", secondary));
        legend.Children.Add(LegendItem(FillBrush("CardBorderBrush", "#33808080"), null, "未检出", secondary));
        var legendNote = new TextBlock { Text = "虚线 = 无线链路 / 未实测段 · 悬停节点查看完整实测数据" };
        ApplyStyle(legendNote, "CaptionTextBlock");
        legendNote.Foreground = secondary;
        legend.Children.Add(legendNote);

        var topoStack = new StackPanel { Spacing = 6 };
        topoStack.Children.Add(topoHead);
        topoStack.Children.Add(topoScroller);
        topoStack.Children.Add(legend);
        var topoCard = MakeCard(new Thickness(16, 12, 16, 12), topoStack);
        topoCard.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.HasTopology)));
        topoCard.SetValue(Grid.RowProperty, 4);
        root.Children.Add(topoCard);

        // ---- 行 5：检查结果列表（ListView + 运行时模板虚拟化） ----
        var itemsList = MakeList(LoadTemplate(TemplateCheckStyled(), TemplateCheckPlain()));
        itemsList.IsItemClickEnabled = false;
        itemsList.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(NetworkDiagViewModel.Items)));
        itemsList.SetValue(Grid.RowProperty, 5);
        root.Children.Add(itemsList);

        // ---- 行 6：修复 + 导出双卡 ----
        var bottom = new Grid { ColumnSpacing = 12, Height = 236 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左卡：一键修复（标题行 + 修复动作列表 + 提示行）
        var repairTitle = new TextBlock { Text = "一键修复（傻瓜模式）" };
        ApplyStyle(repairTitle, "SectionTitleTextBlock");
        var cancelRepairBtn = new Button { Content = "取消修复" };
        cancelRepairBtn.Command = _vm.CancelRepairCommand;
        cancelRepairBtn.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(NetworkDiagViewModel.RepairCancelVis)));
        var fixBtn = new Button { Content = "修复所选" };
        fixBtn.Command = _vm.FixSelectedCommand;
        fixBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(NetworkDiagViewModel.CanRepair)));
        ApplyStyle(fixBtn, "AccentButtonStyle");
        var repairActionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        repairActionsRow.Children.Add(cancelRepairBtn);
        repairActionsRow.Children.Add(fixBtn);
        // 内置页：标题与右侧按钮行同格叠放
        var repairHead = new Grid();
        repairHead.Children.Add(repairTitle);
        repairHead.Children.Add(repairActionsRow);

        var repairList = MakeList(LoadTemplate(TemplateRepairStyled(), TemplateRepairPlain()));
        repairList.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(NetworkDiagViewModel.RepairActions)));
        repairList.SetValue(Grid.RowProperty, 1);
        var repairHint = new TextBlock();
        ApplyStyle(repairHint, "CaptionTextBlock");
        repairHint.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.RepairHint)));
        repairHint.SetValue(Grid.RowProperty, 2);

        var repairGrid = new Grid { RowSpacing = 6 };
        repairGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        repairGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        repairGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        repairGrid.Children.Add(repairHead);
        repairGrid.Children.Add(repairList);
        repairGrid.Children.Add(repairHint);
        var repairCard = MakeCard(new Thickness(14), repairGrid);
        repairCard.SetValue(Grid.ColumnProperty, 0);
        bottom.Children.Add(repairCard);

        // 右卡：导出 PDF 诊断报告（按钮行 + 结果 InfoBar + 诊断日志位置）
        var exportTitle = new TextBlock { Text = "导出 PDF 诊断报告" };
        ApplyStyle(exportTitle, "SectionTitleTextBlock");
        var exportDesc = new TextBlock
        {
            Text = "正规模板：含报告编号（按日累计）、设备编号与序列号、年级班级（生成时填写）、分层结果、修复记录与分级处置建议，适合上报与留档。",
            TextWrapping = TextWrapping.Wrap
        };
        ApplyStyle(exportDesc, "CaptionTextBlock");
        var exportBtn = new Button { Content = "导出 PDF 报告" };
        ApplyStyle(exportBtn, "AccentButtonStyle");
        exportBtn.Click += ExportPdf_Click;
        exportBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(NetworkDiagViewModel.HasRun)));
        var openFolderBtn = new Button { Content = "打开所在文件夹" };
        openFolderBtn.Click += OpenReportFolder_Click;
        openFolderBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(NetworkDiagViewModel.ReportReady)));
        var exportBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        exportBtnRow.Children.Add(exportBtn);
        exportBtnRow.Children.Add(openFolderBtn);

        _reportInfo = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Success, IsClosable = true };
        var logCaption = new TextBlock { Text = "诊断日志按日期与次数分文件留存：", Margin = new Thickness(0, 6, 0, 0) };
        ApplyStyle(logCaption, "CaptionTextBlock");
        var logLocation = new TextBlock { IsTextSelectionEnabled = true };
        ApplyStyle(logLocation, "MonoTextBlock");
        logLocation.SetBinding(TextBlock.TextProperty, OneWay(nameof(NetworkDiagViewModel.LogLocationText)));

        var exportStack = new StackPanel { Spacing = 8 };
        exportStack.Children.Add(exportTitle);
        exportStack.Children.Add(exportDesc);
        exportStack.Children.Add(exportBtnRow);
        exportStack.Children.Add(_reportInfo);
        exportStack.Children.Add(logCaption);
        exportStack.Children.Add(logLocation);
        var exportCard = MakeCard(new Thickness(14), exportStack);
        exportCard.SetValue(Grid.ColumnProperty, 1);
        bottom.Children.Add(exportCard);

        bottom.SetValue(Grid.RowProperty, 6);
        root.Children.Add(bottom);
        return root;
    }

    // ================= PDF 导出（询问式填写 年级/班级，内置页逐字对应） =================

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        var facts = await _probe.EnsureLoadedAsync();

        var panel = new StackPanel { Spacing = 10 };
        TextBox Field(string text, string header, string placeholder)
            => new() { Header = header, PlaceholderText = placeholder, Text = text };
        var tbSchool = Field(_settings.LastSchool, "学校名称", "如：市第一中学");
        var tbGrade = Field(_settings.LastGrade, "年级（必填）", "如：高二");
        var tbClass = Field(_settings.LastClassName, "班级（必填）", "如：3 班");
        var tbDevice = Field(_settings.LastDeviceTag, "设备编号（默认主机名）", facts.HostName);
        var tbReporter = Field(_settings.LastReporter, "报告人", "如：李老师");
        panel.Children.Add(tbSchool);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { tbGrade, tbClass } });
        panel.Children.Add(tbDevice);
        panel.Children.Add(tbReporter);
        var hint = new TextBlock
        {
            Text = "报告将自动写入设备序列号（SMBIOS）与 MachineGuid，并按当日次数编号，便于追溯。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            // 内置页此处用宿主内部主题解析服务；插件不依赖宿主内部类型，退化为
            // Application.Resources 当前变体 + 本地兜底色（视图构造期已解析的语义保持一致）
            Foreground = ResolveThemeBrush("TextSecondaryBrush", ActualTheme == ElementTheme.Dark)
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
            ShowReportInfo(InfoBarSeverity.Warning, "缺少必填信息", "年级与班级为必填项，请重新导出并填写。");
            return;
        }

        try
        {
            var path = await _vm.ExportReportAsync(
                tbSchool.Text.Trim(), tbGrade.Text.Trim(), tbClass.Text.Trim(),
                tbReporter.Text.Trim(), tbDevice.Text.Trim());
            ShowReportInfo(InfoBarSeverity.Success, "报告已生成", path);
        }
        catch (Exception ex)
        {
            ShowReportInfo(InfoBarSeverity.Error, "报告生成失败", ex.Message);
        }
    }

    private void ShowReportInfo(InfoBarSeverity severity, string title, string message)
    {
        if (_reportInfo == null) return; // 布局未装配时不可达（防御）
        _reportInfo.Severity = severity;
        _reportInfo.Title = title;
        _reportInfo.Message = message;
        _reportInfo.IsOpen = true;
    }

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_vm.LastReportPath) || !File.Exists(_vm.LastReportPath)) return;
        try
        {
            Process.Start("explorer.exe", $"/select,\"{_vm.LastReportPath}\"");
        }
        catch { }
    }

    // ================= 网络拓扑渲染（Canvas 手绘，无第三方依赖，内置 code-behind 逐行移植） =================

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkDiagViewModel.Topology) or nameof(NetworkDiagViewModel.HasTopology))
            RenderTopology();
    }

    private void RenderTopology()
    {
        if (_topoCanvas == null) return;
        _topoCanvas.Children.Clear();
        var topo = _vm.Topology;
        if (topo.Nodes.Count == 0) return;
        bool dark = ActualTheme == ElementTheme.Dark;
        _topoCanvas.Width = topo.CanvasW;
        _topoCanvas.Height = topo.CanvasH;
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
            _topoCanvas.Children.Add(line);

            if (string.IsNullOrEmpty(edge.Label)) continue;
            var label = MakeLabel(edge.Label, color, dark);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(0, (x1 + x2) / 2 - label.DesiredSize.Width / 2));
            Canvas.SetTop(label, Math.Max(0, (y1 + y2) / 2 - label.DesiredSize.Height / 2 - 13));
            _topoCanvas.Children.Add(label);
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
            var tip = $"{n.Badge} {n.Title}\n{n.Subtitle}\n{n.StatusText}";
            if (!string.IsNullOrEmpty(n.TooltipText)) tip += $"\n——\n{n.TooltipText}";
            ToolTipService.SetToolTip(border, tip);

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
            _topoCanvas.Children.Add(border);
        }
    }

    /// <summary>按页面实际主题解析刷子：宿主内部主题解析服务不进插件（仅契约接口依赖），
    /// 退化为 Application.Resources 当前变体优先 + 本地 FromHex 兜底（色值对齐 WinUI 深浅主题文本色）。</summary>
    private Microsoft.UI.Xaml.Media.Brush ResolveThemeBrush(string key, bool dark)
        => TryBrush(key) ?? new SolidColorBrush(key switch
        {
            "TextPrimaryBrush" => FromHex(dark ? "#FFFFFF" : "#1A1A1A"),
            _ => FromHex(dark ? "#C5C5C5" : "#5C5C5C")
        });

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

    // 拓扑画布颜色恒用本地兜底调色板（与内置 code-behind 同一组色值，深浅主题各一套）
    private static Windows.UI.Color HealthColor(TopoHealth h, bool dark) => h switch
    {
        TopoHealth.Ok => FromHex(dark ? "#5FBF77" : "#1F8A4C"),
        TopoHealth.Warn => FromHex(dark ? "#E3B341" : "#B97E14"),
        TopoHealth.Fail => FromHex(dark ? "#F26D6D" : "#C42B1C"),
        _ => FromHex(dark ? "#8E8E93" : "#7C7C80")
    };

    // 全项目约定：WinUI 3 投影不含 Windows.UI.Colors，颜色一律本地 FromHex 构造（同内置页/MainWindow）
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

    // ================= 运行时模板（XamlReader 经典 Binding，保 ListView 虚拟化） =================

    /// <summary>样式模板优先；宿主样式键缺失（必败）或解析失败时自动降级无资源模板并留日志。</summary>
    private DataTemplate? LoadTemplate(string styled, string plain)
    {
        if (_hasStyles)
        {
            var t = TryLoadTemplate(styled);
            if (t != null) return t;
        }
        _log("warn", "网络诊断视图：样式模板解析失败或宿主样式缺失，已降级为基础外观模板（功能不受影响）");
        return TryLoadTemplate(plain);
    }

    private static DataTemplate? TryLoadTemplate(string xaml)
    {
        try { return Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml) as DataTemplate; }
        catch { return null; }
    }

    // 模板一律普通 verbatim 串（{Binding} 单括号、"" 双引号转义），不与插值转义混写；
    // 可见性绑定全部走行模型 Visibility 类型 XxxVisState 属性（NetworkDiagModels 已齐备）。
    // 检查项行（内置页 DataTemplate 逐区对应：名称+分类徽章 / 时长+七态 Chip / 摘要 / 建议条 / 详情 Expander）
    private static string TemplateCheckStyled() => @"<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        Style=""{StaticResource CardStyle}"" Padding=""14,10,14,10"">
  <StackPanel Spacing=""6"">
    <Grid ColumnSpacing=""10"">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/>
      </Grid.ColumnDefinitions>
      <StackPanel Orientation=""Horizontal"" Spacing=""10"">
        <TextBlock Text=""{Binding Name}"" Style=""{StaticResource BodyTextBlock}"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
        <Border Background=""{ThemeResource InfoChipFillBrush}"" CornerRadius=""99"" Padding=""8,1"">
          <TextBlock Text=""{Binding CategoryText}"" Foreground=""{ThemeResource InfoTextBrush}"" FontSize=""11""/>
        </Border>
      </StackPanel>
      <StackPanel Grid.Column=""1"" Orientation=""Horizontal"" Spacing=""8"">
        <TextBlock Text=""{Binding DurationText, Mode=OneWay}"" FontSize=""11""
                   Foreground=""{ThemeResource PlaceholderTextBrush}"" VerticalAlignment=""Center""/>
        <Border Background=""{ThemeResource SuccessChipFillBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding PassVisState}"">
          <TextBlock Text=""通过"" Foreground=""{ThemeResource SuccessTextBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource WarningChipFillBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding WarnVisState}"">
          <TextBlock Text=""警告"" Foreground=""{ThemeResource WarningTextBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource ErrorChipFillBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding FailVisState}"">
          <TextBlock Text=""未通过"" Foreground=""{ThemeResource ErrorTextBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource InfoChipFillBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding FixedVisState}"">
          <TextBlock Text=""已修复"" Foreground=""{ThemeResource InfoTextBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource CardBorderBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding SkippedVisState}"">
          <TextBlock Text=""跳过"" Foreground=""{ThemeResource TextSecondaryBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource InfoChipFillBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding RunningVisState}"">
          <TextBlock Text=""检查中…"" Foreground=""{ThemeResource InfoTextBrush}"" FontSize=""11.5""/>
        </Border>
        <Border Background=""{ThemeResource CardBorderBrush}"" CornerRadius=""99"" Padding=""10,2""
                Visibility=""{Binding PendingVisState}"">
          <TextBlock Text=""待检查"" Foreground=""{ThemeResource TextSecondaryBrush}"" FontSize=""11.5""/>
        </Border>
      </StackPanel>
    </Grid>
    <TextBlock Text=""{Binding Summary, Mode=OneWay}"" Style=""{StaticResource CaptionTextBlock}""/>
    <Border Background=""{ThemeResource HoverFillBrush}"" CornerRadius=""6"" Padding=""10,6""
            Visibility=""{Binding SuggestionVisState}"">
      <StackPanel Orientation=""Horizontal"" Spacing=""8"">
        <FontIcon FontFamily=""Segoe MDL2 Assets"" Glyph=""&#xE946;"" FontSize=""13""
                  Foreground=""{ThemeResource InfoTextBrush}""/>
        <TextBlock Text=""{Binding Suggestion, Mode=OneWay}"" Style=""{StaticResource CaptionTextBlock}""
                   Foreground=""{ThemeResource TextPrimaryBrush}""/>
      </StackPanel>
    </Border>
    <Expander Visibility=""{Binding DetailsVisState}"" HorizontalAlignment=""Stretch"" HorizontalContentAlignment=""Left"">
      <Expander.Header>
        <TextBlock Text=""详细信息"" Style=""{StaticResource CaptionTextBlock}""/>
      </Expander.Header>
      <ItemsControl ItemsSource=""{Binding Details, Mode=OneWay}"">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text=""{Binding}"" Style=""{StaticResource MonoTextBlock}"" Margin=""0,2"" IsTextSelectionEnabled=""True""/>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </Expander>
  </StackPanel>
</Border>";

    private static string TemplateCheckPlain() => @"<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        Padding=""14,10,14,10"" CornerRadius=""12"" Background=""#14000000"" BorderBrush=""#33000000"" BorderThickness=""1"">
  <StackPanel Spacing=""6"">
    <Grid ColumnSpacing=""10"">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/>
      </Grid.ColumnDefinitions>
      <StackPanel Orientation=""Horizontal"" Spacing=""10"">
        <TextBlock Text=""{Binding Name}"" FontWeight=""SemiBold"" VerticalAlignment=""Center""/>
        <Border Background=""#1A0062CC"" CornerRadius=""99"" Padding=""8,1"">
          <TextBlock Text=""{Binding CategoryText}"" Foreground=""#FF0062CC"" FontSize=""11""/>
        </Border>
      </StackPanel>
      <StackPanel Grid.Column=""1"" Orientation=""Horizontal"" Spacing=""8"">
        <TextBlock Text=""{Binding DurationText, Mode=OneWay}"" FontSize=""11"" Foreground=""#FF9E9E9E"" VerticalAlignment=""Center""/>
        <Border Background=""#1A107C10"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding PassVisState}"">
          <TextBlock Text=""通过"" Foreground=""#FF107C10"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#1AFF9500"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding WarnVisState}"">
          <TextBlock Text=""警告"" Foreground=""#FFC93400"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#1AC42B1C"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding FailVisState}"">
          <TextBlock Text=""未通过"" Foreground=""#FFC42B1C"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#1A0062CC"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding FixedVisState}"">
          <TextBlock Text=""已修复"" Foreground=""#FF0062CC"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#33000000"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding SkippedVisState}"">
          <TextBlock Text=""跳过"" Foreground=""#FF5C5C5C"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#1A0062CC"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding RunningVisState}"">
          <TextBlock Text=""检查中…"" Foreground=""#FF0062CC"" FontSize=""11.5""/>
        </Border>
        <Border Background=""#33000000"" CornerRadius=""99"" Padding=""10,2"" Visibility=""{Binding PendingVisState}"">
          <TextBlock Text=""待检查"" Foreground=""#FF5C5C5C"" FontSize=""11.5""/>
        </Border>
      </StackPanel>
    </Grid>
    <TextBlock Text=""{Binding Summary, Mode=OneWay}"" FontSize=""12"" Opacity=""0.78""/>
    <Border Background=""#0F000000"" CornerRadius=""6"" Padding=""10,6"" Visibility=""{Binding SuggestionVisState}"">
      <StackPanel Orientation=""Horizontal"" Spacing=""8"">
        <FontIcon FontFamily=""Segoe MDL2 Assets"" Glyph=""&#xE946;"" FontSize=""13"" Foreground=""#FF0062CC""/>
        <TextBlock Text=""{Binding Suggestion, Mode=OneWay}"" FontSize=""12""/>
      </StackPanel>
    </Border>
    <Expander Visibility=""{Binding DetailsVisState}"" HorizontalAlignment=""Stretch"" HorizontalContentAlignment=""Left"">
      <Expander.Header>
        <TextBlock Text=""详细信息"" FontSize=""12""/>
      </Expander.Header>
      <ItemsControl ItemsSource=""{Binding Details, Mode=OneWay}"">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text=""{Binding}"" FontFamily=""Consolas"" FontSize=""11.5"" Margin=""0,2"" IsTextSelectionEnabled=""True""/>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </Expander>
  </StackPanel>
</Border>";

    // 修复动作行（内置页 DataTemplate 逐区对应：勾选 / 标题+风险徽章 / 详情 / 权限+结果徽章+执行环）
    private static string TemplateRepairStyled() => @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        ColumnSpacing=""8"" Padding=""4,2"">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/>
  </Grid.ColumnDefinitions>
  <CheckBox IsChecked=""{Binding IsSelected, Mode=TwoWay}"" VerticalAlignment=""Center""/>
  <StackPanel Grid.Column=""1"" Spacing=""1"">
    <StackPanel Orientation=""Horizontal"" Spacing=""8"">
      <TextBlock Text=""{Binding Title}"" Style=""{StaticResource CaptionTextBlock}""
                 Foreground=""{ThemeResource TextPrimaryBrush}""/>
      <Border Background=""{ThemeResource WarningChipFillBrush}"" CornerRadius=""4"" Padding=""5,0""
              Visibility=""{Binding RiskyVisState}"">
        <TextBlock Text=""需重启/谨慎"" Foreground=""{ThemeResource WarningTextBrush}"" FontSize=""10""/>
      </Border>
    </StackPanel>
    <TextBlock Text=""{Binding Detail}"" Style=""{StaticResource CaptionTextBlock}"" TextTrimming=""CharacterEllipsis""/>
  </StackPanel>
  <StackPanel Grid.Column=""2"" Orientation=""Horizontal"" Spacing=""6"" VerticalAlignment=""Center"">
    <TextBlock Text=""{Binding AdminText}"" FontSize=""10""
               Foreground=""{ThemeResource PlaceholderTextBrush}"" VerticalAlignment=""Center""/>
    <Border Background=""{ThemeResource SuccessChipFillBrush}"" CornerRadius=""4"" Padding=""6,1""
            Visibility=""{Binding OkVisState}"">
      <TextBlock Text=""成功"" Foreground=""{ThemeResource SuccessTextBrush}"" FontSize=""10.5""/>
    </Border>
    <Border Background=""{ThemeResource ErrorChipFillBrush}"" CornerRadius=""4"" Padding=""6,1""
            Visibility=""{Binding FailVisState}"">
      <TextBlock Text=""失败"" Foreground=""{ThemeResource ErrorTextBrush}"" FontSize=""10.5""/>
    </Border>
    <ProgressRing Width=""16"" Height=""16"" IsActive=""{Binding RunningVis, Mode=OneWay}""/>
  </StackPanel>
</Grid>";

    private static string TemplateRepairPlain() => @"<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        ColumnSpacing=""8"" Padding=""4,2"">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/>
  </Grid.ColumnDefinitions>
  <CheckBox IsChecked=""{Binding IsSelected, Mode=TwoWay}"" VerticalAlignment=""Center""/>
  <StackPanel Grid.Column=""1"" Spacing=""1"">
    <StackPanel Orientation=""Horizontal"" Spacing=""8"">
      <TextBlock Text=""{Binding Title}"" FontSize=""12"" FontWeight=""SemiBold""/>
      <Border Background=""#1AFF9500"" CornerRadius=""4"" Padding=""5,0"" Visibility=""{Binding RiskyVisState}"">
        <TextBlock Text=""需重启/谨慎"" Foreground=""#FFC93400"" FontSize=""10""/>
      </Border>
    </StackPanel>
    <TextBlock Text=""{Binding Detail}"" FontSize=""12"" Opacity=""0.78"" TextTrimming=""CharacterEllipsis""/>
  </StackPanel>
  <StackPanel Grid.Column=""2"" Orientation=""Horizontal"" Spacing=""6"" VerticalAlignment=""Center"">
    <TextBlock Text=""{Binding AdminText}"" FontSize=""10"" Foreground=""#FF9E9E9E"" VerticalAlignment=""Center""/>
    <Border Background=""#1A107C10"" CornerRadius=""4"" Padding=""6,1"" Visibility=""{Binding OkVisState}"">
      <TextBlock Text=""成功"" Foreground=""#FF107C10"" FontSize=""10.5""/>
    </Border>
    <Border Background=""#1AC42B1C"" CornerRadius=""4"" Padding=""6,1"" Visibility=""{Binding FailVisState}"">
      <TextBlock Text=""失败"" Foreground=""#FFC42B1C"" FontSize=""10.5""/>
    </Border>
    <ProgressRing Width=""16"" Height=""16"" IsActive=""{Binding RunningVis, Mode=OneWay}""/>
  </StackPanel>
</Grid>";

    // 低速嫌疑行（内置页 DataTemplate 逐区对应：嫌疑档徽章三选一 / 标题 / 依据 / 处置）。
    // 内置页 {x:Bind IsHigh/IsMid/IsLow} 是 bool 经 x:Bind 隐式转 Visibility；经典 Binding 无此转换，
    // 行模型已备 Visibility 类型 HighVisState/MidVisState/LowVisState，直接绑定免转换器。
    private static string TemplateCauseStyled() => @"<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        Background=""{ThemeResource HoverFillBrush}"" CornerRadius=""6"" Padding=""10,6"">
  <StackPanel Spacing=""2"">
    <StackPanel Orientation=""Horizontal"" Spacing=""8"">
      <Border Background=""{ThemeResource ErrorChipFillBrush}"" CornerRadius=""99"" Padding=""8,1""
              Visibility=""{Binding HighVisState}"">
        <TextBlock Text=""高嫌疑"" Foreground=""{ThemeResource ErrorTextBrush}"" FontSize=""10.5""/>
      </Border>
      <Border Background=""{ThemeResource WarningChipFillBrush}"" CornerRadius=""99"" Padding=""8,1""
              Visibility=""{Binding MidVisState}"">
        <TextBlock Text=""可能"" Foreground=""{ThemeResource WarningTextBrush}"" FontSize=""10.5""/>
      </Border>
      <Border Background=""{ThemeResource InfoChipFillBrush}"" CornerRadius=""99"" Padding=""8,1""
              Visibility=""{Binding LowVisState}"">
        <TextBlock Text=""轻微"" Foreground=""{ThemeResource InfoTextBrush}"" FontSize=""10.5""/>
      </Border>
      <TextBlock Text=""{Binding Title}"" Style=""{StaticResource CaptionTextBlock}"" FontWeight=""SemiBold""
                 Foreground=""{ThemeResource TextPrimaryBrush}""/>
    </StackPanel>
    <TextBlock Text=""{Binding EvidenceText}"" Style=""{StaticResource CaptionTextBlock}""
               TextTrimming=""CharacterEllipsis"" Foreground=""{ThemeResource TextSecondaryBrush}""/>
    <TextBlock Text=""{Binding ActionText}"" Style=""{StaticResource CaptionTextBlock}"" TextTrimming=""CharacterEllipsis""/>
  </StackPanel>
</Border>";

    private static string TemplateCausePlain() => @"<Border xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        Background=""#0F000000"" CornerRadius=""6"" Padding=""10,6"">
  <StackPanel Spacing=""2"">
    <StackPanel Orientation=""Horizontal"" Spacing=""8"">
      <Border Background=""#1AC42B1C"" CornerRadius=""99"" Padding=""8,1"" Visibility=""{Binding HighVisState}"">
        <TextBlock Text=""高嫌疑"" Foreground=""#FFC42B1C"" FontSize=""10.5""/>
      </Border>
      <Border Background=""#1AFF9500"" CornerRadius=""99"" Padding=""8,1"" Visibility=""{Binding MidVisState}"">
        <TextBlock Text=""可能"" Foreground=""#FFC93400"" FontSize=""10.5""/>
      </Border>
      <Border Background=""#1A0062CC"" CornerRadius=""99"" Padding=""8,1"" Visibility=""{Binding LowVisState}"">
        <TextBlock Text=""轻微"" Foreground=""#FF0062CC"" FontSize=""10.5""/>
      </Border>
      <TextBlock Text=""{Binding Title}"" FontSize=""12"" FontWeight=""SemiBold""/>
    </StackPanel>
    <TextBlock Text=""{Binding EvidenceText}"" FontSize=""12"" Opacity=""0.72"" TextTrimming=""CharacterEllipsis""/>
    <TextBlock Text=""{Binding ActionText}"" FontSize=""12"" Opacity=""0.78"" TextTrimming=""CharacterEllipsis""/>
  </StackPanel>
</Border>";

    // ================= 列表与控件构造 =================

    /// <summary>虚拟化列表（模板经 XamlReader 运行时解析；容器样式 C# 构造，行全宽贴边）。</summary>
    private static ListView MakeList(DataTemplate? template)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, ItemContainerStyle = MakeListItemStyle() };
        if (template != null) list.ItemTemplate = template;
        return list;
    }

    private static Style MakeListItemStyle()
    {
        var s = new Style(typeof(ListViewItem));
        s.Setters.Add(new Setter(FrameworkElement.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 2, 0, 2)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        return s;
    }

    private Border MakeCard(Thickness padding, UIElement content)
    {
        var card = new Border { Padding = padding };
        var style = TryStyle("CardStyle");
        if (style != null)
        {
            card.Style = style;
        }
        else
        {
            card.CornerRadius = new CornerRadius(12);
            card.Background = TryBrush("CardBackgroundBrush") ?? new SolidColorBrush(ColorsTransparent);
            card.BorderBrush = TryBrush("DividerStrokeBrush") ?? new SolidColorBrush(ColorsTransparent);
            card.BorderThickness = new Thickness(1);
        }
        card.Child = content;
        return card;
    }

    /// <summary>统计芯片（胶囊底 + 绑定文本），通过/警告/未通过/已修复四枚共用。</summary>
    private static Border CountChip(Brush bg, Brush fg, string textPath)
    {
        var t = new TextBlock { Foreground = fg, FontSize = 12 };
        t.SetBinding(TextBlock.TextProperty, OneWay(textPath));
        return new Border { Background = bg, CornerRadius = new CornerRadius(99), Padding = new Thickness(10, 2, 10, 2), Child = t };
    }

    /// <summary>图例小项（圆点 + 说明文字）；stroke 传 null 表示无描边（「未检出」）。</summary>
    private static StackPanel LegendItem(Brush fill, Brush? stroke, string label, Brush secondary)
    {
        var dot = new Ellipse { Width = 9, Height = 9, Fill = fill };
        if (stroke != null)
        {
            dot.Stroke = stroke;
            dot.StrokeThickness = 1;
        }
        var t = new TextBlock { Text = label, Foreground = secondary };
        ApplyStyle(t, "CaptionTextBlock");
        t.Foreground = secondary; // 样式 Setter 可能携带前景，局部值后置覆盖保证兜底语义
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { dot, t } };
    }

    // ================= 样式与色刷（usb 模式：宿主 Application.Resources 当前主题变体 + 本地兜底） =================

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

    /// <summary>宿主主题刷缺失时按主题取本地兜底色（芯片底/文本色，色值对齐 WinUI 默认主题变体）。</summary>
    private static Brush FillBrush(string key, string fallbackHex)
        => TryBrush(key) ?? new SolidColorBrush(FromHex(fallbackHex));

    private static Brush SecondaryBrush(bool dark)
        => FillBrush("TextSecondaryBrush", dark ? "#C5C5C5" : "#5C5C5C");

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

    private sealed class BoolToVisConverter : IValueConverter
    {
        public static readonly BoolToVisConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
