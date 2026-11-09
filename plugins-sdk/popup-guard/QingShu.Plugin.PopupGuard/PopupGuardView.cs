using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace QingShu.Plugin.PopupGuard;

/// <summary>
/// 弹窗拦截导航页（纯代码构建，零 XAML 编译依赖——XAML 页面在插件 ALC 内的资源加载是硬约束，
/// 与 gala 面板同一模式）：开关与状态 / 白名单管理 / 最近拦截列表。
/// 界面数据 1 秒轮询拉引擎快照（计数/列表签名比对，无变化不重建）；
/// Unloaded 只停界面定时器，拦截引擎由插件 OnUnloadedAsync 统一收尾。
/// 色刷/样式在构造时从宿主 Application.Resources 取当前主题变体；
/// 页面每次导航进入都会重建（宿主不缓存视图），切主题后重进即跟随新主题。
/// </summary>
public sealed class PopupGuardView : UserControl
{
    private readonly PopupGuardPlugin _owner;
    private readonly DispatcherQueueTimer _timer;

    private readonly ToggleSwitch _enabledSwitch;
    private readonly Border _runningBadge;
    private readonly Border _pausedBadge;
    private readonly TextBlock _countText;
    private readonly TextBlock _whitelistSummary;
    private readonly TextBox _newWhitelistName;
    private readonly StackPanel _whitelistList;
    private readonly ScrollViewer _whitelistScroll;
    private readonly StackPanel _recentList;
    private readonly ScrollViewer _recentScroll;
    private readonly TextBlock _recentEmpty;

    private long _recentSig = -1; // 最近拦截列表签名（计数+条数+首条时间），变化才重建
    private int _lastCount = -1;

    public PopupGuardView(PopupGuardPlugin owner)
    {
        _owner = owner;

        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => { try { RefreshRuntime(); } catch { } };
        Loaded += (_, _) => { RefreshRuntime(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        var store = _owner.Store; // 初始化失败会抛，宿主 CreateNavViewFor 已兜底回退内置页

        // ---------- 顶部开关卡 ----------
        _enabledSwitch = new ToggleSwitch { IsOn = store.Enabled, Margin = new Thickness(-8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _enabledSwitch.Toggled += (_, _) =>
        {
            try
            {
                _owner.SetEnabled(_enabledSwitch.IsOn);
                RefreshRuntime(); // 徽章/计数立即跟随
            }
            catch (Exception ex)
            {
                _owner.Context.Log("error", $"切换开关失败：{ex.Message}");
            }
        };

        _runningBadge = Badge("运行中", "SuccessChipFillBrush", "SuccessTextBrush");
        _pausedBadge = Badge("已暂停", "InfoChipFillBrush", "InfoTextBrush");

        _countText = new TextBlock
        {
            Text = "0",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = TryBrush("InfoTextBrush") ?? FallbackInfoBrush(),
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var iconBox = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = TryBrush("InfoChipFillBrush") ?? FallbackInfoChipBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBox.Child = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = "\uE720",
            FontSize = 20,
            Foreground = TryBrush("InfoTextBrush") ?? FallbackInfoBrush()
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = "被动弹窗拦截", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        titleRow.Children.Add(_runningBadge);
        titleRow.Children.Add(_pausedBadge);

        var titleCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleCol.Children.Add(titleRow);
        titleCol.Children.Add(new TextBlock
        {
            Text = "开启后每 3 秒扫描一次新出现的可见窗口：白名单内的进程放行，其余被动关闭（不注入、不挂钩，仅发送关闭消息）。启动时已存在的窗口、前台窗口、托盘/输入法等系统外壳永不拦截。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });

        var counterCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        counterCol.Children.Add(_countText);
        counterCol.Children.Add(new TextBlock { Text = "本次开机累计拦截", FontSize = 11, Foreground = SecondaryBrush(), HorizontalAlignment = HorizontalAlignment.Right });

        var headerGrid = new Grid { ColumnSpacing = 16 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        iconBox.SetValue(Grid.ColumnProperty, 0);
        titleCol.SetValue(Grid.ColumnProperty, 1);
        counterCol.SetValue(Grid.ColumnProperty, 2);
        var counterSwitchCol = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        counterSwitchCol.Children.Add(counterCol);
        counterSwitchCol.Children.Add(_enabledSwitch);
        counterSwitchCol.SetValue(Grid.ColumnProperty, 2);
        headerGrid.Children.Add(iconBox);
        headerGrid.Children.Add(titleCol);
        headerGrid.Children.Add(counterSwitchCol);

        // ---------- 白名单卡 ----------
        _whitelistSummary = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TryBrush("InfoTextBrush") ?? FallbackInfoBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 480
        };

        _newWhitelistName = new TextBox
        {
            PlaceholderText = "输入进程名，如 qqexternal（无需 .exe）"
        };
        var addButton = new Button
        {
            Content = "添加到白名单",
            Style = TryStyle("AccentButtonStyle")
        };
        addButton.Click += (_, _) => AddWhitelist();

        var inputGrid = new Grid { ColumnSpacing = 8 };
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
        _newWhitelistName.SetValue(Grid.ColumnProperty, 0);
        addButton.SetValue(Grid.ColumnProperty, 1);
        inputGrid.Children.Add(_newWhitelistName);
        inputGrid.Children.Add(addButton);

        _whitelistList = new StackPanel { Spacing = 6 };
        _whitelistScroll = new ScrollViewer
        {
            Content = _whitelistList,
            MaxHeight = 264,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var whitelistCard = MakeCard();
        var whitelistBody = (StackPanel)whitelistCard.Child;
        var wlHeader = new Grid();
        wlHeader.Children.Add(new TextBlock { Text = "白名单进程", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        wlHeader.Children.Add(_whitelistSummary);
        whitelistBody.Children.Add(wlHeader);
        whitelistBody.Children.Add(new TextBlock
        {
            Text = "填写要放行的进程名（无需 .exe，支持前缀通配如 wps*）。建议添加网课直播、考试客户端、办公与投影软件，避免上课软件窗口被误关。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        whitelistBody.Children.Add(inputGrid);
        whitelistBody.Children.Add(_whitelistScroll);

        // ---------- 最近拦截卡 ----------
        var clearButton = new Button { Content = "清空列表" };
        clearButton.Click += (_, _) =>
        {
            try
            {
                _owner.Engine?.ClearRecent();
                _recentSig = -1;
                RefreshRuntime();
            }
            catch { }
        };

        _recentList = new StackPanel { Spacing = 2 };
        _recentEmpty = new TextBlock
        {
            Text = "本会话还没有拦截记录。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10)
        };
        _recentScroll = new ScrollViewer
        {
            Content = _recentList,
            MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var recentCard = MakeCard();
        var recentBody = (StackPanel)recentCard.Child;
        var rcHeader = new Grid();
        var rcTitle = new TextBlock { Text = "最近拦截（本次开机）", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        clearButton.HorizontalAlignment = HorizontalAlignment.Right;
        rcHeader.Children.Add(rcTitle);
        rcHeader.Children.Add(clearButton);
        recentBody.Children.Add(rcHeader);
        recentBody.Children.Add(new TextBlock
        {
            Text = "若教学软件窗口被误拦截，请将其进程名加入上方白名单后重新打开该软件。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        recentBody.Children.Add(_recentEmpty);
        recentBody.Children.Add(_recentScroll);

        // ---------- 策略说明 ----------
        var infoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsOpen = true,
            IsClosable = false,
            Title = "拦截策略说明",
            Message = "被动原则：不做注入、不挂钩 DLL，仅扫描可见顶层窗口并发送关闭消息。拦截动作与白名单变更全部写入日志，可在「日志查看」页回溯（来源 plugin:qingshu.plugin.popupguard）。"
        };

        // ---------- 页根 ----------
        var title = new TextBlock
        {
            Text = "弹窗拦截",
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        var subtitle = new TextBlock
        {
            Text = "被动拦截全网课/办公软件之外的新弹窗：白名单进程永不拦截，前台与系统窗口永不打扰。（外移插件实现，可在插件管理页卸载回退内置版）",
            FontSize = 12,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        var root = new StackPanel
        {
            MaxWidth = 1080,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 14
        };
        root.Children.Add(title);
        root.Children.Add(subtitle);
        var headerCard = MakeCard();
        headerCard.Child = headerGrid; // 头卡内容为自定义三列 Grid（覆盖卡片默认空 StackPanel）
        root.Children.Add(headerCard);
        root.Children.Add(whitelistCard);
        root.Children.Add(recentCard);
        root.Children.Add(infoBar);

        Content = new ScrollViewer
        {
            Padding = new Thickness(28, 20, 28, 20),
            Content = root
        };

        _whitelistSummary.Text = WhitelistSummaryText();
        RebuildWhitelist();
    }

    private string WhitelistSummaryText()
    {
        var list = _owner.Store.Whitelist;
        return list.Count == 0
            ? "白名单为空"
            : $"共 {list.Count} 项（前缀通配支持 wps*）";
    }

    // ================= 运行时刷新（1s 轮询） =================

    private void RefreshRuntime()
    {
        var engine = _owner.Engine;
        int count = engine?.BlockedCount ?? 0;
        bool running = engine != null && engine.Enabled && _enabledSwitch.IsOn;

        if (count != _lastCount)
        {
            _countText.Text = count.ToString();
            _lastCount = count;
        }
        _runningBadge.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _pausedBadge.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        // 最近拦截列表签名比对：无变化不重建（列表最长 200 条，重建有成本）
        long sig;
        var recent = engine?.Recent ?? Array.Empty<BlockedEntry>();
        sig = recent.Count == 0 ? 0 : count * 1_000_000_000L + recent.Count * 100_000L + recent[0].Time.Ticks % 100_000L;
        if (sig != _recentSig)
        {
            _recentSig = sig;
            RebuildRecent(recent);
        }
    }

    private void RebuildRecent(IReadOnlyList<BlockedEntry> recent)
    {
        _recentList.Children.Clear();
        _recentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _recentScroll.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var mono = TryStyle("MonoTextBlock");
        foreach (var r in recent)
        {
            var time = new TextBlock
            {
                Text = r.Time.ToString("HH:mm:ss"),
                FontSize = 11.5,
                Foreground = SecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (mono != null) time.Style = mono;

            var procBadge = new Border
            {
                Background = TryBrush("WarningChipFillBrush") ?? FallbackWarnChipBrush(),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center
            };
            procBadge.Child = new TextBlock
            {
                Text = r.Process,
                FontSize = 10.5,
                Foreground = TryBrush("WarningTextBrush") ?? FallbackWarnBrush()
            };

            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 2, 4, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            time.SetValue(Grid.ColumnProperty, 0);
            procBadge.SetValue(Grid.ColumnProperty, 1);
            grid.Children.Add(time);
            grid.Children.Add(procBadge);
            var title = new TextBlock
            {
                Text = r.Title.Length == 0 ? "（无标题）" : r.Title,
                FontSize = 11.5,
                Foreground = SecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetValue(Grid.ColumnProperty, 2);
            grid.Children.Add(title);
            _recentList.Children.Add(grid);
        }
    }

    // ================= 白名单操作 =================

    private void AddWhitelist()
    {
        var name = _newWhitelistName.Text.Trim().Replace(".exe", "");
        if (name.Length == 0) return;
        var store = _owner.Store;
        if (store.Whitelist.Any(w => w.Trim().ToLowerInvariant() == name.ToLowerInvariant()))
        {
            _newWhitelistName.Text = "";
            return; // 重复项静默忽略（与内置版一致）
        }
        store.Whitelist.Add(name);
        _owner.SaveWhitelist();
        _newWhitelistName.Text = "";
        _whitelistSummary.Text = WhitelistSummaryText();
        RebuildWhitelist();
        _owner.Context.Log("info", $"白名单已添加：{name}");
    }

    private void RemoveWhitelist(string name)
    {
        var store = _owner.Store;
        var item = store.Whitelist.FirstOrDefault(w => string.Equals(w.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (item != null)
        {
            store.Whitelist.Remove(item);
            _owner.SaveWhitelist();
            _whitelistSummary.Text = WhitelistSummaryText();
            RebuildWhitelist();
            _owner.Context.Log("info", $"白名单已移除：{item}");
        }
    }

    private void RebuildWhitelist()
    {
        var store = _owner.Store;
        _whitelistList.Children.Clear();
        if (store.Whitelist.Count == 0)
        {
            _whitelistList.Children.Add(new TextBlock
            {
                Text = "白名单为空。除系统外壳外的所有新窗口都会被拦截——请先把教学软件加入白名单。",
                FontSize = 11.5,
                Foreground = SecondaryBrush(),
                Margin = new Thickness(0, 6, 0, 6)
            });
            return;
        }
        foreach (var name in store.Whitelist)
        {
            var rowIcon = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = TryBrush("InfoChipFillBrush") ?? FallbackInfoChipBrush()
            };
            rowIcon.Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE72E",
                FontSize = 12,
                Foreground = TryBrush("InfoTextBrush") ?? FallbackInfoBrush()
            };

            var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
            nameCol.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            nameCol.Children.Add(new TextBlock { Text = "命中后窗口放行，不再被拦截", FontSize = 11, Foreground = SecondaryBrush() });

            var removeButton = new Button
            {
                Padding = new Thickness(6, 4, 6, 4),
                MinWidth = 0,
                MinHeight = 0,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                VerticalAlignment = VerticalAlignment.Center
            };
            removeButton.Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = "\uE711",
                FontSize = 11,
                Foreground = TryBrush("TextSecondaryBrush") ?? SecondaryBrush()
            };
            var captured = name;
            removeButton.Click += (_, _) => RemoveWhitelist(captured);
            ToolTipService.SetToolTip(removeButton, "从白名单移除");

            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Auto) });
            rowIcon.SetValue(Grid.ColumnProperty, 0);
            nameCol.SetValue(Grid.ColumnProperty, 1);
            removeButton.SetValue(Grid.ColumnProperty, 2);
            grid.Children.Add(rowIcon);
            grid.Children.Add(nameCol);
            grid.Children.Add(removeButton);

            _whitelistList.Children.Add(new Border
            {
                Background = TryBrush("CardBackgroundBrush") ?? new SolidColorBrush(Colors.Transparent),
                BorderBrush = TryBrush("DividerStrokeBrush") ?? new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Child = grid
            });
        }
    }

    // ================= 构建辅助 =================

    /// <summary>标准卡片容器（宿主 CardStyle；取不到时本地兜底：圆角+描边）。初始内容为空 StackPanel。</summary>
    private static Border MakeCard()
    {
        var b = new Border { Padding = new Thickness(16) };
        var style = TryStyle("CardStyle");
        if (style != null)
        {
            b.Style = style;
        }
        else
        {
            b.CornerRadius = new CornerRadius(12);
            b.Background = TryBrush("CardBackgroundBrush") ?? new SolidColorBrush(Colors.Transparent);
            b.BorderBrush = TryBrush("DividerStrokeBrush") ?? new SolidColorBrush(Colors.Transparent);
            b.BorderThickness = new Thickness(1);
        }
        b.Child = new StackPanel { Spacing = 10 };
        return b;
    }

    private static Border Badge(string text, string fillKey, string textKey)
    {
        var b = new Border
        {
            Background = TryBrush(fillKey) ?? FallbackInfoChipBrush(),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        b.Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = TryBrush(textKey) ?? FallbackInfoBrush()
        };
        return b;
    }

    private static Brush? TryBrush(string key)
    {
        try { return Application.Current.Resources[key] as Brush; } catch { return null; }
    }

    private static Style? TryStyle(string key)
    {
        try { return Application.Current.Resources[key] as Style; } catch { return null; }
    }

    private static Brush SecondaryBrush() => new SolidColorBrush(Color.FromArgb(255, 110, 118, 128));
    private static Brush FallbackInfoBrush() => new SolidColorBrush(Color.FromArgb(255, 0, 98, 204));
    private static Brush FallbackInfoChipBrush() => new SolidColorBrush(Color.FromArgb(255, 26, 0, 122, 255));
    private static Brush FallbackWarnBrush() => new SolidColorBrush(Color.FromArgb(255, 201, 52, 0));
    private static Brush FallbackWarnChipBrush() => new SolidColorBrush(Color.FromArgb(255, 26, 255, 149, 0));
}