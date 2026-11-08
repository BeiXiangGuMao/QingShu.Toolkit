using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using QingShu.Gala;

namespace QingShu.Plugin.GalaAgent;

/// <summary>
/// 服务端（被控终端）面板（纯代码构建，零 XAML 编译依赖）：
/// 主控地址/端口/配对码 → 连接；显示当前分配、本地指标与指令记录。
/// 连接参数会写入 gala-agent.json（随插件加载自动重连，常驻终端无需手动操作）。
/// 面板关闭（Unloaded）只停界面定时器；后台连接由插件 OnUnloadedAsync 统一收尾。
/// </summary>
public sealed class GalaAgentPanelView : UserControl
{
    private readonly GalaAgentPlugin _owner;
    private readonly DispatcherQueueTimer _timer;

    private readonly TextBox _masterIpBox;
    private readonly TextBox _portBox;
    private readonly TextBox _pairingBox;
    private readonly ToggleSwitch _clientSwitch;
    private readonly TextBlock _statusText;
    private readonly TextBlock _assignText;
    private readonly TextBlock _metricsText;
    private readonly TextBlock _eventText;
    private bool _suspendSwitch;

    public GalaAgentPanelView(GalaAgentPlugin owner)
    {
        _owner = owner;
        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => { try { RefreshAll(); } catch { } };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();

        // ---------- 顶部：连接参数 + 开关 ----------
        var (host, port, pairing) = _owner.CurrentConnection;
        _masterIpBox = new TextBox { PlaceholderText = "主控 IP，如 10.10.0.1", Width = 150, Text = host };
        _portBox = new TextBox { PlaceholderText = "端口", Text = port > 0 ? port.ToString() : "47816", Width = 72 };
        _pairingBox = new TextBox { PlaceholderText = "配对码（可空）", Width = 120, Margin = new Thickness(8, 0, 0, 0), Text = pairing };
        _clientSwitch = new ToggleSwitch { OnContent = "已连接/连接中", OffContent = "连接主控端", IsOn = false, Margin = new Thickness(16, 0, 0, 0) };
        _clientSwitch.Toggled += async (_, _) =>
        {
            if (_suspendSwitch) return;
            await OnClientToggledAsync();
        };

        var opsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opsRow.Children.Add(new TextBlock { Text = "客户端（舞台/投票/直播终端）", VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        opsRow.Children.Add(_masterIpBox);
        opsRow.Children.Add(_portBox);
        opsRow.Children.Add(_pairingBox);
        opsRow.Children.Add(_clientSwitch);

        _statusText = new TextBlock
        {
            Text = "就绪。填主控 IP 后打开「连接主控端」；成功连接一次即写入 gala-agent.json，此后随插件加载自动重连。",
            FontSize = 12,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        };

        // ---------- 状态区 ----------
        _assignText = BodyText("当前分配：—");
        _metricsText = BodyText("本地指标：—");
        _metricsText.FontSize = 12.5;
        _eventText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush() };
        var eventScroll = new ScrollViewer { Content = _eventText, Height = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(_assignText);
        body.Children.Add(_metricsText);
        body.Children.Add(new TextBlock
        {
            Text = "当前分配会落盘 assignment.json（插件快捷动作「打开数据目录」可达），供本机其它执行器（路由/限速脚本）读取消费；真正的线路切换由校园网策略在路由侧生效。",
            FontSize = 11.5,
            Foreground = SecondaryBrush(),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock { Text = "指令与连接记录", FontSize = 12.5 });
        body.Children.Add(eventScroll);

        // ---------- 组装（状态行独立成行，操作结果全程可见） ----------
        var root = new Grid { RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(opsRow, 0, 0);
        root.Children.Add(_statusText, 1, 0);
        root.Children.Add(body, 2, 0);

        Content = root;
        RefreshAll();
    }

    // ================= 连接开关 =================

    private async System.Threading.Tasks.Task OnClientToggledAsync()
    {
        try
        {
            if (_clientSwitch.IsOn)
            {
                var ip = _masterIpBox.Text.Trim();
                if (ip.Length == 0) throw new InvalidOperationException("请先填写主控 IP");
                int port = int.TryParse(_portBox.Text.Trim(), out var p) ? p : 47816;
                _statusText.Text = await _owner.ConnectAsync(ip, port, _pairingBox.Text.Trim());
            }
            else
            {
                _statusText.Text = await _owner.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            _clientSwitch.IsOn = false;
            _statusText.Text = $"启动客户端失败：{ex.Message}";
        }
    }

    // ================= 定时刷新 =================

    private void RefreshAll()
    {
        try
        {
            var agent = _owner.EnsureAgent();
            if (agent.IsRunning != _clientSwitch.IsOn)
            {
                _suspendSwitch = true;
                _clientSwitch.IsOn = agent.IsRunning;
                _suspendSwitch = false;
            }
            var a = agent.Snapshot();
            if (a.Running)
            {
                _assignText.Text = $"当前分配：服务器 {OrDash(a.ServerId)}（{OrDash(a.ServerIp)}）· 经 {OrDash(a.RouterId)}"
                    + $" · 优先级 {GalaDispatcher.PriorityNames[Math.Clamp(a.Priority, 0, 2)]}"
                    + (a.ThrottleKbps > 0 ? $" · 限速标记 {a.ThrottleKbps} kbps" : "");
                _metricsText.Text = a.Connected
                    ? $"已连接主控 {a.MasterHost}:{a.Port} · 到主控 RTT {FmtMs(a.RttMasterMs)} · 丢包 {a.LossPct:F0}% · 到服务器 RTT {FmtMs(a.RttServerMs)}"
                      + $" · 收 {a.RxKbps:F0} kbps · 发 {a.TxKbps:F0} kbps · 本进程 CPU {a.AppCpu:F0}%"
                    : $"未连接主控 {a.MasterHost}:{a.Port}（自动重连中，约 {a.ReconnectDelaySeconds} 秒后重试）";
                _eventText.Text = a.Events.Count > 0 ? string.Join("\n", a.Events.TakeLast(60)) : "暂无事件。";
            }
            else
            {
                _assignText.Text = "当前分配：—（客户端未启动）";
                _metricsText.Text = "本地指标：—";
                _eventText.Text = "暂无事件。";
            }
        }
        catch { }
    }

    // ================= 小工具 =================

    private static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string FmtMs(double v) => v < 0 ? "—" : $"{v:F0}ms";

    private static Brush SecondaryBrush() => new SolidColorBrush(Color.FromArgb(255, 110, 118, 128));

    private static TextBlock BodyText(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap
    };
}
