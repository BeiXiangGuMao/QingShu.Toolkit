# 元旦晚会智能分流插件（GalaDispatch）

主控端 + 客户端的晚会现场网络分流调度插件：

- **主控端**（礼堂控制台运行青枢工具箱 + 本插件）：监听内网 TCP 端口，汇聚各终端上报的链路指标；对「路由压力 × 服务器压力」加权综合评分，按滞回策略远程切换各终端线路；面板实时绘制拓扑（节点压力着色 + 客户端流量小圆），支持远程操控（切换线路 / 优先级 / 限速标记 / 即时采集）。
- **客户端**（舞台 / 投票 / 直播终端运行同一插件，切到「客户端」角色）：自动重连主控，周期上报 RTT / 丢包 / 收发速率 / 进程 CPU，执行主控指令；当前分配落盘 `assignment.json` 供本机执行器消费。
- **探测并行化**：所有节点每轮并发探测，单轮耗时 ≈ 最慢一个节点，节点数增多不拖慢调度周期（tickSeconds=3 时几十节点拓扑也能保持实时手感）。
- **限城域网内网使用**：会话准入按 IP 段前缀白名单（默认 `10. / 172.16. / 192.168. / 100.64.`，CGNAT 段包含在内），可选配对码防误连。**切勿把监听端口映射到公网。**
- **独立部署形态**：主控端与服务端（被控端，即插件面板中的「客户端」）可分别发布为不依赖工具箱的独立控制台程序（`QingShu.GalaMaster` / `QingShu.GalaAgent`），三形态共用同一份核心源码与协议，详见 §5。

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

**调度手感（周期/阈值/冷却/满载基数）不用改文件**：主控面板「调度参数」区直接改，
点「保存并生效」后下一探测周期自动生效（同一份运行配置热更新，主控不重启、客户端不掉线），
同时写回本文件持久化。本文件的 `settings` 四项只作初始值/手工备份用途。

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
5. **压力趋势**：主控面板节点压力列表右侧带迷你趋势线（近 90 个探测周期 ≈ 5 分钟），
   压力是否在爬坡一眼可辨，比单点数字更适合现场决策。

## 4. 现场部署清单

1. 控制台与全部终端在同一城域网（各 IP 段互通），配置好 `routes` 段前缀。
2. 控制台首次启动主控端，Windows 防火墙会弹「允许访问」——勾选**专用网络**允许（TCP 47816 入站）。也可提前放行：
   `netsh advfirewall firewall add rule name="QSTK-Gala" dir=in action=allow protocol=TCP localport=47816`
3. 终端打开工具箱 → 插件页 → 面板 → 客户端角色 → 填主控 IP → 连接（终端机也可不装工具箱，改用独立服务端程序常驻，见 §5.3）。
4. 主控面板把关键终端设为「高」优先级。
5. 演出中只看拓扑与事件日志；需要人工干预时选中终端 → 切换线路 / 限速标记。
6. 需要事后复盘时，插件快捷动作「导出事件日志」把主控端 + 客户端事件写出为 txt（含全部调度决策与数字理由）。

## 4.1 远程操控项

| 操控 | 说明 |
| --- | --- |
| 切换线路 | 选中终端 → 选目标服务器 → 下发 switch 指令，终端探测新服务器并更新本地分配 |
| 设优先级 | 高（阈值/冷却减半，压力起来先保）/ 中 / 低；影响自动调度手感 |
| 限速标记 | 下发 kbps 值，终端落盘 assignment.json 供本机限速执行器消费；空/0 = 解除 |
| 即时采集 | 命令终端立即上报一轮指标 |

离线超过 30 分钟的终端会话自动清理（重连保分配的语义保留 30 分钟，防临时终端无限积累，清理有事件留痕）。

## 4.2 限速标记的消费端（示例执行器）

`tools/assign-executor.ps1` 是 assignment.json 的最小可运行消费端示例：

```powershell
# 仅展示模式：轮询打印当前分配（服务器/路由/优先级/限速标记）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1

# 应用模式：把限速标记转成本机 Windows QoS 策略（需管理员；标记 0 自动解除）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1 -Apply
```

真实消费端（路由器 API / 代理切换 / 专用限速工具）参照其「读文件 → 差异化执行」骨架替换执行段即可。

## 5. 独立部署形态：主控端 / 服务端单独出程序

同一份 Gala 核心源码（`QingShu.Plugin.GalaDispatch/Gala/*.cs`，三个工程以 Compile 链接共享，
单一事实源）提供三种运行形态，按现场机器角色选用：

| 形态 | 程序 | 运行位置 | 特点 |
| --- | --- | --- | --- |
| 图形主控 / 客户端 | 工具箱 + 本插件面板 | 管理机 | 实时拓扑着色、趋势线、调度参数热调、快捷动作 |
| 独立主控端（控制台） | `QingShu.GalaMaster` | 任意常驻机器 | 免装工具箱；命令行全景 / 操控 / 热调；可挂计划任务 |
| 独立服务端（被控端） | `QingShu.GalaAgent` | 各节点机 | 单文件常驻，免装工具箱；自动重连上报 + 执行指令 |

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

- 拓扑配置：exe 同目录 `gala-topology.json`，格式与插件数据目录完全一致，可互相拷贝；
  首次运行自动生成模板并退出，填好真实网络资料后再启动。`--config 路径` 可指定其他位置。
- 交互命令：`status / nodes / clients / events`（全景与事件）、`assign / priority / throttle / ping`
  （与面板同语义的远程操控）、`params / setparam`（调度参数热调，下一探测周期生效并写回配置文件）、
  `watch on|off`（每 10 秒一行态势摘要）、`quit`。
- 无人值守：`--watch` 启动即开态势摘要；输出重定向（计划任务）时自动跳过交互输入，
  持续常驻，Ctrl+C 或结束进程优雅退出。
- 端口被占等启动失败会给出明确错误文案；首次连接的防火墙放行同 §4 第 2 条。

### 5.3 独立服务端（QingShu.GalaAgent）

- 配置：exe 同目录 `gala-agent.json`（首次运行自动生成模板）：`masterHost`（主控机 IP，必填）
  / `port` / `pairingCode` / `dataFolder`；命令行参数优先：
  `--master 主控IP[:端口] --port 端口 --pairing 配对码 --data 数据目录`。
- 行为与插件「客户端」角色一致：断线 3 秒重连、周期上报指标、执行主控指令、当前分配落盘
  `assignment.json`（供 §4.2 执行器消费）。
- 交互命令：`status / events / quit`；重定向输出时同样保持常驻。

## 6. 边界说明

- 插件下发的是「终端应使用哪台服务器/线路」的分配与标记；实际网络路径由校园网策略/路由侧生效。
  `assignment.json` 就是为这类执行器准备的对接点（真实拓扑资料到位后可加路由器 API 对接）。
- 协议为明文 JSON over TCP，靠**内网准入 + 配对码**控制接入，不含公网对抗设计。
- 客户端指标中的「RTT/丢包」是终端到主控的 ICMP 探测结果；禁 ICMP 的网段内探测显示为「—」，调度退化为并发/带宽分量主导。
- 卸载插件会停掉主控监听与客户端连接（宿主统一收尾，面板自动收起）。
