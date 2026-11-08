using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using QingShu.Gala;

namespace QingShu.Plugin.GalaMaster;

/// <summary>
/// 主控端面板（纯代码构建，零 XAML 编译依赖）：
/// 拓扑实时画布（节点压力着色 / 链路流量）+ 节点压力列表（含迷你趋势线）+ 客户端列表
/// + 远程操控（切换线路/优先级/限速标记/即时采集）+ 调度参数热调 + 事件日志。
/// 面板关闭（Unloaded）只停界面定时器；后台监听由插件 OnUnloadedAsync 统一收尾。
/// </summary>
public sealed class GalaMasterPanelView : UserControl
{
    private const double TopologyWidth = 780;
    private const double TopologyHeight = 540;

    private readonly GalaMasterPlugin _owner;
    private readonly DispatcherQueueTimer _timer;

    private readonly ToggleSwitch _masterSwitch;
    private readonly TextBlock _statusText;
    private readonly Canvas _topology;
    private readonly StackPanel _serverPanel;
    private readonly ListView _clientList;
    private readonly ComboBox _routeBox;
    private readonly ComboBox _prioBox;
    private readonly TextBox _throttleBox;
    private readonly TextBox _paramTick;
    private readonly TextBox _paramThreshold;
    private readonly TextBox _paramCooldown;
    private readonly TextBox _paramFull;
    private readonly TextBlock _eventText;

    private readonly List<ClientSnap> _clientRows = new();
    private readonly List<string> _serverIds = new();
    private readonly TextBlock _onlineSummary;
    private string _selectedClientId = "";
    private string _routeSignature = "";
    private string _clientsSignature = "";
    private string _serversSignature = "";
    private string _topologySignature = "";
    private bool _suspendSwitch;

    public GalaMasterPanelView(GalaMasterPlugin owner)
    {
        _owner = owner;
        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => { try { RefreshAll(); } catch { } };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();

        // ---------- 顶部：主控开关 + 状态行 ----------
        _masterSwitch = new ToggleSwitch { OnContent = "主控运行中", OffContent = "启动主控端", IsOn = false, Margin = new Thickness(0, 0, 16, 0) };
        _masterSwitch.Toggled += async (_, _) =>
        {
            if (_suspendSwitch) return; // 状态同步触发，非用户操作
            await OnMasterToggledAsync();
        };

        var opsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opsRow.Children.Add(new TextBlock { Text = "主控端（礼堂控制台）", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        opsRow.Children.Add(_masterSwitch);
        opsRow.Children.Add(_onlineSummary); // 运行摘要每 2 秒刷新（在线 n/m），不占用操作反馈行
        opsRow.Children.Add(new TextBlock
        {
            Text = "监听端口/准入段/配对码在 gala-topology.json 配置（插件快捷动作可打开）。被控终端装服务端插件或独立 QingShu.GalaAgent 程序接入。",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        });

        _statusText = new TextBlock
        {
            Text = "就绪。点「启动主控端」开始监听；各 IP 段终端可通，仅限城域网内网。",
            FontSize = 12,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        // ---------- 主控区 ----------
        _topology = new Canvas { Width = TopologyWidth, Height = TopologyHeight };
        var topologyScroll = new ScrollViewer
        {
            Content = _topology,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var legend = new TextBlock
        {
            Text = "节点颜色 = 综合压力（绿 <40 / 黄 40-70 / 红 ≥70，灰 = 失联或非压力节点）；小圆 = 动态注册的真实终端（蓝=网线 / 紫=直连 WiFi / 灰=离线，悬停看详情），按网段挂在接入交换机下；拓扑与链路来自 gala-topology.json。",
            FontSize = 11,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        _serverPanel = new StackPanel { Spacing = 4 };

        // ---- 调度参数区（面板内热调，免手编 JSON）----
        var s0 = _owner.Config.Settings;
        _paramTick = new TextBox { Text = s0.TickSeconds.ToString(), Width = 56 };
        _paramThreshold = new TextBox { Text = s0.SwitchThreshold.ToString("F0"), Width = 56 };
        _paramCooldown = new TextBox { Text = s0.SwitchCooldownSeconds.ToString(), Width = 56 };
        _paramFull = new TextBox { Text = s0.ClientsPerNodeFull.ToString(), Width = 56 };
        var saveParamBtn = new Button { Content = "保存并生效", Padding = new Thickness(12, 4, 12, 4) };
        saveParamBtn.Click += (_, _) => OnSaveParamsClick();
        var paramRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        paramRow.Children.Add(new TextBlock { Text = "周期s", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Foreground = SecondaryBrush() });
        paramRow.Children.Add(_paramTick);
        paramRow.Children.Add(new TextBlock { Text = "阈值", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Foreground = SecondaryBrush() });
        paramRow.Children.Add(_paramThreshold);
        paramRow.Children.Add(new TextBlock { Text = "冷却s", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Foreground = SecondaryBrush() });
        paramRow.Children.Add(_paramCooldown);
        paramRow.Children.Add(new TextBlock { Text = "满载台", VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Foreground = SecondaryBrush() });
        paramRow.Children.Add(_paramFull);
        paramRow.Children.Add(saveParamBtn);
        var paramNote = new TextBlock
        {
            Text = "改的是同一份运行配置：下一探测周期自动生效，无需重启主控、终端不掉线；同时写回 gala-topology.json。",
            FontSize = 11,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        _onlineSummary = new TextBlock
        {
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = SecondaryBrush(),
            Margin = new Thickness(0, 0, 8, 0)
        };

        _clientList = new ListView { Height = 170, SelectionMode = SelectionMode.Single };
        _clientList.SelectionChanged += (_, _) =>
        {
            try
            {
                var idx = _clientList.SelectedIndex;
                _selectedClientId = idx >= 0 && idx < _clientRows.Count ? _clientRows[idx].ClientId : "";
            }
            catch { }
        };

        _routeBox = new ComboBox { PlaceholderText = "选择目标服务器", MinWidth = 190 };
        _prioBox = new ComboBox { PlaceholderText = "优先级", MinWidth = 90 };
        _prioBox.Items.Add("高（先保）");
        _prioBox.Items.Add("中（默认）");
        _prioBox.Items.Add("低（可让）");
        _prioBox.SelectedIndex = -1;

        var assignBtn = new Button
        {
            Content = "切换线路",
            Padding = new Thickness(12, 4, 12, 4)
        };
        assignBtn.Click += (_, _) => OnAssignClick();
        var prioBtn = new Button { Content = "设优先级", Padding = new Thickness(12, 4, 12, 4) };
        prioBtn.Click += (_, _) => OnPriorityClick();
        _throttleBox = new TextBox { PlaceholderText = "kbps（空/0解除）", Width = 130 };
        var throttleBtn = new Button { Content = "限速标记", Padding = new Thickness(12, 4, 12, 4) };
        throttleBtn.Click += (_, _) => OnThrottleClick();
        var pingBtn = new Button { Content = "即时采集", Padding = new Thickness(12, 4, 12, 4) };
        pingBtn.Click += (_, _) => OnPingNowClick();
        var kickBtn = new Button { Content = "剔除会话", Padding = new Thickness(12, 4, 12, 4) };
        kickBtn.Click += (_, _) => OnKickClick();
        var unpinBtn = new Button { Content = "恢复自动", Padding = new Thickness(12, 4, 12, 4) };
        unpinBtn.Click += (_, _) => OnUnpinClick();

        var controlRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controlRow.Children.Add(_routeBox);
        controlRow.Children.Add(assignBtn);
        controlRow.Children.Add(_prioBox);
        controlRow.Children.Add(prioBtn);
        controlRow.Children.Add(_throttleBox);
        controlRow.Children.Add(throttleBtn);
        controlRow.Children.Add(pingBtn);
        controlRow.Children.Add(kickBtn);
        controlRow.Children.Add(unpinBtn);

        _eventText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush() };
        var eventScroll = new ScrollViewer
        {
            Content = _eventText,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var rightCol = new StackPanel { Spacing = 8 };
        rightCol.Children.Add(new TextBlock { Text = "节点压力（探测 + 并发 + 带宽加权，右侧为近期趋势）", FontSize = 12.5 });
        rightCol.Children.Add(_serverPanel);
        rightCol.Children.Add(new TextBlock { Text = "调度参数（现场手感在这里调）", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(paramRow);
        rightCol.Children.Add(paramNote);
        rightCol.Children.Add(new TextBlock { Text = "客户端（点击选中后可远程操控）", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(_clientList);
        rightCol.Children.Add(new TextBlock { Text = "远程操控", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(controlRow);
        rightCol.Children.Add(new TextBlock { Text = "调度与操控事件", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(eventScroll);

        var masterGrid = new Grid { ColumnSpacing = 14 };
        masterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        masterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400, GridUnitType.Pixel) });
        var leftCol = new StackPanel { Spacing = 4 };
        leftCol.Children.Add(topologyScroll);
        leftCol.Children.Add(legend);
        masterGrid.Children.Add(leftCol, 0, 0);
        masterGrid.Children.Add(rightCol, 1, 0);

        // ---------- 组装（状态行独立成行，操作结果全程可见） ----------
        var root = new Grid { RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(opsRow, 0, 0);
        root.Children.Add(_statusText, 1, 0);
        root.Children.Add(masterGrid, 2, 0);

        Content = root;
        RefreshAll();
    }

    // ================= 主控开关 =================

    private async System.Threading.Tasks.Task OnMasterToggledAsync()
    {
        try
        {
            if (_masterSwitch.IsOn)
            {
                var master = _owner.EnsureMaster();
                master.Start(); // 端口占用等异常抛出 → 下方 catch 显示
                _statusText.Text = $"主控端监听中：0.0.0.0:{master.Port}。等待服务端接入…";
            }
            else
            {
                _statusText.Text = "主控端已停止监听。";
                // StopAsync 内部有超时兜底，切换 UI 不等待
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await _owner.EnsureMaster().StopAsync(); } catch { }
                });
            }
        }
        catch (Exception ex)
        {
            _masterSwitch.IsOn = false;
            _statusText.Text = $"启动主控失败：{ex.Message}（常见：端口被占用，改 gala-topology.json 的 listenPort）";
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    // ================= 远程操控 =================

    private void OnAssignClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            var idx = _routeBox.SelectedIndex;
            if (idx < 0 || idx >= _serverIds.Count) { _statusText.Text = "请选择目标服务器。"; return; }
            bool ok = master.TryAssignServer(_selectedClientId, _serverIds[idx]);
            _statusText.Text = ok
                ? $"切换指令已下发：{_selectedClientId} → {_serverIds[idx]}（已锁定，自动调度不再覆盖；点「恢复自动」解除）。"
                : "下发失败：客户端不在线或已断开。";
        }
        catch (Exception ex) { _statusText.Text = $"切换线路失败：{ex.Message}"; }
    }

    private void OnPriorityClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            if (_prioBox.SelectedIndex < 0) { _statusText.Text = "请选择优先级。"; return; }
            bool ok = master.TrySetPriority(_selectedClientId, _prioBox.SelectedIndex);
            _statusText.Text = ok
                ? $"优先级指令已下发：{_selectedClientId} → {GalaDispatcher.PriorityNames[_prioBox.SelectedIndex]}。"
                : "下发失败：客户端不在线或已断开。";
        }
        catch (Exception ex) { _statusText.Text = $"设置优先级失败：{ex.Message}"; }
    }

    private void OnPingNowClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            bool ok = master.TryPingNow(_selectedClientId);
            _statusText.Text = ok ? "即时采集请求已下发。" : "下发失败：客户端不在线或已断开。";
        }
        catch (Exception ex) { _statusText.Text = $"即时采集失败：{ex.Message}"; }
    }

    private void OnKickClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            bool ok = master.TryKick(_selectedClientId);
            _statusText.Text = ok
                ? $"已剔除 {_selectedClientId}：连接断开并移除（重连将按新终端重新分配）。"
                : "剔除失败：会话不存在（可能已被清理）。";
        }
        catch (Exception ex) { _statusText.Text = $"剔除失败：{ex.Message}"; }
    }

    private void OnUnpinClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            bool ok = master.TryUnpin(_selectedClientId);
            _statusText.Text = ok
                ? $"已恢复自动调度：{_selectedClientId}（下一探测周期重新参与综合调度）。"
                : "操作失败：客户端不在线或已断开。";
        }
        catch (Exception ex) { _statusText.Text = $"恢复自动调度失败：{ex.Message}"; }
    }

    private void OnSaveParamsClick()
    {
        try
        {
            if (!int.TryParse(_paramTick.Text.Trim(), out int tick) || tick < 1)
                throw new InvalidOperationException("探测周期需为正整数（秒）");
            if (!double.TryParse(_paramThreshold.Text.Trim(), out double th) || th <= 0)
                throw new InvalidOperationException("切换阈值需为正数（路径分差）");
            if (!int.TryParse(_paramCooldown.Text.Trim(), out int cd) || cd < 0)
                throw new InvalidOperationException("冷却需为非负整数（秒）");
            if (!int.TryParse(_paramFull.Text.Trim(), out int full) || full < 1)
                throw new InvalidOperationException("满载基数需为正整数（台）");
            string msg = _owner.UpdateRuntimeSettings(tick, th, cd, full);
            _statusText.Text = $"调度参数已保存：{msg}";
        }
        catch (Exception ex) { _statusText.Text = $"保存调度参数失败：{ex.Message}"; }
    }

    private void OnThrottleClick()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (_selectedClientId.Length == 0) { _statusText.Text = "请先在列表中选中一个客户端。"; return; }
            var raw = _throttleBox.Text.Trim();
            int kbps = 0;
            if (raw.Length > 0 && (!int.TryParse(raw, out kbps) || kbps < 0))
            {
                _statusText.Text = "限速值需为非负整数 kbps（留空或 0 = 解除限速）。";
                return;
            }
            bool ok = master.TrySetThrottle(_selectedClientId, kbps);
            _statusText.Text = ok
                ? (kbps > 0
                    ? $"限速标记已下发：{_selectedClientId} → {kbps} kbps（客户端落盘 assignment.json 供执行器消费）。"
                    : $"限速解除指令已下发：{_selectedClientId}。")
                : "下发失败：客户端不在线或已断开。";
        }
        catch (Exception ex) { _statusText.Text = $"限速标记失败：{ex.Message}"; }
    }

    // ================= 定时刷新 =================

    private void RefreshAll()
    {
        try
        {
            SyncSwitchStates();
            SyncRouteOptions();
            var master = _owner.EnsureMaster();
            var snap = master.Snapshot();
            // 运行摘要：一眼看到接入情况（在线数/监听状态），操作反馈另走状态行不冲突
            _onlineSummary.Text = master.IsRunning
                ? $"在线 {snap.Clients.Count(c => c.Online)}/{snap.Clients.Count} 台 · 端口 {snap.Port}"
                : "";
            RenderServers(snap);
            RenderClients(snap);
            _eventText.Text = snap.Events.Count > 0 ? string.Join("\n", snap.Events) : "暂无事件。";
            DrawTopology(snap);
        }
        catch { }
    }

    private void RenderServers(MasterSnapshot snap)
    {
        // 签名一致跳过重建（含趋势线末点与点数）：数值没变不重画，减少无谓 UI 抖动
        var sig = string.Join("\n", snap.Nodes.Where(n => n.Type is "router" or "server").Select(n =>
            $"{n.Id}|{n.Down}|{n.Pressure:F1}|{n.RttMs:F0}|{n.LossPct:F0}|{n.ServedClients}|{n.GatewayAlive}|{n.GatewayTotal}"))
            + "||hist:" + string.Join(";", snap.PressureHistory.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{kv.Value.Count}:{(kv.Value.Count > 0 ? kv.Value[^1].ToString("F1") : "")}"));
        if (sig == _serversSignature) return;
        _serversSignature = sig;

        _serverPanel.Children.Clear();
        foreach (var n in snap.Nodes.Where(n => n.Type is "router" or "server").OrderBy(n => n.Type).ThenBy(n => n.Id))
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(n.Down ? Color.FromArgb(255, 130, 130, 130) : PressureColor(n.Pressure)),
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(dot);
            Grid.SetColumn(dot, 0);

            var info = new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Text = n.Down
                    ? $"{n.Label} · {n.Ip} · 失联（压力按打满计）"
                    : $"{n.Label} · {n.Ip}" + (n.GatewayTotal > 1 ? $" · 网关 {n.GatewayAlive}/{n.GatewayTotal}" : "")
                      + $" · 压力 {n.Pressure:F0} · RTT {FmtMs(n.RttMs)} · 丢包 {n.LossPct:F0}% · 服务 {n.ServedClients} 台"
            };
            row.Children.Add(info);
            Grid.SetColumn(info, 1);

            var trend = BuildTrendLine(snap.PressureHistory, n.Id);
            if (trend != null)
            {
                row.Children.Add(trend);
                Grid.SetColumn(trend, 2);
            }
            _serverPanel.Children.Add(row);
        }
        if (_serverPanel.Children.Count == 0)
            _serverPanel.Children.Add(new TextBlock { Text = "gala-topology.json 中还没有 router/server 节点。", FontSize = 11.5, Foreground = SecondaryBrush() });
    }

    /// <summary>节点压力迷你趋势线（旧→新，横向 72px 纵向 0~100 满刻度）；样本不足两点时不画。</summary>
    private static Polyline? BuildTrendLine(IReadOnlyDictionary<string, List<double>> history, string nodeId)
    {
        if (!history.TryGetValue(nodeId, out var hist) || hist.Count < 2) return null;
        const double w = 72, h = 18;
        var pc = new PointCollection();
        int cnt = hist.Count;
        for (int i = 0; i < cnt; i++)
        {
            double x = i * w / (cnt - 1);
            double y = h - Math.Clamp(hist[i], 0, 100) / 100.0 * h;
            pc.Add(new Windows.Foundation.Point(x, y));
        }
        return new Polyline
        {
            Points = pc,
            Width = w,
            Height = h,
            StrokeThickness = 1.2,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 120, 144, 156)),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void RenderClients(MasterSnapshot snap)
    {
        // 签名一致跳过重建：列表不再每 2 秒闪动/滚动归零/选中被打断（快照没变就别动 UI）；
        // 心跳年龄只对离线项展示且必须随时间刷新，在线项不计入（否则签名永变，跳过失效）
        var sig = string.Join("\n", snap.Clients.Select(c =>
            $"{c.ClientId}|{c.Online}|{c.Link}|{c.ServerId}|{c.RouterId}|{c.Priority}|{c.Pinned}" +
            $"|{c.ThrottleKbps}|{c.RttMasterMs:F0}|{c.RxKbps:F0}|{c.TxKbps:F0}" +
            (c.Online ? "" : $"|{c.LastAgeSec:F0}")));
        if (sig == _clientsSignature) return;
        _clientsSignature = sig;

        _clientRows.Clear();
        _clientList.Items.Clear();
        int restore = -1;
        foreach (var c in snap.Clients)
        {
            _clientRows.Add(c);
            if (string.Equals(c.ClientId, _selectedClientId, StringComparison.Ordinal)) restore = _clientRows.Count - 1;
            _clientList.Items.Add(
                $"{(c.Online ? "●" : "○")} {c.Hostname} · {c.Ip}"
                + (c.Link.Length > 0 ? $" · {LinkName(c.Link)}" : "")
                + $" · → {OrDash(c.ServerId)}（经 {OrDash(c.RouterId)}）"
                + $" · {GalaDispatcher.PriorityNames[Math.Clamp(c.Priority, 0, 2)]}优先"
                + (c.Pinned ? " · 手动锁定" : "")
                + (c.ThrottleKbps > 0 ? $" · 限速 {c.ThrottleKbps} kbps" : "")
                + (c.Online
                    ? $" · RTT {FmtMs(c.RttMasterMs)} · ↑{c.TxKbps:F0} ↓{c.RxKbps:F0} kbps"
                    : $" · 离线（最后心跳 {FmtAge(c.LastAgeSec)}）"));
        }
        _clientList.SelectedIndex = restore;
    }

    // ================= 拓扑绘制 =================

    private void DrawTopology(MasterSnapshot snap)
    {
        // 签名一致跳过重绘：悬停提示不再每 2 秒被清空重建打断（同一画面稳定展示）
        var sig = string.Join("\n", snap.Nodes.Select(n => $"{n.Id}|{n.Down}|{n.Pressure:F0}|{n.RttMs:F0}"))
            + "||cl:" + string.Join(";", snap.Clients.Select(c => $"{c.ClientId}|{c.Online}|{c.Link}|{c.ServerId}"))
            + "||lk:" + string.Join(";", _owner.Config.Links.Select(l => $"{l.From}>{l.To}:{l.CapacityMbps:F0}"))
            + "||seg:" + string.Join(";", _owner.Config.Nodes.Select(n => $"{n.Id}:{string.Join(",", n.Segments)}"));
        if (sig == _topologySignature) return;
        _topologySignature = sig;

        _topology.Children.Clear();
        var nodes = snap.Nodes;
        if (nodes.Count == 0)
        {
            _topology.Children.Add(new TextBlock
            {
                Text = "拓扑为空：请编辑 gala-topology.json 加入真实节点后重载。",
                FontSize = 13,
                Margin = new Thickness(12, 12, 0, 0)
            });
            return;
        }

        // ---- 自动分层布局（配置了 x/y 的节点优先）----
        // 行序：主控 → 出口/核心路由 → 交换机（两行，大拓扑如 40 台接入） → 服务器 → 终端
        var cfg = _owner.Config;
        var pos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var routers = nodes.Where(n => n.Type == "router").ToList();
        var servers = nodes.Where(n => n.Type == "server").ToList();
        var masters = nodes.Where(n => n.Type == "master").ToList();
        var switches = nodes.Where(n => n.Type == "switch").ToList();
        void Place(List<NodeSnap> list, double y)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                var manual = cfg.Nodes.FirstOrDefault(c => string.Equals(c.Id, n.Id, StringComparison.OrdinalIgnoreCase));
                pos[n.Id] = manual is { X: not null, Y: not null }
                    ? (manual.X!.Value * TopologyWidth, manual.Y!.Value * TopologyHeight)
                    : (TopologyWidth * (i + 1) / (list.Count + 1), TopologyHeight * y);
            }
        }
        Place(masters, 0.08);
        Place(routers, 0.30);
        int swHalf = (switches.Count + 1) / 2;
        Place(switches.GetRange(0, swHalf), 0.48);
        Place(switches.GetRange(swHalf, switches.Count - swHalf), 0.58);
        Place(servers, 0.76);

        // 客户端小点：真实终端动态挂载——优先按交换机 segments 挂到所属网段接入交换机下，
        // 段未配交换机时挂其路由节点；都缺则排底部散行。逐台绘制，悬停看详情
        var looseClients = new List<ClientSnap>();
        var mounts = new Dictionary<string, List<ClientSnap>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in snap.Clients)
        {
            string mountId = ResolveMount(c);
            if (mountId.Length == 0 || !nodes.Any(n => n.Id == mountId))
            {
                looseClients.Add(c);
                continue;
            }
            if (!mounts.TryGetValue(mountId, out var list)) mounts[mountId] = list = new List<ClientSnap>();
            list.Add(c);
        }
        // 在线优先排前：密集组里离线终端不挡在线终端的视野
        foreach (var list in mounts.Values)
            list.Sort((a, b) => (b.Online ? 1 : 0).CompareTo(a.Online ? 1 : 0));

        // ---- 链路 ----
        foreach (var link in cfg.Links)
        {
            if (!pos.TryGetValue(link.From, out var a) || !pos.TryGetValue(link.To, out var b)) continue;
            var na = nodes.FirstOrDefault(n => n.Id == link.From);
            var nb = nodes.FirstOrDefault(n => n.Id == link.To);
            double pressure = Math.Max(na?.Pressure ?? -1, nb?.Pressure ?? -1);
            bool down = na?.Down == true || nb?.Down == true;
            AddLine(a, b, down ? Color.FromArgb(255, 130, 130, 130) : PressureColor(pressure), 2.5);
        }
        // master → router 兜底连线（未配置 links 也能看出接入关系）
        foreach (var m in masters)
        {
            if (!pos.TryGetValue(m.Id, out var mp)) continue;
            foreach (var r in routers)
            {
                if (!pos.TryGetValue(r.Id, out var rp)) continue;
                bool exists = cfg.Links.Any(l =>
                    (l.From == m.Id && l.To == r.Id) || (l.From == r.Id && l.To == m.Id));
                if (!exists) AddLine(mp, rp, PressureColor(r.Pressure), 2);
            }
        }
        // 挂载点（路由/交换机） → 其动态注册终端：真实 PC 逐台绘制（网线蓝 / 直连 WiFi 紫 /
        // 离线灰），奇偶错行防密集拓扑（40 台接入交换机）相邻组互相压盖
        int mountIdx = 0;
        foreach (var kv in mounts)
        {
            if (!pos.TryGetValue(kv.Key, out var mp)) { looseClients.AddRange(kv.Value); continue; }
            var list = kv.Value;
            int n = list.Count;
            double step = n > 1 ? Math.Max(5.0, Math.Min(14.0, 60.0 / (n - 1))) : 14.0;
            double cy = mp.Y + (mountIdx % 2 == 0 ? 26 : 38);
            for (int j = 0; j < n; j++)
            {
                var c = list[j];
                double cx = mp.X - (n - 1) * step / 2.0 + j * step;
                pos[c.ClientId] = (cx, cy);
                AddLine(mp, (cx, cy), Color.FromArgb(255, 176, 190, 197), 1);
            }
            mountIdx++;
        }
        for (int j = 0; j < looseClients.Count; j++)
        {
            var c = looseClients[j];
            pos[c.ClientId] = (TopologyWidth * (j + 1) / (looseClients.Count + 1), TopologyHeight * 0.92);
        }

        // ---- 节点 ----
        foreach (var n in nodes)
        {
            if (!pos.TryGetValue(n.Id, out var p)) continue;
            AddNode(p.X, p.Y, n, n.Type == "master" ? 34 : n.Type == "switch" ? 20 : 28);
        }
        // ---- 动态注册的真实终端（逐台，悬停看主机名/地址/接入方式/线路）----
        foreach (var c in snap.Clients)
        {
            if (!pos.TryGetValue(c.ClientId, out var p)) continue;
            double size = c.Online ? 9 : 7;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(ClientColor(c)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 1
            };
            ToolTipService.SetToolTip(dot,
                $"{c.Hostname} · {c.Ip}\n{LinkName(c.Link)} · → {OrDash(c.ServerId)} · " +
                (c.Online ? $"RTT {FmtMs(c.RttMasterMs)}" : "离线"));
            _topology.Children.Add(dot);
            Canvas.SetLeft(dot, p.X - size / 2);
            Canvas.SetTop(dot, p.Y - size / 2);
        }
    }

    /// <summary>
    /// 终端挂载点：所属网段配了接入交换机（segments 八点界命中，最长前缀优先）挂交换机，
    /// 否则挂其路由节点（viaRouter）；都缺返回空（排底部散行）。
    /// </summary>
    private string ResolveMount(ClientSnap c)
    {
        try
        {
            string bestSeg = "";
            GalaNodeConfig? bestSw = null;
            foreach (var n in _owner.Config.Nodes)
            {
                if (n.Type != "switch") continue;
                foreach (var seg in n.Segments)
                {
                    if (GalaNet.MatchesPrefix(c.Ip, seg) && seg.Length > bestSeg.Length)
                    {
                        bestSeg = seg;
                        bestSw = n;
                    }
                }
            }
            return bestSw?.Id ?? c.RouterId;
        }
        catch { return c.RouterId; }
    }

    /// <summary>终端小点配色：离线灰 / 直连 WiFi 紫 / 网线蓝 / 未上报蓝灰。</summary>
    private static Color ClientColor(ClientSnap c) => !c.Online ? Color.FromArgb(255, 176, 176, 176)
        : c.Link == "wifi" ? Color.FromArgb(255, 171, 71, 188)
        : c.Link == "ethernet" ? Color.FromArgb(255, 66, 165, 245)
        : Color.FromArgb(255, 120, 144, 156);

    private static string LinkName(string link) => link switch
    {
        "wifi" => "直连 WiFi",
        "ethernet" => "网线",
        _ => "接入方式未上报"
    };

    private void AddLine((double X, double Y) a, (double X, double Y) b, Color color, double thickness)
    {
        var line = new Line
        {
            X1 = a.X,
            Y1 = a.Y,
            X2 = b.X,
            Y2 = b.Y,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness
        };
        _topology.Children.Add(line);
    }

    private void AddNode(double x, double y, NodeSnap n, double size)
    {
        var ellipse = new Ellipse
        {
            Width = size,
            Height = size,
            // switch 无压力分（-1），存活时用青绿色与路由/服务器压力色区分；失联同样灰显
            Fill = new SolidColorBrush(n.Down ? Color.FromArgb(255, 130, 130, 130)
                : n.Type == "switch" ? Color.FromArgb(255, 77, 182, 172)
                : PressureColor(n.Pressure)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 2
        };
        ToolTipService.SetToolTip(ellipse,
            $"{n.Label} · {n.Ip} · {(n.Down ? "失联" : $"RTT {FmtMs(n.RttMs)}")}"
            + (n.GatewayTotal > 1 ? $"\n网关 {n.GatewayAlive}/{n.GatewayTotal} 可达" : ""));
        _topology.Children.Add(ellipse);
        Canvas.SetLeft(ellipse, x - size / 2);
        Canvas.SetTop(ellipse, y - size / 2);

        var label = new TextBlock
        {
            Text = n.Type == "router" || n.Type == "server"
                ? $"{n.Label} {n.Pressure:F0}"
                : n.Label,
            FontSize = n.Type == "switch" ? 9 : 11,
            TextWrapping = TextWrapping.NoWrap
        };
        _topology.Children.Add(label);
        Canvas.SetLeft(label, x - 60);
        // switch 标签画在节点上方：下方空间留给动态挂载的真实终端小点
        Canvas.SetTop(label, n.Type == "switch" ? y - size / 2 - 16 : y + size / 2 + 2);
        label.MinWidth = 120;
        label.TextAlignment = Microsoft.UI.Text.TextAlignment.Center;
    }

    // ================= 小工具 =================

    /// <summary>拓扑配置变化时重建「切换线路」下拉（签名比对避免打断用户当前选择）。</summary>
    private void SyncRouteOptions()
    {
        try
        {
            var sig = string.Join(",", _owner.Config.Nodes
                .Where(n => n.Type == "server")
                .Select(n => n.Id + ":" + n.Ip));
            if (sig == _routeSignature) return;
            _routeSignature = sig;
            var keep = _routeBox.SelectedIndex;
            _routeBox.Items.Clear();
            _serverIds.Clear();
            foreach (var n in _owner.Config.Nodes.Where(n => n.Type == "server"))
            {
                _serverIds.Add(n.Id);
                _routeBox.Items.Add($"{n.Label}（{n.Ip}）");
            }
            _routeBox.SelectedIndex = keep >= 0 && keep < _serverIds.Count ? keep : (_serverIds.Count > 0 ? 0 : -1);
        }
        catch { }
    }

    /// <summary>面板重建后把开关同步到实际服务状态（重开面板/插件自动重启后不再误导）。</summary>
    private void SyncSwitchStates()
    {
        try
        {
            var master = _owner.EnsureMaster();
            if (master.IsRunning != _masterSwitch.IsOn)
            {
                _suspendSwitch = true;
                _masterSwitch.IsOn = master.IsRunning;
                _suspendSwitch = false;
            }
        }
        catch { }
    }

    private static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string FmtMs(double v) => v < 0 ? "—" : $"{v:F0}ms";

    /// <summary>心跳年龄人话化：离线终端判断“刚掉还是早掉”。</summary>
    private static string FmtAge(double sec) => sec < 90 ? $"{Math.Max(0, sec):F0} 秒前" : $"{sec / 60:F0} 分钟前";

    private static Color PressureColor(double p) => p switch
    {
        < 0 => Color.FromArgb(255, 158, 158, 158),
        < 40 => Color.FromArgb(255, 76, 175, 80),
        < 70 => Color.FromArgb(255, 245, 166, 35),
        _ => Color.FromArgb(255, 229, 57, 53)
    };

    private static Brush SecondaryBrush() => new SolidColorBrush(Color.FromArgb(255, 110, 118, 128));
}

/// <summary>Grid.Children.Add 扩展（带行/列定位），减少样板。</summary>
internal static class GalaMasterPanelExtensions
{
    public static void Add(this UIElementCollection children, UIElement element, int row, int col)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
        children.Add(element);
    }
}
