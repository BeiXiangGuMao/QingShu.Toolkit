using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace QingShu.Plugin.GalaDispatch.Gala;

/// <summary>
/// 元旦晚会智能分流面板（纯代码构建，零 XAML 编译依赖）：
/// 主控端 = 拓扑实时画布（节点压力着色 / 链路流量）+ 节点压力列表 + 客户端列表 + 远程操控 + 事件日志；
/// 客户端 = 主控地址/配对码 → 连接，显示当前分配、本地指标与指令记录。
/// 面板关闭（Unloaded）只停界面定时器；后台服务由插件 OnUnloadedAsync 统一收尾。
/// </summary>
public sealed class GalaPanelView : UserControl
{
    private const double TopologyWidth = 780;
    private const double TopologyHeight = 540;

    private readonly GalaDispatchPlugin _owner;
    private readonly DispatcherQueueTimer _timer;

    private readonly RadioButton _modeMaster;
    private readonly RadioButton _modeClient;
    private readonly ToggleSwitch _masterSwitch;
    private readonly ToggleSwitch _clientSwitch;
    private readonly TextBox _masterIpBox;
    private readonly TextBox _portBox;
    private readonly TextBox _pairingBox;
    private readonly TextBlock _statusText;
    private readonly StackPanel _masterOps;
    private readonly StackPanel _clientOps;
    private readonly Grid _masterPane;
    private readonly StackPanel _clientPane;
    private readonly Canvas _topology;
    private readonly StackPanel _serverPanel;
    private readonly ListView _clientList;
    private readonly ComboBox _routeBox;
    private readonly ComboBox _prioBox;
    private readonly TextBlock _eventText;
    private readonly TextBlock _agentAssignText;
    private readonly TextBlock _agentMetricsText;
    private readonly TextBlock _agentEventText;

    private readonly List<ClientSnap> _clientRows = new();
    private readonly List<string> _serverIds = new();
    private string _selectedClientId = "";
    private string _routeSignature = "";
    private bool _suspendSwitch;

    public GalaPanelView(GalaDispatchPlugin owner)
    {
        _owner = owner;
        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => { try { RefreshAll(); } catch { } };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();

        // ---------- 顶部：角色 + 操作 ----------
        _modeMaster = new RadioButton { Content = "主控端（礼堂控制台）", GroupName = "galaMode", IsChecked = true, MinWidth = 0 };
        _modeClient = new RadioButton { Content = "客户端（舞台/投票/直播终端）", GroupName = "galaMode", MinWidth = 0 };
        _modeMaster.Checked += (_, _) => SwitchMode(true);
        _modeClient.Checked += (_, _) => SwitchMode(false);

        _masterSwitch = new ToggleSwitch { OnContent = "主控运行中", OffContent = "启动主控端", IsOn = false, Margin = new Thickness(16, 0, 0, 0) };
        _masterSwitch.Toggled += async (_, _) =>
        {
            if (_suspendSwitch) return; // 状态同步触发，非用户操作
            await OnMasterToggledAsync();
        };

        _masterIpBox = new TextBox { PlaceholderText = "主控 IP，如 10.10.0.1", Width = 150 };
        _portBox = new TextBox { PlaceholderText = "端口", Text = "47816", Width = 72 };
        _pairingBox = new TextBox { PlaceholderText = "配对码（可空）", Width = 120, Margin = new Thickness(8, 0, 0, 0) };
        _clientSwitch = new ToggleSwitch { OnContent = "已连接/连接中", OffContent = "连接主控端", IsOn = false, Margin = new Thickness(16, 0, 0, 0) };
        _clientSwitch.Toggled += async (_, _) =>
        {
            if (_suspendSwitch) return;
            await OnClientToggledAsync();
        };

        var roleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        roleRow.Children.Add(new TextBlock { Text = "角色", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        roleRow.Children.Add(_modeMaster);
        roleRow.Children.Add(_modeClient);

        _masterOps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _masterOps.Children.Add(_masterSwitch);
        _masterOps.Children.Add(new TextBlock
        {
            Text = "监听端口/准入段/配对码在 gala-topology.json 配置（插件快捷动作可打开）",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380
        });

        _clientOps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _clientOps.Children.Add(_masterIpBox);
        _clientOps.Children.Add(_portBox);
        _clientOps.Children.Add(_pairingBox);
        _clientOps.Children.Add(_clientSwitch);

        _statusText = new TextBlock
        {
            Text = "就绪。主控端在本机启动监听；客户端填主控 IP 后连接。仅限城域网内网使用。",
            FontSize = 12,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        var topGrid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.Children.Add(roleRow, 0, 0);
        topGrid.Children.Add(_masterOps, 0, 1);
        topGrid.Children.Add(_clientOps, 0, 2);

        // ---------- 主控面板 ----------
        _topology = new Canvas { Width = TopologyWidth, Height = TopologyHeight };
        var topologyScroll = new ScrollViewer
        {
            Content = _topology,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var legend = new TextBlock
        {
            Text = "节点颜色 = 综合压力（绿 <40 / 黄 40-70 / 红 ≥70，灰 = 失联或非压力节点）；客户端小圆 = 在线终端；拓扑布局与链路来自 gala-topology.json。",
            FontSize = 11,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        _serverPanel = new StackPanel { Spacing = 4 };

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
        var pingBtn = new Button { Content = "即时采集", Padding = new Thickness(12, 4, 12, 4) };
        pingBtn.Click += (_, _) => OnPingNowClick();

        var opsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opsRow.Children.Add(_routeBox);
        opsRow.Children.Add(assignBtn);
        opsRow.Children.Add(_prioBox);
        opsRow.Children.Add(prioBtn);
        opsRow.Children.Add(pingBtn);

        _eventText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush() };
        var eventScroll = new ScrollViewer
        {
            Content = _eventText,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var rightCol = new StackPanel { Spacing = 8 };
        rightCol.Children.Add(new TextBlock { Text = "节点压力（探测 + 并发 + 带宽加权）", FontSize = 12.5 });
        rightCol.Children.Add(_serverPanel);
        rightCol.Children.Add(new TextBlock { Text = "客户端（点击选中后可远程操控）", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(_clientList);
        rightCol.Children.Add(new TextBlock { Text = "远程操控", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) });
        rightCol.Children.Add(opsRow);
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

        _masterPane = new Grid { Children = { masterGrid } };

        // ---------- 客户端面板 ----------
        _agentAssignText = BodyText("当前分配：—");
        _agentMetricsText = BodyText("本地指标：—");
        _agentEventText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush() };
        var agentEventScroll = new ScrollViewer { Content = _agentEventText, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _clientPane = new StackPanel { Spacing = 8 };
        _clientPane.Children.Add(_agentAssignText);
        _agentMetricsText.FontSize = 12.5;
        _clientPane.Children.Add(_agentMetricsText);
        _clientPane.Children.Add(new TextBlock
        {
            Text = "当前分配会落盘 assignment.json（路径见下方），供本机其它执行器（路由/限速脚本）读取消费；真正的线路切换由校园网策略在路由侧生效。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        _clientPane.Children.Add(agentEventScroll);

        // ---------- 组装 ----------
        var root = new Grid { RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(topGrid, 0, 0);
        root.Children.Add(_masterPane, 0, 1);
        root.Children.Add(_clientPane, 0, 1);

        Content = root;
        SwitchMode(true);
        RefreshAll();
    }

    // ================= 角色/服务开关 =================

    private void SwitchMode(bool master)
    {
        _masterPane.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        _clientPane.Visibility = master ? Visibility.Collapsed : Visibility.Visible;
        _masterOps.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        _clientOps.Visibility = master ? Visibility.Collapsed : Visibility.Visible;
        _statusText.Text = master
            ? "主控端：点「启动主控端」开始监听（端口/准入段在 gala-topology.json）。各 IP 段客户端可通，仅限城域网。"
            : "客户端：填主控 IP（同网可达）后打开「连接主控端」。";
    }

    private async System.Threading.Tasks.Task OnMasterToggledAsync()
    {
        try
        {
            if (_masterSwitch.IsOn)
            {
                var master = _owner.EnsureMaster();
                master.Start(); // 端口占用等异常抛出 → 下方 catch 显示
                _statusText.Text = $"主控端监听中：0.0.0.0:{master.Port}。等待客户端接入…";
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

    private async System.Threading.Tasks.Task OnClientToggledAsync()
    {
        try
        {
            if (_clientSwitch.IsOn)
            {
                var ip = _masterIpBox.Text.Trim();
                if (ip.Length == 0) throw new InvalidOperationException("请先填写主控 IP");
                int port = int.TryParse(_portBox.Text.Trim(), out var p) ? p : 47816;
                var agent = _owner.EnsureAgent();
                agent.Start(ip, port, _pairingBox.Text.Trim());
                _statusText.Text = $"客户端已启动：目标 {_masterIpBox.Text.Trim()}:{port}，连接中…";
            }
            else
            {
                _statusText.Text = "客户端已断开。";
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await _owner.EnsureAgent().StopAsync(); } catch { }
                });
            }
        }
        catch (Exception ex)
        {
            _clientSwitch.IsOn = false;
            _statusText.Text = $"启动客户端失败：{ex.Message}";
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
                ? $"切换指令已下发：{_selectedClientId} → {_serverIds[idx]}。"
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

    // ================= 定时刷新 =================

    private void RefreshAll()
    {
        try
        {
            SyncSwitchStates();
            SyncRouteOptions();
            var master = _owner.EnsureMaster();
            var snap = master.Snapshot();
            RenderServers(snap);
            RenderClients(snap);
            _eventText.Text = snap.Events.Count > 0 ? string.Join("\n", snap.Events) : "暂无事件。";
            DrawTopology(snap);
        }
        catch { }

        try
        {
            var agent = _owner.EnsureAgent();
            var a = agent.Snapshot();
            if (a.Running)
            {
                _agentAssignText.Text = $"当前分配：服务器 {OrDash(a.ServerId)}（{OrDash(a.ServerIp)}）· 经 {OrDash(a.RouterId)}"
                    + $" · 优先级 {GalaDispatcher.PriorityNames[Math.Clamp(a.Priority, 0, 2)]}"
                    + (a.ThrottleKbps > 0 ? $" · 限速标记 {a.ThrottleKbps} kbps" : "");
                _agentMetricsText.Text = a.Connected
                    ? $"已连接主控 {a.MasterHost}:{a.Port} · 到主控 RTT {FmtMs(a.RttMasterMs)} · 丢包 {a.LossPct:F0}% · 到服务器 RTT {FmtMs(a.RttServerMs)}"
                      + $" · 收 {a.RxKbps:F0} kbps · 发 {a.TxKbps:F0} kbps · 本进程 CPU {a.AppCpu:F0}%"
                    : $"未连接主控 {a.MasterHost}:{a.Port}（自动重连中）";
                _agentEventText.Text = a.Events.Count > 0 ? string.Join("\n", a.Events.TakeLast(60)) : "暂无事件。";
            }
            else
            {
                _agentAssignText.Text = "当前分配：—（客户端未启动）";
                _agentMetricsText.Text = "本地指标：—";
            }
        }
        catch { }
    }

    private void RenderServers(MasterSnapshot snap)
    {
        _serverPanel.Children.Clear();
        foreach (var n in snap.Nodes.Where(n => n.Type is "router" or "server").OrderBy(n => n.Type).ThenBy(n => n.Id))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(n.Down ? Color.FromArgb(255, 130, 130, 130) : PressureColor(n.Pressure)),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Text = n.Down
                    ? $"{n.Label} · {n.Ip} · 失联（压力按打满计）"
                    : $"{n.Label} · {n.Ip} · 压力 {n.Pressure:F0} · RTT {FmtMs(n.RttMs)} · 丢包 {n.LossPct:F0}% · 服务 {n.ServedClients} 台"
            });
            _serverPanel.Children.Add(row);
        }
        if (_serverPanel.Children.Count == 0)
            _serverPanel.Children.Add(new TextBlock { Text = "gala-topology.json 中还没有 router/server 节点。", FontSize = 11.5, Foreground = SecondaryBrush() });
    }

    private void RenderClients(MasterSnapshot snap)
    {
        _clientRows.Clear();
        _clientList.Items.Clear();
        int restore = -1;
        foreach (var c in snap.Clients)
        {
            _clientRows.Add(c);
            if (string.Equals(c.ClientId, _selectedClientId, StringComparison.Ordinal)) restore = _clientRows.Count - 1;
            _clientList.Items.Add(
                $"{(c.Online ? "●" : "○")} {c.Hostname} · {c.Ip} · → {OrDash(c.ServerId)}（经 {OrDash(c.RouterId)}）"
                + $" · {GalaDispatcher.PriorityNames[Math.Clamp(c.Priority, 0, 2)]}优先"
                + (c.Online ? $" · RTT {FmtMs(c.RttMasterMs)} · ↑{c.TxKbps:F0} ↓{c.RxKbps:F0} kbps" : " · 离线"));
        }
        _clientList.SelectedIndex = restore;
    }

    // ================= 拓扑绘制 =================

    private void DrawTopology(MasterSnapshot snap)
    {
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
        var cfg = _owner.Config;
        var pos = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var routers = nodes.Where(n => n.Type == "router").ToList();
        var servers = nodes.Where(n => n.Type == "server").ToList();
        var masters = nodes.Where(n => n.Type == "master").ToList();
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
        Place(masters, 0.10);
        Place(routers, 0.42);
        Place(servers, 0.72);

        // 客户端小点：挂在所属路由下方
        var clientsByRouter = snap.Clients
            .Where(c => c.RouterId.Length > 0)
            .GroupBy(c => c.RouterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var looseClients = snap.Clients.Where(c => c.RouterId.Length == 0).ToList();

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
        // 路由 → 其客户端（细线，仅在线）
        foreach (var kv in clientsByRouter)
        {
            if (!pos.TryGetValue(kv.Key, out var rp)) continue;
            int k = kv.Value.Count;
            for (int j = 0; j < k; j++)
            {
                var c = kv.Value[j];
                double cx = rp.X + (j - (k - 1) / 2.0) * 54;
                double cy = TopologyHeight * 0.92;
                pos[c.ClientId] = (cx, cy);
                AddLine(rp, (cx, cy), Color.FromArgb(255, 176, 190, 197), 1);
            }
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
            AddNode(p.X, p.Y, n, n.Type == "master" ? 34 : 28);
        }
        foreach (var c in snap.Clients)
        {
            if (!pos.TryGetValue(c.ClientId, out var p)) continue;
            var dot = new Ellipse
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(c.Online
                    ? Color.FromArgb(255, 66, 165, 245)
                    : Color.FromArgb(255, 176, 176, 176)),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 1.5
            };
            _topology.Children.Add(dot);
            Canvas.SetLeft(dot, p.X - 7);
            Canvas.SetTop(dot, p.Y - 7);
            var tip = new TextBlock
            {
                Text = c.Hostname,
                FontSize = 10,
                Foreground = SecondaryBrush()
            };
            _topology.Children.Add(tip);
            Canvas.SetLeft(tip, p.X - 20);
            Canvas.SetTop(tip, p.Y + 8);
        }
    }

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
            Fill = new SolidColorBrush(n.Down ? Color.FromArgb(255, 130, 130, 130) : PressureColor(n.Pressure)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            StrokeThickness = 2
        };
        _topology.Children.Add(ellipse);
        Canvas.SetLeft(ellipse, x - size / 2);
        Canvas.SetTop(ellipse, y - size / 2);

        var label = new TextBlock
        {
            Text = n.Type == "router" || n.Type == "server"
                ? $"{n.Label} {n.Pressure:F0}"
                : n.Label,
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap
        };
        _topology.Children.Add(label);
        Canvas.SetLeft(label, x - 60);
        Canvas.SetTop(label, y + size / 2 + 2);
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
        try
        {
            var agent = _owner.EnsureAgent();
            if (agent.IsRunning != _clientSwitch.IsOn)
            {
                _suspendSwitch = true;
                _clientSwitch.IsOn = agent.IsRunning;
                _suspendSwitch = false;
            }
        }
        catch { }
    }

    private static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string FmtMs(double v) => v < 0 ? "—" : $"{v:F0}ms";

    private static Color PressureColor(double p) => p switch
    {
        < 0 => Color.FromArgb(255, 158, 158, 158),
        < 40 => Color.FromArgb(255, 76, 175, 80),
        < 70 => Color.FromArgb(255, 245, 166, 35),
        _ => Color.FromArgb(255, 229, 57, 53)
    };

    private static Brush SecondaryBrush() => new SolidColorBrush(Color.FromArgb(255, 110, 118, 128));

    private static TextBlock BodyText(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap
    };
}

/// <summary>Grid.Children.Add 扩展（带行/列定位），减少样板。</summary>
internal static class GalaPanelViewExtensions
{
    public static void Add(this UIElementCollection children, UIElement element, int row, int col)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
        children.Add(element);
    }
}
