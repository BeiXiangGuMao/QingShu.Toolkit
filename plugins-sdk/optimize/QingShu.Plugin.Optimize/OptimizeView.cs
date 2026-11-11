using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace QingShu.Plugin.Optimize;

/// <summary>
/// 性能优化导航页（纯代码构建，零 XAML 编译依赖——XAML 页面在插件 ALC 内的资源加载是硬约束，
/// 与 gala / 弹窗拦截 / U盘工具 / 磁盘清理同一模式）。布局与功能同内置 OptimizePage.xaml 逐区对应：
/// 页头 → 应用级区（内存整理卡 / 进程节能卡）→ 系统级区（启动项管理卡）→ 设计边界 InfoBar。
/// 列表保留 ListView + DataTemplate 虚拟化，模板经 XamlReader.Load 运行时解析走经典 Binding
/// （disk-cleanup 批三条实锤同样适用）；行 VM 直接暴露 Visibility 类型属性供模板绑定免转换器，
/// 视图层绑定沿用本地 IValueConverter；样式键缺失自动降级无资源模板（两级模板构造期选定）。
/// 页面每次导航重建（VM 构造即拉起进程/启动项刷新，与内置页同语义）。
/// </summary>
public sealed class OptimizeView : UserControl
{
    private const string Xns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";
    private readonly OptimizeViewModel _vm;
    private readonly Action<string, string> _log; // (level, message) → context.Log（模板降级等视图级日志）
    private readonly bool _hasStyles;             // 宿主样式可用 → 模板用样式资源；否则无资源降级模板

    public OptimizeView(OptimizeViewModel vm, Action<string, string> log)
    {
        _vm = vm;
        _log = log;
        DataContext = _vm;
        _hasStyles = TryStyle("CardStyle") != null && TryStyle("SectionTitleTextBlock") != null && TryBrush("TextPrimaryBrush") != null;

        Content = BuildLayout();
    }

    // ================= 布局 =================

    private UIElement BuildLayout()
    {
        var rootStack = new StackPanel { MaxWidth = 1080, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 14 };
        var transitions = new TransitionCollection
        {
            new EntranceThemeTransition { FromVerticalOffset = 12, IsStaggeringEnabled = true }
        };
        rootStack.Transitions = transitions;

        // ---- 页头 ----
        var header = new StackPanel { Spacing = 0 };
        var title = new TextBlock { Text = "性能优化（插件）" };
        ApplyStyle(title, "PageTitleTextBlock");
        var subtitle = new TextBlock
        {
            Text = "只做安全且可逆的优化，按影响范围分为应用级与系统级两档。绝不触碰服务/虚拟内存/注册表/Defender。",
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap
        };
        ApplyStyle(subtitle, "PageSubtitleTextBlock");
        header.Children.Add(title);
        header.Children.Add(subtitle);
        rootStack.Children.Add(header);

        // ---- 区块标题：应用级 ----
        rootStack.Children.Add(MakeSectionHeader(
            "应用级优化 —— 只影响所选进程，全部可逆",
            "针对单个程序的内存与功耗动作；系统关键组件已标注并在引擎内受保护。"));

        // ---- 内存整理卡 ----
        var memRefresh = IconTextButton(Symbol.Sync, "刷新", _vm.RefreshMemoryCommand);
        memRefresh.SetBinding(Control.IsEnabledProperty, OneWay(nameof(OptimizeViewModel.MemoryReady)));
        var memTrim = AccentButton("整理所选内存", _vm.TrimMemoryCommand);
        memTrim.SetBinding(Control.IsEnabledProperty, OneWay(nameof(OptimizeViewModel.MemoryReady)));

        var memList = MakeList(LoadTemplate(TemplateProcessStyled(withEco: false), TemplateProcessPlain(withEco: false)),
            nameof(OptimizeViewModel.MemoryProcesses), 300, pad: "0,1");
        var memResult = Caption(Info());
        memResult.SetBinding(TextBlock.TextProperty, OneWay(nameof(OptimizeViewModel.MemoryResult)));
        // 逐进程确定性进度条（整理执行中且已报出进度时显示）
        var memProgress = new ProgressBar { CornerRadius = new CornerRadius(2), Height = 6 };
        memProgress.SetBinding(ProgressBar.ValueProperty, OneWay(nameof(OptimizeViewModel.MemoryProgress)));
        memProgress.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(OptimizeViewModel.MemoryProgressVis)));
        rootStack.Children.Add(MakeCard(
            CardHeader("内存整理（不结束进程）", memRefresh, memTrim),
            CaptionText("EmptyWorkingSet 原理：把进程的物理内存页回收进待用列表，进程继续正常运行，等价于系统自带的内存压缩策略。"),
            memList,
            memProgress,
            memResult));

        // ---- 进程节能卡 ----
        var ecoApply = AccentButton("对所选启用节能", _vm.ApplyEcoCommand);
        ecoApply.SetBinding(Control.IsEnabledProperty, OneWay(nameof(OptimizeViewModel.EcoReady)));
        var ecoRestore = AccentButton("恢复调度", _vm.RestoreEcoCommand);
        ecoRestore.SetBinding(Control.IsEnabledProperty, OneWay(nameof(OptimizeViewModel.EcoReady)));

        var ecoList = MakeList(LoadTemplate(TemplateProcessStyled(withEco: true), TemplateProcessPlain(withEco: true)),
            nameof(OptimizeViewModel.EcoProcesses), 240, pad: "0,1");
        var ecoResult = Caption(Info());
        ecoResult.SetBinding(TextBlock.TextProperty, OneWay(nameof(OptimizeViewModel.EcoResult)));
        rootStack.Children.Add(MakeCard(
            CardHeader("进程节能（EcoQoS 功耗遏制）", ecoApply, ecoRestore),
            CaptionText("EcoQoS 让闲置后台进程运行在能效核/低频档（Win10 1709+），适合在完成后台的更新、同步类任务上使用。"),
            ecoList,
            ecoResult));

        // ---- 区块标题：系统级 ----
        rootStack.Children.Add(MakeSectionHeader(
            "系统级优化 —— 影响整机行为，逐项确认",
            "启动项影响开机加载，支持应用内禁用/启用（可逆）。弹窗拦截已拆分为独立页面（左侧导航「弹窗拦截」）。均不修改系统服务/Defender。"));

        // ---- 启动项管理卡 ----
        var stRefresh = IconTextButton(Symbol.Sync, "刷新", _vm.RefreshStartupCommand);
        var stTaskMgr = AccentButton("打开任务管理器", _vm.OpenTaskManagerCommand);

        var stHint = Caption(Secondary());
        stHint.TextWrapping = TextWrapping.Wrap;
        stHint.SetBinding(TextBlock.TextProperty, OneWay(nameof(OptimizeViewModel.StartupHint)));
        var stResult = Caption(Info());
        stResult.SetBinding(TextBlock.TextProperty, OneWay(nameof(OptimizeViewModel.StartupResult)));
        stResult.SetBinding(FrameworkElement.VisibilityProperty, Vis(nameof(OptimizeViewModel.HasStartupResult)));

        var stList = MakeList(LoadTemplate(TemplateStartupStyled(), TemplateStartupPlain()),
            nameof(OptimizeViewModel.StartupItems), 260, pad: "0,2");
        rootStack.Children.Add(MakeCard(
            CardHeader("启动项管理", stRefresh, stTaskMgr),
            stHint,
            stResult,
            stList));

        // ---- 边界声明 ----
        var boundary = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false,
            Title = "青枢工具箱的设计边界",
            Message = "本工具承诺：不改 services.msc 服务、不改虚拟内存、不导入注册表文件、不做 NTLite 式精简、不内置一键优化全家桶、不关闭 Defender。所有优化均可逆、可解释、可回溯（全部写入日志）。"
        };
        rootStack.Children.Add(boundary);

        return new ScrollViewer { Padding = new Thickness(28, 20, 28, 20), Content = rootStack };
    }

    // ================= 区块构造 =================

    private static StackPanel MakeSectionHeader(string title, string caption)
    {
        var s = new StackPanel { Spacing = 2 };
        var t = new TextBlock { Text = title };
        ApplyStyle(t, "SectionTitleTextBlock");
        var c = new TextBlock { Text = caption };
        ApplyStyle(c, "CaptionTextBlock");
        c.Foreground = Secondary();
        s.Children.Add(t);
        s.Children.Add(c);
        return s;
    }

    private static Grid CardHeader(string title, params Button[] buttons)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        ApplyStyle(t, "SectionTitleTextBlock");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) right.Children.Add(b);
        g.Children.Add(t);
        right.SetValue(Grid.ColumnProperty, 1);
        g.Children.Add(right);
        return g;
    }

    private Border MakeCard(params UIElement[] children)
    {
        var card = new Border { Padding = new Thickness(16) };
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
        var sp = new StackPanel { Spacing = 10 };
        foreach (var c in children) sp.Children.Add(c);
        card.Child = sp;
        return card;
    }

    private static TextBlock CaptionText(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        ApplyStyle(t, "CaptionTextBlock");
        t.Foreground = Secondary();
        return t;
    }

    private static ListView MakeList(DataTemplate? template, string itemsPath, double maxHeight, string pad)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = maxHeight,
            ItemContainerStyle = MakeListItemStyle(pad)
        };
        if (template != null) list.ItemTemplate = template;
        list.SetBinding(ItemsControl.ItemsSourceProperty, OneWay(itemsPath));
        return list;
    }

    private static Style MakeListItemStyle(string pad)
    {
        var s = new Style(typeof(ListViewItem));
        s.Setters.Add(new Setter(FrameworkElement.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        s.Setters.Add(new Setter(Control.PaddingProperty, ParsePad(pad)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        return s;
    }

    private static Thickness ParsePad(string pad)
    {
        // 仅本视图内部使用，格式恒为 "t,l" 两段数字（模板清单写死），解析失败回退 0,1
        var parts = pad.Split(',');
        try
        {
            return new Thickness(0, Convert.ToDouble(parts[0], System.Globalization.CultureInfo.InvariantCulture), 0,
                parts.Length > 1 ? Convert.ToDouble(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0);
        }
        catch { return new Thickness(0, 1, 0, 1); }
    }

    private static Button IconTextButton(Symbol symbol, string text, System.Windows.Input.ICommand command)
    {
        var b = new Button();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new SymbolIcon { Symbol = symbol });
        row.Children.Add(new TextBlock { Text = text });
        b.Content = row;
        b.Command = command;
        return b;
    }

    private static Button AccentButton(string text, System.Windows.Input.ICommand command)
    {
        var b = new Button { Content = text, Command = command };
        ApplyStyle(b, "AccentButtonStyle");
        return b;
    }

    // ================= 运行时模板（XamlReader 经典 Binding，保 ListView 虚拟化） =================

    /// <summary>样式模板优先；解析失败（宿主缺样式键等）自动降级无资源模板并留日志。</summary>
    private DataTemplate? LoadTemplate(string styled, string plain)
    {
        var t = TryLoadTemplate(styled);
        if (t != null) return t;
        _log("warn", "性能优化视图：样式模板解析失败，已降级为基础外观模板（功能不受影响）");
        return TryLoadTemplate(plain);
    }

    private static DataTemplate? TryLoadTemplate(string xaml)
    {
        try { return Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml) as DataTemplate; }
        catch { return null; }
    }

    // 进程行（withEco=false 内存整理：PID+内存两列；true 进程节能：EcoQoS 徽章替换 PID 列）。
    // 两态段先拼进局部变量再以 {mid} 进插值洞：插值洞内只允许纯变量——洞内嵌套字符串/三元会被
    // brackets 门禁剥离器误算（门禁口径实锤），且普通 verbatim 串用单括号、插值模板用双括号，
    // 两层转义规则混写极易出错。
    private static string TemplateProcessStyled(bool withEco)
    {
        var mid = withEco
            ? @"<Border Grid.Column=""2"" Background=""{ThemeResource SuccessChipFillBrush}"" CornerRadius=""4"" Padding=""6,1"" Visibility=""{Binding EcoVisState}"">
    <TextBlock Text=""EcoQoS"" Foreground=""{ThemeResource SuccessTextBrush}"" FontSize=""10""/>
  </Border>"
            : @"<TextBlock Grid.Column=""2"" Text=""{Binding PidText}"" Style=""{StaticResource MonoTextBlock}"" VerticalAlignment=""Center""/>";
        var wsBold = withEco ? "" : @" FontWeight=""SemiBold""";
        return $@"<Grid {Xns} ColumnSpacing=""10"" Padding=""4,2"">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
  </Grid.ColumnDefinitions>
  <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" MinWidth=""0"" VerticalAlignment=""Center""/>
  <StackPanel Grid.Column=""1"" Spacing=""0"">
    <StackPanel Orientation=""Horizontal"" Spacing=""6"">
      <TextBlock Text=""{{Binding Name}}"" Style=""{{StaticResource BodyTextBlock}}""/>
      <Border Background=""{{ThemeResource WarningChipFillBrush}}"" CornerRadius=""4"" Padding=""5,0"" Visibility=""{{Binding IsSystemVisState}}"">
        <TextBlock Text=""系统组件"" Foreground=""{{ThemeResource WarningTextBrush}}"" FontSize=""10""/>
      </Border>
    </StackPanel>
    <TextBlock Text=""{{Binding Purpose}}"" Style=""{{StaticResource CaptionTextBlock}}""/>
  </StackPanel>
  {mid}
  <TextBlock Grid.Column=""3"" Text=""{{Binding WsText}}"" Style=""{{StaticResource BodyTextBlock}}""{wsBold} VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
</Grid>";
    }

    private static string TemplateProcessPlain(bool withEco)
    {
        string mid;
        if (withEco)
        {
            mid = @"<Border Grid.Column=""2"" Background=""#1A107C10"" CornerRadius=""4"" Padding=""6,1"" Visibility=""{Binding EcoVisState}"">
    <TextBlock Text=""EcoQoS"" Foreground=""#FF107C10"" FontSize=""10""/>
  </Border>";
        }
        else
        {
            mid = @"<TextBlock Grid.Column=""2"" Text=""{Binding PidText}"" FontFamily=""Consolas"" VerticalAlignment=""Center"" Opacity=""0.85""/>";
        }
        var wsBold = withEco ? "" : @" FontWeight=""SemiBold""";
        return $@"<Grid {Xns} ColumnSpacing=""10"" Padding=""4,2"">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
  </Grid.ColumnDefinitions>
  <CheckBox IsChecked=""{{Binding IsSelected, Mode=TwoWay}}"" MinWidth=""0"" VerticalAlignment=""Center""/>
  <StackPanel Grid.Column=""1"" Spacing=""0"">
    <StackPanel Orientation=""Horizontal"" Spacing=""6"">
      <TextBlock Text=""{{Binding Name}}""/>
      <Border Background=""{{ThemeResource WarningChipFillBrush}}"" CornerRadius=""4"" Padding=""5,0"" Visibility=""{{Binding IsSystemVisState}}"">
        <TextBlock Text=""系统组件"" Foreground=""{{ThemeResource WarningTextBrush}}"" FontSize=""10""/>
      </Border>
    </StackPanel>
    <TextBlock Text=""{{Binding Purpose}}"" FontSize=""12"" Opacity=""0.72""/>
  </StackPanel>
  {mid}
  <TextBlock Grid.Column=""3"" Text=""{{Binding WsText}}""{wsBold} VerticalAlignment=""Center"" Margin=""14,0,0,0""/>
</Grid>";
    }

    // 启动项行（名称+来源 / 命令 / 已启用·已禁用徽章 / 切换按钮，内置页 DataTemplate 逐区对应）
    private static string TemplateStartupStyled() => $@"<Border {Xns}
        Background=""{{ThemeResource CardBackgroundBrush}}"" BorderBrush=""{{ThemeResource DividerStrokeBrush}}"" BorderThickness=""1""
        CornerRadius=""8"" Padding=""12,7"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""150""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <StackPanel VerticalAlignment=""Center"" Spacing=""1"">
      <TextBlock Text=""{{Binding Name}}"" Style=""{{StaticResource BodyTextBlock}}"" TextTrimming=""CharacterEllipsis"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding Source}}"" Style=""{{StaticResource CaptionTextBlock}}"" TextTrimming=""CharacterEllipsis""/>
    </StackPanel>
    <TextBlock Grid.Column=""1"" Text=""{{Binding Command}}"" Style=""{{StaticResource MonoTextBlock}}"" TextTrimming=""CharacterEllipsis"" VerticalAlignment=""Center"" Opacity=""0.75""/>
    <Border Grid.Column=""2"" Background=""{{ThemeResource SuccessChipFillBrush}}"" CornerRadius=""4"" Padding=""7,1"" Visibility=""{{Binding EnabledVisState}}"" VerticalAlignment=""Center"">
      <TextBlock Text=""已启用"" Foreground=""{{ThemeResource SuccessTextBrush}}"" FontSize=""10""/>
    </Border>
    <Border Grid.Column=""2"" Background=""{{ThemeResource InfoChipFillBrush}}"" CornerRadius=""4"" Padding=""7,1"" Visibility=""{{Binding DisabledVisState}}"" VerticalAlignment=""Center"">
      <TextBlock Text=""已禁用"" Foreground=""{{ThemeResource InfoTextBrush}}"" FontSize=""10""/>
    </Border>
    <Button Grid.Column=""3"" Command=""{{Binding Owner.ToggleStartupCommand}}"" CommandParameter=""{{Binding}}"" Content=""{{Binding ToggleText}}""
            Padding=""12,3"" MinHeight=""0"" VerticalAlignment=""Center"" ToolTipService.ToolTip=""禁用前自动备份，可随时恢复""/>
  </Grid>
</Border>";

    private static string TemplateStartupPlain() => $@"<Border {Xns} Background=""#14000000"" BorderBrush=""#33000000"" BorderThickness=""1""
        CornerRadius=""8"" Padding=""12,7"">
  <Grid ColumnSpacing=""10"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""150""/><ColumnDefinition Width=""*""/><ColumnDefinition Width=""Auto""/><ColumnDefinition Width=""Auto""/>
    </Grid.ColumnDefinitions>
    <StackPanel VerticalAlignment=""Center"" Spacing=""1"">
      <TextBlock Text=""{{Binding Name}}"" TextTrimming=""CharacterEllipsis"" FontWeight=""SemiBold""/>
      <TextBlock Text=""{{Binding Source}}"" FontSize=""12"" TextTrimming=""CharacterEllipsis"" Opacity=""0.72""/>
    </StackPanel>
    <TextBlock Grid.Column=""1"" Text=""{{Binding Command}}"" FontFamily=""Consolas"" TextTrimming=""CharacterEllipsis"" VerticalAlignment=""Center"" Opacity=""0.75""/>
    <Border Grid.Column=""2"" Background=""#1A107C10"" CornerRadius=""4"" Padding=""7,1"" Visibility=""{{Binding EnabledVisState}}"" VerticalAlignment=""Center"">
      <TextBlock Text=""已启用"" Foreground=""#FF107C10"" FontSize=""10""/>
    </Border>
    <Border Grid.Column=""2"" Background=""#1A0062CC"" CornerRadius=""4"" Padding=""7,1"" Visibility=""{{Binding DisabledVisState}}"" VerticalAlignment=""Center"">
      <TextBlock Text=""已禁用"" Foreground=""#FF0062CC"" FontSize=""10""/>
    </Border>
    <Button Grid.Column=""3"" Command=""{{Binding Owner.ToggleStartupCommand}}"" CommandParameter=""{{Binding}}"" Content=""{{Binding ToggleText}}""
            Padding=""12,3"" MinHeight=""0"" VerticalAlignment=""Center"" ToolTipService.ToolTip=""禁用前自动备份，可随时恢复""/>
  </Grid>
</Border>";

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

    private static readonly Windows.UI.Color ColorsTransparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    // ================= Binding 辅助 =================

    private static Binding OneWay(string path) => new()
    {
        Path = new PropertyPath(path),
        Mode = BindingMode.OneWay
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
