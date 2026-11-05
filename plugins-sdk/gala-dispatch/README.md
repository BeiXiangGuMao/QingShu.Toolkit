# 元旦晚会智能分流插件（GalaDispatch）

主控端 + 客户端的晚会现场网络分流调度插件：

- **主控端**（礼堂控制台运行青枢工具箱 + 本插件）：监听内网 TCP 端口，汇聚各终端上报的链路指标；对「路由压力 × 服务器压力」加权综合评分，按滞回策略远程切换各终端线路；面板实时绘制拓扑（节点压力着色 + 客户端流量小圆），支持远程操控（切换线路 / 优先级 / 即时采集）。
- **客户端**（舞台 / 投票 / 直播终端运行同一插件，切到「客户端」角色）：自动重连主控，周期上报 RTT / 丢包 / 收发速率 / 进程 CPU，执行主控指令；当前分配落盘 `assignment.json` 供本机执行器消费。
- **限城域网内网使用**：会话准入按 IP 段前缀白名单（默认 `10. / 172.16. / 192.168. / 100.64.`，CGNAT 段包含在内），可选配对码防误连。**切勿把监听端口映射到公网。**

## 1. 构建与签名

```powershell
# 1) 先构建宿主（或直接使用已发布目录，设 QINGSHU_HOME 指向它）
$env:QINGSHU_HOME = "C:\Tools\QingShu.Toolkit"

# 2) 构建插件
dotnet build plugins-sdk/gala-dispatch/QingShu.Plugin.GalaDispatch -c Release

# 3) 组装插件目录（目录名必须 = plugin.json 的 id）
$qdir = "$env:QINGSHU_HOME\plugins\qingshu.galadispatch"
New-Item -ItemType Directory -Force $qdir | Out-Null
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaDispatch/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaDispatch.dll $qdir\
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaDispatch/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaDispatch.pdb $qdir\   # 可选
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaDispatch/plugin.json.template $qdir\plugin.json

# 4) 官方私钥签名（tools/plugin-sign.ps1，私钥离线保管，绝不入库）
powershell -ExecutionPolicy Bypass -File tools/plugin-sign.ps1 -PluginDir $qdir
```

宿主插件页「重新扫描」→ 卡片出现「官方签名」→「加载」→「面板」打开主控台。

## 2. 拓扑配置（等真实网络资料到位后编辑）

首次加载会在 `%LOCALAPPDATA%\QingShu.Toolkit\plugins-data\qingshu.galadispatch\gala-topology.json`
生成占位模板（两出口路由 × 两服务器示例）。字段：

```jsonc
{
  "settings": {
    "listenPort": 47816,            // 主控 TCP 监听端口（改了要同步改客户端）
    "tickSeconds": 3,               // 探测/调度周期（秒）
    "allowedPrefixes": ["10.", "172.16.", "192.168.", "100.64."],  // 内网准入段
    "pairingCode": "",              // 配对码，空 = 不校验；现场建议设置
    "switchThreshold": 12,          // 切换阈值（路径分差），防抖动
    "switchCooldownSeconds": 30,    // 同一终端两次切换最小间隔
    "clientsPerNodeFull": 10,       // 并发满载基数（负载分）
    "weightRtt": 0.35, "weightLoss": 0.25, "weightLoad": 0.20, "weightBandwidth": 0.20
  },
  "nodes": [                        // 固定设施：master / router / server / switch
    { "id": "r1", "type": "router", "label": "出口路由A", "ip": "10.10.0.254" },
    { "id": "s1", "type": "server", "label": "流媒体服务器", "ip": "10.20.0.10", "probePort": 8000 },
    { "id": "s2", "type": "server", "label": "互动服务器", "ip": "10.20.0.11",
      "x": 0.7, "y": 0.72 }         // x/y 可选：手工拓扑布局（0~1 归一化），缺省自动分层
  ],
  "links": [                        // 链路容量（带宽压力分用）
    { "from": "r1", "to": "s1", "capacityMbps": 1000 }
  ],
  "routes": [                       // 调度规则：某 IP 段经哪台路由、可落哪些服务器
    { "segmentPrefix": "10.10.", "viaRouter": "r1", "servers": ["s1", "s2"] }
  ]
}
```

改完在插件卡片点快捷动作「重载拓扑配置」即时生效（运行中的主控自动按新配置重启）。
客户端不进 `nodes`——上线自动注册，归属按其 IP 段匹配 `routes`。

## 3. 综合调控算法

1. **节点压力（0~100，每 tick 重算）**
   `压力 = 加权( RTT分 + 丢包分 + 并发分 + 带宽分 )`
   - RTT 分：ICMP 探测 EMA（服务器叠加 TCP 端口探测），40ms 记满；失联按打满；
   - 丢包分：最近 12 次探测失败率；
   - 并发分：该节点服务中的终端数 / `clientsPerNodeFull`；
   - 带宽分：服务流量之和 / 链路 `capacityMbps`。
2. **路径分** = `路由压力 × 50% + 服务器压力 × 50%`；候选 = 该终端 IP 段匹配的 `(router, server)` 全组合，取最优。
3. **滞回切换**：`当前路径分 − 最优路径分 ≥ 阈值` 且冷却已过才下发切换；**高优先级终端阈值减半、冷却减半**（压力起来先保舞台/直播这类关键终端）。
4. 全部决策写入事件日志（含数字理由），面板可见「为什么切」。

## 4. 现场部署清单

1. 控制台与全部终端在同一城域网（各 IP 段互通），配置好 `routes` 段前缀。
2. 控制台首次启动主控端，Windows 防火墙会弹「允许访问」——勾选**专用网络**允许（TCP 47816 入站）。也可提前放行：
   `netsh advfirewall firewall add rule name="QSTK-Gala" dir=in action=allow protocol=TCP localport=47816`
3. 终端打开工具箱 → 插件页 → 面板 → 客户端角色 → 填主控 IP → 连接。
4. 主控面板把关键终端设为「高」优先级。
5. 演出中只看拓扑与事件日志；需要人工干预时选中终端 → 切换线路。

## 5. 边界说明

- 插件下发的是「终端应使用哪台服务器/线路」的分配与标记；实际网络路径由校园网策略/路由侧生效。
  `assignment.json` 就是为这类执行器准备的对接点（真实拓扑资料到位后可加路由器 API 对接）。
- 协议为明文 JSON over TCP，靠**内网准入 + 配对码**控制接入，不含公网对抗设计。
- 客户端指标中的「RTT/丢包」是终端到主控的 ICMP 探测结果；禁 ICMP 的网段内探测显示为「—」，调度退化为并发/带宽分量主导。
- 卸载插件会停掉主控监听与客户端连接（宿主统一收尾，面板自动收起）。
