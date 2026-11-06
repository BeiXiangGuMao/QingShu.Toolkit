# 元旦晚会智能分流（Gala）：主控端 / 服务端分离部署

主控端与服务端（被控终端）拆分为**两个独立插件**——分别构建、分别签名、分别启用/停用；
亦可进一步发布为**两个不依赖工具箱的独立控制台程序**。四个工程以 Compile 链接共享
`core/Gala/` 同一份核心源码（单一事实源），协议帧、拓扑配置、调度语义完全互通。

| 形态 | 工程（插件目录名 = manifest.id） | 运行位置 | 一句话说明 |
| --- | --- | --- | --- |
| 主控端插件 | `qingshu.galamaster`（QingShu.Plugin.GalaMaster） | 礼堂控制机（装工具箱） | 拓扑实时画布（压力着色 + 趋势线）+ 远程操控 + 调度参数热调 |
| 服务端插件 | `qingshu.galaagent`（QingShu.Plugin.GalaAgent） | 舞台/投票/直播终端（装工具箱） | 常驻连接主控、自动重连上报、执行指令、assignment.json 落盘 |
| 独立主控端 | `QingShu.GalaMaster`（控制台） | 任意常驻机器 | 免装工具箱；命令行全景 / 操控 / 热调 / watch 态势 |
| 独立服务端 | `QingShu.GalaAgent`（控制台） | 各节点机 | 单文件常驻，免装工具箱；gala-agent.json 配置自动重连 |

> **由旧版捆绑插件迁移**：旧 `qingshu.galadispatch`（主控/客户端同插件切换角色）已退役——
> 从 `plugins\` 目录删除该文件夹，按 §1 改装两个新插件即可；已编辑过的 `gala-topology.json`
> 可原样拷入 `plugins-data\qingshu.galamaster\`，连接参数填入服务端面板一次即持久化。

共享源码布局（改核心 = 四个形态同时生效）：

```
plugins-sdk/gala-dispatch/
  core/Gala/                      ← 主控逻辑 / Agent 逻辑 / 模型·协议帧 / 压力模型（UI 无关）
  QingShu.Plugin.GalaMaster/      ← 主控端插件（核心链接 + 面板 + 插件类）
  QingShu.Plugin.GalaAgent/       ← 服务端插件（核心链接 + 状态面板 + 插件类）
  QingShu.GalaMaster/             ← 独立主控端（控制台，核心链接）
  QingShu.GalaAgent/              ← 独立服务端（控制台，核心链接）
  tools/assign-executor.ps1       ← assignment.json 消费端示例
```

## 1. 构建与签名（两个插件分别执行）

```powershell
# 0) 先构建宿主（或直接使用已发布目录，设 QINGSHU_HOME 指向它）
$env:QINGSHU_HOME = "C:\Tools\QingShu.Toolkit"

# 1) 构建两个插件
dotnet build plugins-sdk/gala-dispatch/QingShu.Plugin.GalaMaster -c Release
dotnet build plugins-sdk/gala-dispatch/QingShu.Plugin.GalaAgent  -c Release

# 2) 组装主控端插件目录（目录名必须 = plugin.json 的 id）
$qm = "$env:QINGSHU_HOME\plugins\qingshu.galamaster"
New-Item -ItemType Directory -Force $qm | Out-Null
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaMaster/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaMaster.dll $qm\
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaMaster/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaMaster.pdb $qm\   # 可选
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaMaster/plugin.json.template $qm\plugin.json

# 3) 组装服务端插件目录
$qa = "$env:QINGSHU_HOME\plugins\qingshu.galaagent"
New-Item -ItemType Directory -Force $qa | Out-Null
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaAgent/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaAgent.dll $qa\
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaAgent/bin/Release/net8.0-windows10.0.19041.0/QingShu.Plugin.GalaAgent.pdb $qa\      # 可选
Copy-Item plugins-sdk/gala-dispatch/QingShu.Plugin.GalaAgent/plugin.json.template $qa\plugin.json

# 4) 官方私钥分别签名（tools/plugin-sign.ps1，私钥离线保管，绝不入库）
powershell -ExecutionPolicy Bypass -File tools/plugin-sign.ps1 -PluginDir $qm
powershell -ExecutionPolicy Bypass -File tools/plugin-sign.ps1 -PluginDir $qa
```

宿主插件页「重新扫描」→ 两张卡片各自出现「官方签名」→ 分别「加载」。**控制机只装主控端
插件即可**；终端机只装服务端插件（同一台机器想自测回环可两个都装）。

## 2. 主控端：拓扑配置与综合调控

首次加载在 `%LOCALAPPDATA%\QingShu.Toolkit\plugins-data\qingshu.galamaster\gala-topology.json`
生成占位模板（两出口路由 × 两服务器示例）。字段：

```jsonc
{
  "settings": {
    "listenPort": 47816,            // 主控 TCP 监听端口（改了要同步改服务端）
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

改完在主控插件卡片点快捷动作「重载拓扑配置」即时生效（运行中的主控自动按新配置重启）。
服务端不进 `nodes`——上线自动注册，归属按其 IP 段匹配 `routes`。

**调度手感（周期/阈值/冷却/满载基数）不用改文件**：主控面板「调度参数」区直接改，
点「保存并生效」后下一探测周期自动生效（同一份运行配置热更新，主控不重启、终端不掉线），
同时写回本文件持久化。本文件的 `settings` 四项只作初始值/手工备份用途。

### 2.1 综合调控算法

1. **节点压力（0~100，每 tick 重算）**
   `压力 = 加权( RTT分 + 丢包分 + 并发分 + 带宽分 )`
   - RTT 分：ICMP 探测 EMA（服务器叠加 TCP 端口探测），40ms 记满；失联按打满；
   - 丢包分：最近 12 次探测失败率；
   - 并发分：该节点服务中的终端数 / `clientsPerNodeFull`；
   - 带宽分：服务流量之和 / 链路 `capacityMbps`。
2. **路径分** = `路由压力 × 50% + 服务器压力 × 50%`；候选 = 该终端 IP 段匹配的 `(router, server)` 全组合，取最优。
3. **滞回切换**：`当前路径分 − 最优路径分 ≥ 阈值` 且冷却已过才下发切换；**高优先级终端阈值减半、冷却减半**（压力起来先保舞台/直播这类关键终端）。
4. 全部决策写入事件日志（含数字理由），面板可见「为什么切」。
5. **压力趋势**：主控面板节点压力列表右侧带迷你趋势线（近 90 个探测周期 ≈ 5 分钟），
   压力是否在爬坡一眼可辨，比单点数字更适合现场决策。

### 2.2 远程操控项

| 操控 | 说明 |
| --- | --- |
| 切换线路 | 选中终端 → 选目标服务器 → 下发 switch 指令，终端探测新服务器并更新本地分配 |
| 设优先级 | 高（阈值/冷却减半，压力起来先保）/ 中 / 低；影响自动调度手感 |
| 限速标记 | 下发 kbps 值，终端落盘 assignment.json 供本机限速执行器消费；空/0 = 解除 |
| 即时采集 | 命令终端立即上报一轮指标 |

离线超过 30 分钟的终端会话自动清理（重连保分配的语义保留 30 分钟，防临时终端无限积累，清理有事件留痕）。

## 3. 服务端：连接与消费

- **连接**：服务端面板填主控 IP / 端口 / 配对码 → 打开「连接主控端」。成功一次即写入
  `plugins-data\qingshu.galaagent\gala-agent.json`，此后**随插件加载自动重连**（常驻语义，
  终端机重启工具箱后无需任何手动操作）；「断开连接」只停本次会话，清空主控 IP 再连一次才撤销自动重连。
- **行为**：断线 3 秒重连；周期上报 RTT / 丢包 / 收发速率 / 进程 CPU；执行主控指令。
- **消费**：当前分配落盘 `plugins-data\qingshu.galaagent\assignment.json`，供本机执行器读取。

`tools/assign-executor.ps1` 是 assignment.json 的最小可运行消费端示例（默认路径即服务端插件数据目录）：

```powershell
# 仅展示模式：轮询打印当前分配（服务器/路由/优先级/限速标记）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1

# 应用模式：把限速标记转成本机 Windows QoS 策略（需管理员；标记 0 自动解除）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1 -Apply
```

真实消费端（路由器 API / 代理切换 / 专用限速工具）参照其「读文件 → 差异化执行」骨架替换执行段即可。

## 4. 现场部署清单

1. 控制机与全部终端在同一城域网（各 IP 段互通），主控 `gala-topology.json` 配好 `routes` 段前缀。
2. 控制机装主控端插件；终端机装服务端插件（终端不装工具箱时改用独立服务端程序，见 §5.3）。
3. 首次启动主控端，Windows 防火墙会弹「允许访问」——勾选**专用网络**允许（TCP 47816 入站）。也可提前放行：
   `netsh advfirewall firewall add rule name="QSTK-Gala" dir=in action=allow protocol=TCP localport=47816`
4. 主控面板把关键终端（舞台/直播）设为「高」优先级。
5. 演出中只看拓扑与事件日志；需要人工干预时选中终端 → 切换线路 / 限速标记。
6. 事后复盘：主控插件「导出主控事件日志」、服务端插件「导出客户端事件日志」，各自写出含全部
   调度决策与数字理由的 txt。

## 5. 独立部署形态（免装工具箱）

插件之外，两个控制台工程发布为自包含单文件 exe，供不装工具箱的机器使用：

### 5.1 发布命令

```powershell
# 独立主控端（自包含单文件，拷到主控机即可运行）
dotnet publish plugins-sdk/gala-dispatch/QingShu.GalaMaster/QingShu.GalaMaster.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 独立服务端（自包含单文件，复制一个 exe 到每个节点机）
dotnet publish plugins-sdk/gala-dispatch/QingShu.GalaAgent/QingShu.GalaAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物在各自 `bin/Release/net8.0/publish/`。若节点机已确认装有 .NET 8 运行时，可去掉
`--self-contained true` 减小体积。独立程序与插件配置、协议完全互通：拓扑文件同一格式，
配对码 / 准入段 / 调度参数语义一致。

### 5.2 独立主控端（QingShu.GalaMaster）

- 拓扑配置：exe 同目录 `gala-topology.json`，格式与主控插件数据目录完全一致，可互相拷贝；
  首次运行自动生成模板并退出，填好真实网络资料后再启动。`--config 路径` 可指定其他位置。
- 交互命令：`status / nodes / clients / events`（全景与事件）、`assign / priority / throttle / ping`
  （与面板同语义的远程操控）、`params / setparam`（调度参数热调，下一探测周期生效并写回配置文件）、
  `watch on|off`（每 10 秒一行态势摘要）、`quit`。
- 无人值守：`--watch` 启动即开态势摘要；输出重定向（计划任务）时自动跳过交互输入，
  持续常驻，Ctrl+C 或结束进程优雅退出。
- 端口被占等启动失败会给出明确错误文案；首次连接的防火墙放行同 §4 第 3 条。

### 5.3 独立服务端（QingShu.GalaAgent）

- 配置：exe 同目录 `gala-agent.json`（首次运行自动生成模板）：`masterHost`（主控机 IP，必填）
  / `port` / `pairingCode` / `dataFolder`；命令行参数优先：
  `--master 主控IP[:端口] --port 端口 --pairing 配对码 --data 数据目录`。
- 行为与服务端插件一致：断线 3 秒重连、周期上报指标、执行主控指令、当前分配落盘
  `assignment.json`（供 §3 执行器消费）。
- 交互命令：`status / events / quit`；重定向输出时同样保持常驻。

## 6. 边界说明

- 插件下发的是「终端应使用哪台服务器/线路」的分配与标记；实际网络路径由校园网策略/路由侧生效。
  `assignment.json` 就是为这类执行器准备的对接点（真实拓扑资料到位后可加路由器 API 对接）。
- 协议为明文 JSON over TCP，靠**内网准入 + 配对码**控制接入，不含公网对抗设计。
- 客户端指标中的「RTT/丢包」是终端到主控的 ICMP 探测结果；禁 ICMP 的网段内探测显示为「—」，调度退化为并发/带宽分量主导。
- 卸载任一插件只停自己的服务：卸主控 = 停监听（各终端自动重连等待），卸服务端 = 断本机连接；
  宿主统一收尾，面板自动收起。两个插件互不依赖，加载顺序无关。
