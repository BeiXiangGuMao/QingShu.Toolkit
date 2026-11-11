using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace QingShu.Plugin.DiskCleanup;

/// <summary>
/// 磁盘清理导航页（纯代码构建，零 XAML 编译依赖——XAML 页面在插件 ALC 内的资源加载是硬约束，
/// 与 gala / 弹窗拦截 / U盘工具同一模式）。布局与功能同内置 DiskCleanupPage.xaml 逐区对应：
/// 页头 → Pivot 四标签（常规清理 / 微信文件 / QQ 文件 / 大文件）→ 底部状态卡（删除模式开关 + 结果 + 安全边界）。
/// 列表保留 ListView + DataTemplate 虚拟化（微信/QQ 久远文件可达数千行，手工行重建会因无虚拟化卡死 UI）：
/// 模板经 XamlReader.Load 运行时解析，走经典 Binding（x:Bind 是编译期生成器行为，运行时模板不可用）；
/// bool→Visibility 无隐式转换（x:Bind 的隐式转换是生成器行为），行 VM 直接暴露 Visibility 类型属性
/// （AdminVisState / HeaderVisState / FileVisState，跨 ALC 类型同一）供模板绑定；视图层绑定沿用
/// usb 模式（本地 IValueConverter）。样式资源引用（CardStyle 等）在宿主 Application.Resources 取不到时
/// 自动降级为无资源模板（默认前景 + 半透明兜底色），两级模板构造期选定，不做运行期重试。
/// 破坏性口径与内置页逐字对应：删除所选不弹对话框（“询问式”勾选即确认，阈值/分组头/状态提示齐全）。
/// 页面每次导航重建：Loaded 不做自动扫描（内置页同样手动触发），Unloaded 取消全部在途操作。
/// </summary>
public sealed class DiskCleanupView : UserControl
{
    private const string Xns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    private readonly CleanupViewModel _vm;
    private readonly Action<string, string> _log; // (level, message) → context.Log（模板降级等视图级日志）
    private readonly bool _hasStyles;             // 宿主样式可用 → 模板用样式资源；否则无资源降级模板

    public DiskCleanupView(CleanupViewModel vm, Action<string, string> log)
    {
        _vm = vm;
        _log = log;
        DataContext = _vm;
        _hasStyles = TryStyle("CardStyle") != null && TryStyle("CaptionTextBlock") != null && TryBrush("TextPrimaryBrush") != null;

        Unloaded += (_, _) => _vm.Dispose(); // 离开页面收尾：取消全部在途扫描/删除（与内置页 Unloaded 同语义）

        Content = BuildLayout();
    }

    // ================= 布局 =================

    private UIElement BuildLayout()
    {
        var root = new Grid { RowSpacing = 12, Padding = new Thickness(28, 20, 28, 12) };
        var transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 12, IsStaggeringEnabled = true }
        };
        root.Transitions = transitions;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 页头 ----
        var header = new StackPanel { Spacing = 0 };
        var title = new TextBlock { Text = "磁盘清理（插件）" };
        ApplyStyle(title, "PageTitleTextBlock");
        var subtitle = new TextBlock
        {
            Text = "常规安全清理 · 微信/QQ 久远文件询问式清理 · 全盘大文件兜底扫描。默认删除进回收站，可在底部随时切换。",
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
        ApplyStyle(subtitle, "PageSubtitleTextBlock");
        header.Children.Add(title);
        header.Children.Add(subtitle);
        header.SetValue(Grid.RowProperty, 0);

        // ---- Pivot 四标签 ----
        var pivot = new Pivot();
        pivot.Items.Add(MakePivotItem("常规清理", BuildRegularTab()));
        pivot.Items.Add(MakePivotItem("微信文件", BuildWeChatTab()));
        pivot.Items.Add(MakePivotItem("QQ文件", BuildQqTab()));
        pivot.Items.Add(MakePivotItem("大文件", BuildLargeTab()));
        pivot.SetValue(Grid.RowProperty, 1);

        // ---- 底部状态卡 ----
        var toggle = new ToggleSwitch
        {
            OnContent = "删除进回收站",
            OffContent = "直接删除",
            Margin = new Thickness(-8, 0, 0, 0)
        };
        toggle.SetBinding(ToggleSwitch.IsOnProperty, TwoWay(nameof(CleanupViewModel.DeleteToRecycleBin)));
        var lastResult = Caption(Secondary());
        lastResult.VerticalAlignment = VerticalAlignment.Center;
        lastResult.SetBinding(TextBlock.TextProperty, OneWay(nameof(CleanupViewModel.LastResult)));
        var leftCluster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        leftCluster.Children.Add(toggle);
        leftCluster.Children.Add(lastResult);
        var safetyNote = new TextBlock
        {
            Text = "安全边界：不修改系统服务 / 不动虚拟内存 / 不导入注册表 / 不关闭 Defender",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        ApplyStyle(safetyNote, "CaptionTextBlock");
        safetyNote.Foreground = Secondary();
        var bottomGrid = new Grid { ColumnSpacing = 14 };
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftCluster.SetValue(Grid.ColumnProperty, 0);
        safetyNote.SetValue(Grid.ColumnProperty, 1);
        bottomGrid.Children.Add(leftCluster);
        bottomGrid.Children.Add(safetyNote);
        var bottomCard = MakeCard(14, 8);
        ((StackPanel)bottomCard.Child).Children.Add(bottomGrid);
        bottomCard.SetValue(Grid.RowProperty, 2);

        root.Children.Add(header);
        root.Children.Add(pivot);
        root.Children.Add(bottomCard);
        return root;
    }

    private static PivotItem MakePivotItem(string header, UIElement content)
        => new() { Header = header, Content = content };

    // ---- 标签页一：常规清理 ----

    private UIElement BuildRegularTab()
    {
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var scanBtn = AccentTextButton(Symbol.Sync, "开始扫描", _vm.ScanRegularCommand);
        scanBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(CleanupViewModel.RegularCanScan)));
        var selectAll = PlainButton("全选", _vm.SelectAllRegularCommand);
        var selectNone = PlainButton("全不选", _vm.SelectNoneRegularCommand);
        var cleanBtn = AccentTextButton(Symbol.Delete, "开始清理", _vm.CleanRegularCommand);
        cleanBtn.SetBinding(Control.IsEnabledProperty, OneWay(nameof(CleanupViewModel.RegularReady)));

        var bar = new ProgressBar { Width = 140, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(2) };
        bar.SetBinding(ProgressBar.IsIndeterminateProperty, OneWay(nameof(CleanupViewModel.IsRegularBusy)));
        bar.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.RegularBusyVis)));

        // 清理阶段确定性进度条（目标级推进；扫描阶段隐藏走不定态条）
        var cleanBar = new ProgressBar { Width = 140, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(2) };
        cleanBar.SetBinding(ProgressBar.ValueProperty, OneWay(nameof(CleanupViewModel.RegularCleanProgress)));
        cleanBar.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.RegularCleanBarVis)));

        var cancelBtn = PlainButton("取消扫描", _vm.CancelRegularScanCommand);
        cancelBtn.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.RegularBusyVis)));

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        toolbar.Children.Add(scanBtn);
        toolbar.Children.Add(selectAll);
        toolbar.Children.Add(selectNone);
        toolbar.Children.Add(cleanBtn);
        toolbar.Children.Add(bar);
        toolbar.Children.Add(cleanBar);
        toolbar.Children.Add(cancelBtn);
        toolbar.SetValue(Grid.RowProperty, 0);

        var list = MakeList(LoadTemplate(TemplateRegularStyled(), TemplateRegularPlain()));
        list.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(CleanupViewModel.RegularTargets)));
        list.SetValue(Grid.RowProperty, 1);

        grid.Children.Add(toolbar);
        grid.Children.Add(list);
        return grid;
    }

    // ---- 标签页二/三：微信 / QQ（结构一致，命令与文案不同） ----

    private UIElement BuildWeChatTab()
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            IsOpen = true,
            IsClosable = false,
            Title = "清理前请阅读",
            Message = "本工具绝不删除聊天记录数据库；但删除久远媒体文件后，对应历史聊天中的图片/视频将显示“图片已过期”。请确认后再勾选删除。"
        };
        return BuildImTab(info, isWeChat: true);
    }

    private UIElement BuildQqTab()
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false,
            Message = "扫描 Tencent Files（含 FileRecv 接收文件、图片、视频、表情缓存与新版 nt_qq 数据），仅列出超过阈值天数的文件。数据库文件受保护不会被列出或删除。"
        };
        return BuildImTab(info, isWeChat: false);
    }

    private UIElement BuildImTab(InfoBar info, bool isWeChat)
    {
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        info.SetValue(Grid.RowProperty, 0);

        var daysBox = new TextBox { Header = "久远阈值（天）", Width = 150 };
        daysBox.SetBinding(TextBox.TextProperty, TwoWay(nameof(CleanupViewModel.ImOldDaysText)));

        Button scanBtn, cancelScanBtn;
        if (isWeChat)
        {
            scanBtn = AccentTextButton(Symbol.Sync, "扫描微信", _vm.ScanWeChatCommand);
            cancelScanBtn = PlainButton("取消", _vm.CancelWeChatScanCommand);
            cancelScanBtn.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.WeChatBusyVis)));
        }
        else
        {
            scanBtn = AccentTextButton(Symbol.Sync, "扫描 QQ", _vm.ScanQqCommand);
            cancelScanBtn = PlainButton("取消", _vm.CancelQqScanCommand);
            cancelScanBtn.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.QqBusyVis)));
        }
        scanBtn.VerticalAlignment = VerticalAlignment.Bottom;
        cancelScanBtn.VerticalAlignment = VerticalAlignment.Bottom;

        var selectAll = PlainButton("全选", _vm.SelectAllImCommand);
        selectAll.VerticalAlignment = VerticalAlignment.Bottom;
        var selectNone = PlainButton("全不选", _vm.SelectNoneImCommand);
        selectNone.VerticalAlignment = VerticalAlignment.Bottom;
        var deleteBtn = PlainButton("删除所选", _vm.DeleteImCommand);
        deleteBtn.VerticalAlignment = VerticalAlignment.Bottom;
        var cancelDelete = PlainButton("取消删除", _vm.CancelImDeleteCommand);
        cancelDelete.VerticalAlignment = VerticalAlignment.Bottom;
        cancelDelete.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.ImDeleteCancelVis)));

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        toolbar.Children.Add(daysBox);
        toolbar.Children.Add(scanBtn);
        toolbar.Children.Add(cancelScanBtn);
        toolbar.Children.Add(selectAll);
        toolbar.Children.Add(selectNone);
        toolbar.Children.Add(deleteBtn);
        toolbar.Children.Add(cancelDelete);
        toolbar.SetValue(Grid.RowProperty, 1);

        // 列表 + 状态行
        var inner = new Grid { RowSpacing = 6 };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = MakeList(LoadTemplate(TemplateImStyled(), TemplateImPlain()));
        list.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(isWeChat ? nameof(CleanupViewModel.WeChatFiles) : nameof(CleanupViewModel.QqFiles)));
        inner.Children.Add(list);

        var selectedText = Caption(Info());
        selectedText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        selectedText.SetBinding(TextBlock.TextProperty, OneWay(nameof(CleanupViewModel.ImSelectedText)));
        var statusText = Caption(Secondary());
        statusText.SetBinding(TextBlock.TextProperty, OneWay(isWeChat ? nameof(CleanupViewModel.WeChatStatus) : nameof(CleanupViewModel.QqStatus)));
        var ring = new ProgressRing { Width = 16, Height = 16 };
        ring.SetBinding(ProgressRing.IsActiveProperty, OneWay(isWeChat ? nameof(CleanupViewModel.IsWeChatBusy) : nameof(CleanupViewModel.IsQqBusy)));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        statusRow.Children.Add(selectedText);
        statusRow.Children.Add(statusText);
        statusRow.Children.Add(ring);
        statusRow.SetValue(Grid.RowProperty, 1);
        inner.Children.Add(statusRow);

        // 删除确定性进度条（逐文件推进，删除中且已报出进度时显示）
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var imDeleteBar = new ProgressBar { CornerRadius = new CornerRadius(2), Height = 6 };
        imDeleteBar.SetBinding(ProgressBar.ValueProperty, OneWay(nameof(CleanupViewModel.ImDeleteProgress)));
        imDeleteBar.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.ImDeleteProgressVis)));
        imDeleteBar.SetValue(Grid.RowProperty, 2);
        inner.Children.Add(imDeleteBar);

        inner.SetValue(Grid.RowProperty, 2);
        grid.Children.Add(info);
        grid.Children.Add(toolbar);
        grid.Children.Add(inner);
        return grid;
    }

    // ---- 标签页四：大文件 ----

    private UIElement BuildLargeTab()
    {
        var grid = new Grid { RowSpacing = 10 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var driveCombo = new ComboBox { Header = "扫描分区", MinWidth = 120 };
        driveCombo.SetBinding(ComboBox.SelectedIndexProperty, TwoWay(nameof(CleanupViewModel.DriveIndex)));
        driveCombo.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(CleanupViewModel.Drives)));

        var minMbBox = new TextBox { Header = "大小阈值（MB）", Width = 150 };
        minMbBox.SetBinding(TextBox.TextProperty, TwoWay(nameof(CleanupViewModel.LargeMinMbText)));

        var scanBtn = AccentTextButton(Symbol.Sync, "开始扫描", _vm.ScanLargeCommand);
        scanBtn.VerticalAlignment = VerticalAlignment.Bottom;
        var cancelScanBtn = PlainButton("取消", _vm.CancelLargeScanCommand);
        cancelScanBtn.VerticalAlignment = VerticalAlignment.Bottom;
        cancelScanBtn.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.LargeBusyVis)));
        var openBtn = new Button { Content = "打开位置", VerticalAlignment = VerticalAlignment.Bottom };
        openBtn.Click += OpenLargeFolder_Click;
        var deleteBtn = PlainButton("删除所选", _vm.DeleteLargeCommand);
        deleteBtn.VerticalAlignment = VerticalAlignment.Bottom;
        var cancelDelete = PlainButton("取消删除", _vm.CancelLargeDeleteCommand);
        cancelDelete.VerticalAlignment = VerticalAlignment.Bottom;
        cancelDelete.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.LargeDeleteCancelVis)));

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        toolbar.Children.Add(driveCombo);
        toolbar.Children.Add(minMbBox);
        toolbar.Children.Add(scanBtn);
        toolbar.Children.Add(cancelScanBtn);
        toolbar.Children.Add(openBtn);
        toolbar.Children.Add(deleteBtn);
        toolbar.Children.Add(cancelDelete);
        toolbar.SetValue(Grid.RowProperty, 0);

        var inner = new Grid { RowSpacing = 6 };
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var list = MakeList(LoadTemplate(TemplateLargeStyled(), TemplateLargePlain()));
        list.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(nameof(CleanupViewModel.LargeFiles)));
        inner.Children.Add(list);

        var selectedText = Caption(Info());
        selectedText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        selectedText.SetBinding(TextBlock.TextProperty, OneWay(nameof(CleanupViewModel.LargeSelectedText)));
        var statusText = Caption(Secondary());
        statusText.SetBinding(TextBlock.TextProperty, OneWay(nameof(CleanupViewModel.LargeStatus)));
        var ring = new ProgressRing { Width = 16, Height = 16 };
        ring.SetBinding(ProgressRing.IsActiveProperty, OneWay(nameof(CleanupViewModel.IsLargeBusy)));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        statusRow.Children.Add(selectedText);
        statusRow.Children.Add(statusText);
        statusRow.Children.Add(ring);
        statusRow.SetValue(Grid.RowProperty, 1);
        inner.Children.Add(statusRow);

        // 删除确定性进度条（逐文件推进，删除中且已报出进度时显示）
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var largeDeleteBar = new ProgressBar { CornerRadius = new CornerRadius(2), Height = 6 };
        largeDeleteBar.SetBinding(ProgressBar.ValueProperty, OneWay(nameof(CleanupViewModel.LargeDeleteProgress)));
        largeDeleteBar.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(CleanupViewModel.LargeDeleteProgressVis)));
        largeDeleteBar.SetValue(Grid.RowProperty, 2);
        inner.Children.Add(largeDeleteBar);

        inner.SetValue(Grid.RowProperty, 1);
        grid.Children.Add(toolbar);
        grid.Children.Add(inner);
        return grid;
    }

    private void OpenLargeFolder_Click(object sender, RoutedEventArgs e)
    {
        var sel = _vm.LargeFiles.FirstOrDefault(x => x.IsSelected) ?? _vm.LargeFiles.FirstOrDefault();
        if (sel == null) return;
        try
        {
            var target = File.Exists(sel.Item.Path) ? sel.Item.Path : sel.Item.Dir;
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch { }
    }

    // ================= 运行时模板（XamlReader 经典 Binding，保 ListView 虚拟化） =================

    /// <summary>样式模板优先；解析失败（宿主缺样式键等）自动降级无资源模板并留日志。</summary>
    private DataTemplate? LoadTemplate(string styled, string plain)
    {
        var t = TryLoadTemplate(styled);
        if (t != null) return t;
        _log("warn", "磁盘清理视图：样式模板解析失败，已降级为基础外观模板（功能不受影响）");
        return TryLoadTemplate(plain);
    }

    private static DataTemplate? TryLoadTemplate(string xaml)
    {
        try { return Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml) as DataTemplate; }
        catch { return null; }
    }

    // 常规清理目标行（内置页 DataTemplate 逐区对应：勾选/名称+风险徽章/需管理员徽章/描述/大小/文件数/扫描环）
    private static string TemplateRegularStyled() => $@"<Border {Xns}
        Style=""{{StaticResource CardStyle}}"" Padding=""12,8"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
    <StackPanel Grid.Column=""1"" Spacing=""2"">
      <StackPanel Orientation=""Horizontal"" Spacing=""8"">
        <TextBlock Text=""{{Binding Name}}"" Style=""{{StaticResource BodyTextBlock}}"" FontWeight=""SemiBold""/>
        <Border Background=""{{ThemeResource InfoChipFillBrush}}"" CornerRadius=""4"" Padding=""5,0"">
          <TextBlock Text=""{{Binding RiskText}}"" Foreground=""{{ThemeResource InfoTextBrush}}"" FontSize=""10.5""/>
        </Border>
        <Border Background=""{{ThemeResource WarningChipFillBrush}}"" CornerRadius=""4"" Padding=""5,0"" Visibility=""{{Binding AdminVisState}}"">
          <TextBlock Text=""需管理员"" Foreground=""{{ThemeResource WarningTextBrush}}"" FontSize=""10.5""/>
        </Border>
      </StackPanel>
      <TextBlock Text=""{{Binding Description}}"" Style=""{{StaticResource CaptionTextBlock}}""/>
    </StackPanel>
    <StackPanel Grid.Column=""2"" VerticalAlignment=""Center"" HorizontalAlignment=""Right"">
      <TextBlock Text=""{{Binding SizeText, Mode=OneWay}}"" Style=""{{StaticResource BodyTextBlock}}"" HorizontalAlignment=""Right"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding FileText, Mode=OneWay}}"" Style=""{{StaticResource CaptionTextBlock}}"" HorizontalAlignment=""Right""/>
    </StackPanel>
    <ProgressRing Grid.Column=""3"" Width=""18"" Height=""18"" VerticalAlignment=""Center"" IsActive=""{{Binding IsScanning, Mode=OneWay}}""/>
  </Grid>
</Border>";

    private static string TemplateRegularPlain() => $@"<Border {Xns} Padding=""12,8"" CornerRadius=""12""
        Background=""#14000000"" BorderBrush=""#33000000"" BorderThickness=""1"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
    <StackPanel Grid.Column=""1"" Spacing=""2"">
      <StackPanel Orientation=""Horizontal"" Spacing=""8"">
        <TextBlock Text=""{{Binding Name}}"" FontWeight=""SemiBold""/>
        <Border Background=""#1A0062CC"" CornerRadius=""4"" Padding=""5,0"">
          <TextBlock Text=""{{Binding RiskText}}"" Foreground=""#FF0062CC"" FontSize=""10.5""/>
        </Border>
        <Border Background=""#1AFF9500"" CornerRadius=""4"" Padding=""5,0"" Visibility=""{{Binding AdminVisState}}"">
          <TextBlock Text=""需管理员"" Foreground=""#FFC93400"" FontSize=""10.5""/>
        </Border>
      </StackPanel>
      <TextBlock Text=""{{Binding Description}}"" FontSize=""12"" Opacity=""0.72"" TextWrapping=""Wrap""/>
    </StackPanel>
    <StackPanel Grid.Column=""2"" VerticalAlignment=""Center"" HorizontalAlignment=""Right"">
      <TextBlock Text=""{{Binding SizeText, Mode=OneWay}}"" HorizontalAlignment=""Right"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding FileText, Mode=OneWay}}"" HorizontalAlignment=""Right"" FontSize=""12"" Opacity=""0.72""/>
    </StackPanel>
    <ProgressRing Grid.Column=""3"" Width=""18"" Height=""18"" VerticalAlignment=""Center"" IsActive=""{{Binding IsScanning, Mode=OneWay}}""/>
  </Grid>
</Border>";

    // 微信/QQ 文件行（账号分组头 + 文件行两态互斥可见，内置页 DataTemplate 逐区对应）
    private static string TemplateImStyled() => $@"<Grid {Xns} Padding=""0,1"">
  <Border Background=""{{ThemeResource InfoChipFillBrush}}"" CornerRadius=""6"" Padding=""12,5"" Visibility=""{{Binding HeaderVisState}}"">
    <TextBlock Text=""{{Binding HeaderText}}"" FontWeight=""SemiBold"" FontSize=""12"" Foreground=""{{ThemeResource InfoTextBrush}}""/>
  </Border>
  <Border Style=""{{StaticResource CardStyle}}"" Padding=""10,6"" Visibility=""{{Binding FileVisState}}"">
    <Grid ColumnSpacing=""10"">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
      </Grid.ColumnDefinitions>
      <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
      <StackPanel Grid.Column=""1"" Spacing=""1"">
        <StackPanel Orientation=""Horizontal"" Spacing=""8"">
          <TextBlock Text=""{{Binding Name}}"" Style=""{{StaticResource CaptionTextBlock}}"" Foreground=""{{ThemeResource TextPrimaryBrush}}"" FontWeight=""SemiBold""/>
          <Border Background=""{{ThemeResource InfoChipFillBrush}}"" CornerRadius=""4"" Padding=""5,0"">
            <TextBlock Text=""{{Binding KindText}}"" Foreground=""{{ThemeResource InfoTextBrush}}"" FontSize=""10""/>
          </Border>
          <TextBlock Text=""{{Binding AccountText}}"" FontSize=""10.5"" Foreground=""{{ThemeResource PlaceholderTextBrush}}"" VerticalAlignment=""Center""/>
        </StackPanel>
        <TextBlock Text=""{{Binding PathText}}"" Style=""{{StaticResource MonoTextBlock}}"" TextTrimming=""CharacterEllipsis""/>
      </StackPanel>
      <TextBlock Grid.Column=""2"" Text=""{{Binding TimeText}}"" Style=""{{StaticResource CaptionTextBlock}}"" VerticalAlignment=""Center"" HorizontalAlignment=""Right""/>
      <TextBlock Grid.Column=""3"" Text=""{{Binding SizeText}}"" Style=""{{StaticResource BodyTextBlock}}"" FontWeight=""SemiBold"" VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
    </Grid>
  </Border>
</Grid>";

    private static string TemplateImPlain() => $@"<Grid {Xns} Padding=""0,1"">
  <Border Background=""#1A0062CC"" CornerRadius=""6"" Padding=""12,5"" Visibility=""{{Binding HeaderVisState}}"">
    <TextBlock Text=""{{Binding HeaderText}}"" FontWeight=""SemiBold"" FontSize=""12"" Foreground=""#FF0062CC""/>
  </Border>
  <Border Padding=""10,6"" CornerRadius=""12"" Background=""#14000000"" BorderBrush=""#33000000"" BorderThickness=""1"" Visibility=""{{Binding FileVisState}}"">
    <Grid ColumnSpacing=""10"">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
      </Grid.ColumnDefinitions>
      <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
      <StackPanel Grid.Column=""1"" Spacing=""1"">
        <StackPanel Orientation=""Horizontal"" Spacing=""8"">
          <TextBlock Text=""{{Binding Name}}"" FontSize=""12"" FontWeight=""SemiBold""/>
          <Border Background=""#1A0062CC"" CornerRadius=""4"" Padding=""5,0"">
            <TextBlock Text=""{{Binding KindText}}"" Foreground=""#FF0062CC"" FontSize=""10""/>
          </Border>
          <TextBlock Text=""{{Binding AccountText}}"" FontSize=""10.5"" Opacity=""0.6"" VerticalAlignment=""Center""/>
        </StackPanel>
        <TextBlock Text=""{{Binding PathText}}"" FontSize=""11.5"" FontFamily=""Consolas"" TextTrimming=""CharacterEllipsis"" Opacity=""0.85""/>
      </StackPanel>
      <TextBlock Grid.Column=""2"" Text=""{{Binding TimeText}}"" FontSize=""12"" Opacity=""0.72"" VerticalAlignment=""Center"" HorizontalAlignment=""Right""/>
      <TextBlock Grid.Column=""3"" Text=""{{Binding SizeText}}"" FontWeight=""SemiBold"" VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
    </Grid>
  </Border>
</Grid>";

    // 大文件行（内置页 DataTemplate 逐区对应）
    private static string TemplateLargeStyled() => $@"<Border {Xns}
        Style=""{{StaticResource CardStyle}}"" Padding=""10,6"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
    <StackPanel Grid.Column=""1"" Spacing=""1"">
      <TextBlock Text=""{{Binding Name}}"" Style=""{{StaticResource CaptionTextBlock}}"" Foreground=""{{ThemeResource TextPrimaryBrush}}"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding PathText}}"" Style=""{{StaticResource MonoTextBlock}}"" TextTrimming=""CharacterEllipsis""/>
    </StackPanel>
    <TextBlock Grid.Column=""2"" Text=""{{Binding TimeText}}"" Style=""{{StaticResource CaptionTextBlock}}"" VerticalAlignment=""Center"" HorizontalAlignment=""Right""/>
    <TextBlock Grid.Column=""3"" Text=""{{Binding SizeText}}"" Style=""{{StaticResource BodyTextBlock}}"" FontWeight=""SemiBold"" VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
  </Grid>
</Border>";

    private static string TemplateLargePlain() => $@"<Border {Xns} Padding=""10,6"" CornerRadius=""12""
        Background=""#14000000"" BorderBrush=""#33000000"" BorderThickness=""1"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" VerticalAlignment=""Center"" MinWidth=""0""/>
    <StackPanel Grid.Column=""1"" Spacing=""1"">
      <TextBlock Text=""{{Binding Name}}"" FontSize=""12"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding PathText}}"" FontSize=""11.5"" FontFamily=""Consolas"" TextTrimming=""CharacterEllipsis"" Opacity=""0.85""/>
    </StackPanel>
    <TextBlock Grid.Column=""2"" Text=""{{Binding TimeText}}"" FontSize=""12"" Opacity=""0.72"" VerticalAlignment=""Center"" HorizontalAlignment=""Right""/>
    <TextBlock Grid.Column=""3"" Text=""{{Binding SizeText}}"" FontWeight=""SemiBold"" VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
  </Grid>
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

    private static Button AccentTextButton(Symbol symbol, string text, System.Windows.Input.ICommand command)
    {
        var b = new Button();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new SymbolIcon { Symbol = symbol });
        row.Children.Add(new TextBlock { Text = text });
        b.Content = row;
        b.Command = command;
        ApplyStyle(b, "AccentButtonStyle");
        return b;
    }

    private static Button PlainButton(string text, System.Windows.Input.ICommand command)
        => new() { Content = text, Command = command };

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

    private static Brush Secondary() => TryBrush("TextSecondaryBrush") ?? FallbackSecondaryBrush();
    private static Brush Info() => TryBrush("InfoTextBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 98, 204));
    private static Brush FallbackSecondaryBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(160, 128, 128, 128));

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
