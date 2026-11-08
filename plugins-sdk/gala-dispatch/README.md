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
powershell -ExecutionPolicy Bypass -File tools/plugin-sign.ps1 -PluginFolder $qm
powershell -ExecutionPolicy Bypass -File tools/plugin-sign.ps1 -PluginFolder $qa
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
    "autoStartMaster": true,        // 随插件加载自动启动监听（无人值守；false = 手动启动）
    "allowedPrefixes": ["10.", "172.16.", "192.168.", "100.64."],  // 内网准入段（八点界匹配："192.168.1" 只认 192.168.1.x，不会误放 192.168.11.x）
    "pairingCode": "",              // 配对码，空 = 不校验；现场建议设置（连续错配自动加 1.5s 延迟防爆破）
    "switchThreshold": 12,          // 切换阈值（路径分差），防抖动
    "switchCooldownSeconds": 30,    // 同一终端两次切换最小间隔
    "clientsPerNodeFull": 10,       // 并发满载基数（负载分）
    "clientIdleTimeoutSeconds": 20, // 心跳空闲超时：超过该秒数未收到终端任何帧按离线断开（半开连接不虚占并发压力分）；须 ≥ 4×tickSeconds+5，配置健康检查会提醒
    "weightRtt": 0.35, "weightLoss": 0.25, "weightLoad": 0.20, "weightBandwidth": 0.20
  },
  "nodes": [                        // 固定设施：master / router / server / switch
    { "id": "r1", "type": "router", "label": "出口路由A", "ip": "10.10.0.254",
      "probePort": 80 },            // probePort 可选、不限类型：禁 ICMP 网段给路由配管理口即可恢复探测
    { "id": "core", "type": "router", "label": "核心 S7706", "ip": "10.10.0.254",
      "gatewayIps": ["10.10.1.254", "10.10.2.254"] },  // 可选：一台设备承载多段网关时全量列出
                                    // （如核心交换机的全部 Vlanif 地址）；全部并行探测，任一可达即在线，
                                    // 面板/预检按「n/m 网关可达」展示，与主 ip 重复的项自动剔除
    { "id": "sw1", "type": "switch", "label": "一楼层", "ip": "10.10.0.2",
      "segments": ["10.10.1."] },   // 可选：该交换机服务的终端段——画布把动态注册的真实终端
                                    // 逐台挂到所属交换机下（未配则终端挂其路由节点）
    { "id": "s1", "type": "server", "label": "流媒体服务器", "ip": "10.20.0.10", "probePort": 8000 },
    { "id": "s2", "type": "server", "label": "互动服务器", "ip": "10.20.0.11",
      "x": 0.7, "y": 0.72 }         // x/y 可选：手工拓扑布局（0~1 归一化），缺省自动分层
  ],
  "links": [                        // 链路容量（带宽压力分用）
    { "from": "r1", "to": "s1", "capacityMbps": 1000 }
  ],
  "routes": [                       // 调度规则：某 IP 段经哪台路由、可落哪些服务器（同样八点界匹配，
                                    // 写 "10.1" 只匹配 10.1.x.x，不会误吞 10.10.x.x；最长前缀优先）
    { "segmentPrefix": "10.10.", "viaRouter": "r1", "servers": ["s1", "s2"] }
  ]
}
```

改完在主控插件卡片点快捷动作「重载拓扑配置」即时生效（运行中的主控自动按新配置重启；
校验告警写插件日志）。服务端不进 `nodes`——**真实终端动态加载**：终端上线自动注册，
拓扑画布逐台绘制（蓝=网线 / 紫=直连 WiFi / 灰=离线，悬停看主机名与地址；接入方式由
客户端随 hello 上报，旧版客户端不上报则按蓝灰展示），归属按其 IP 段匹配 `routes`，
配了交换机 `segments` 的段优先挂到接入交换机下。

**配置健康检查**：每次加载/重载自动校验并用人话告警报出填错处（重复 id/重复 IP/非法 IP/
probePort 越界/规则无服务器/规则指向非 router/无调度规则/无准入段/未设配对码）——
告警写插件日志与控制台启动输出，空列表静默；只报不改，剔错仍由规范化负责。

**原子写与损坏回退（完整性底座）**：拓扑配置/连接参数/assignment.json 的所有写盘均为
原子写（先落临时文件再替换，替换前自动留一份 `.bak`）——进程崩溃/断电/磁盘满不再留半截
JSON；读取时主文件损坏自动回退 `.bak`（告警留痕），填好的真实拓扑不会因一次写坏而整体丢失。

**探测降级（禁 ICMP 网段）**：任一节点（不限类型）配了 `probePort` 即加 TCP 端口握手探测——
Ping 失败但 TCP 通即视为存活（RTT 取握手耗时）。禁 ICMP 的网段给路由配管理口（如 80/443）、
服务器配业务口即可，RTT/丢包/压力评分照常工作。

**即时预检（填完拓扑先验一遍）**：主控插件快捷动作「立即探测一轮」（独立主控台 `probe`
命令）立即探测全部 router/server 节点，逐节点报可达（RTT）/不可达；不写调度状态、不触发
切换，主控未监听时同样可用。探测不到 = IP 写错或网段不通（禁 ICMP 网段记得配 probePort）。

**心跳超时（半开连接治理）**：终端拔电/网线故障时 TCP 可能收不到断开信号（半开连接），
主控超过 `clientIdleTimeoutSeconds`（默认 20 秒）未收到该终端任何帧即按离线断开；
并发压力分同时改用「在线窗口内有心跳」口径统计（窗口 = max(15, 4×tickSeconds)，
tickSeconds 配大时窗口自动跟着变，不会把每个正常心跳周期都误判成离线），
半开终端不再虚增负载分导致调度失真。终端重连后 30 分钟内仍保分配不变。
**配置自洽**：健康检查会提醒 `clientIdleTimeoutSeconds ≥ 4×tickSeconds + 5`，
两者不匹配时终端会在线/离线反复横跳。

**无人值守（autoStartMaster，默认开）**：主控插件随加载自动启动监听——控制机重启工具箱即
恢复监听，无需人工点面板；「停止主控端」只停本次会话。不希望自动启动改为 `false`。

**事件留痕**：主控全部事件（调度决策/上下线/操控回执）自动追加写入
`plugins-data\qingshu.galamaster\gala-master.log`（1MB 自动轮转 .old），复盘不依赖
内存缓冲或导出时机；服务端侧对应 `gala-agent.log`。

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
| 切换线路 | 选中终端 → 选目标服务器 → 下发 switch 指令，终端探测新服务器并更新本地分配；**同时锁定该终端**（Pinned），自动调度不再覆盖操作员意图，锁标签在客户端列表可见 |
| 恢复自动 | 解除手动锁定，下一探测周期起该终端重新参与综合调度（独立主控台命令为 `unpin`） |
| 设优先级 | 高（阈值/冷却减半，压力起来先保）/ 中 / 低；影响自动调度手感 |
| 限速标记 | 下发 kbps 值，终端落盘 assignment.json 供本机限速执行器消费；空/0 = 解除 |
| 即时采集 | 命令终端立即上报一轮指标 |
| 剔除会话 | 断开并立即移除该终端会话（不保留 30 分钟重连保分配），用于误连/故障终端即时清理；重连按新终端重新分配 |

离线超过 30 分钟的终端会话自动清理（重连保分配的语义保留 30 分钟，防临时终端无限积累，清理有事件留痕）。

### 2.3 实况拓扑模板（费县二中 · 2026-11-05 扫描 + 校方口述）

`tools/gala-topology.feixian2zhong.json` 是按本校实测数据填好的真实拓扑（数据驱动，
不是占位示例），启用：把该文件拷到主控插件数据目录并改名 `gala-topology.json`
（独立主控端则放 exe 同目录），点「重载拓扑配置」→「立即探测一轮」验证 IP 可达。

实况要点（校方 2026-11-08 口述口径）：

- **单核心，全部网关采纳**：12 个 /24（`10.120.32.0/24` – `10.120.43.0/24`）的 `.254` 全部是
  同一台 Huawei S7706 的 Vlanif 管理地址——gw-core 主 IP `10.120.42.254`（probePort 80）+
  `gatewayIps` 全量另 11 个 Vlanif 地址：12 个地址并行探测、任一可达即在线，
  面板/预检报「网关 n/12 可达」（某个 Vlanif 不通一眼可见）。
- **按网段分、不分楼**：楼栋归属不做调度依据（校方明确"不要相信分楼"），routes 按网段一段一条，
  共 12 条（含仅剩核心 Vlanif 的预留段 36/38/40/43）；42 台交换机按管理 IP 所在段标注
  `segments`（现场如与实际接入不符，手工改该字段即可）。
- **纳入范围**：路由器（核心 + 校园出口 172.30.1.1，traceroute 推断）、42 台接入交换机
  （40 确认 + 2 疑似，probePort 按指纹推导：26 SSH·22 / 7 Web·80 / 7 Telnet·23 / 2 仅 ICMP）、
  4 台服务器（直播源 AVA `10.120.42.160`、IIS `10.120.39.1`、IIS `10.120.34.121`、文件服务器 `10.120.42.188`）。
- **终端不进 nodes，动态加载**：网线 PC 与直连 WiFi PC 都是 gala 客户端，上线自动注册后
  **逐台画进拓扑**——挂在所属网段接入交换机下（无交换机的机房/预留段挂核心），
  蓝点=网线、紫点=直连 WiFi（客户端自动识别网卡类型上报）、灰点=离线，悬停看主机名/地址/线路；
  摄像头/NVR/打印机/哑终端不在范围，不装客户端自然不出现。
- **实况杂线**：有电脑损坏后把交换机直接级联到路由器上的情况——物理上联未从管理面获取，
  links 按逻辑上联核心建模（capacityMbps 1000 为假设值），现场核验级联/直连后改 `links` 即可。
- **调度语义**：全部段 `viaRouter=gw-core`、`servers=["srv-ava"]`（唯一直播源）；
  若平台方确认 `10.120.39.1` / `10.120.34.121` 可作备用源，把对应 id 加进 `routes.servers`
  即启用综合调度（切换线路下拉会列出全部 server 节点，勿在未确认时手动切到文件服务器）。
- **准入**：`allowedPrefixes=["10.120."]`（城域网内网各段互通）；`pairingCode` 留空待现场设置
  （加载时健康检查会告警提醒）；`clientsPerNodeFull=150`（满载口径：70 台一体机 + 60 台教师 PC ≈ 130 路）。

## 3. 服务端：连接与消费

- **连接**：服务端面板填主控 IP / 端口 / 配对码 → 打开「连接主控端」。成功一次即写入
  `plugins-data\qingshu.galaagent\gala-agent.json`，此后**随插件加载自动重连**（常驻语义，
  终端机重启工具箱后无需任何手动操作）；「断开连接」只停本次会话，清空主控 IP 再连一次才撤销自动重连。
- **行为**：断线自动重连（3 秒起步温和退避，封顶 10 秒，连接成功重置；面板/控制台实时显示
  「约 N 秒后重试」）；连接成功立即首报一轮指标（不等第一个心跳周期）；周期上报
  RTT / 丢包 / 收发速率 / 进程 CPU；执行主控指令；发送失败/写超时立即断开重连（不等心跳
  空闲超时，半开连接最快 5 秒内自愈）。
- **丢包口径**：终端到主控的丢包样本 = Ping 通或 TCP 会话仍在都算链路通——禁 ICMP 的网段
  不会虚报 100% 丢包（RTT 仍如实显示「—」）。
- **上报口径**：指标发送前本机自钳、主控侧入库前再钳（同口径双保险）——异常采样/虚报超限
  不出本机、不污染压力分；分配落盘为原子写（`.bak` 回退，见 §2）。
- **消费**：当前分配落盘 `plugins-data\qingshu.galaagent\assignment.json`；事件留痕写
  `plugins-data\qingshu.galaagent\gala-agent.log`（1MB 轮转）。

### 3.1 分配变更钩子（onAssignmentChange，事件化消费）

`gala-agent.json` 里可加一条 `onAssignmentChange` 命令：**assignment.json 每次落盘后自动执行**
（串行防并发拉起，失败仅留痕），本机执行器从「轮询文件」升级为「变更即执行」：

```jsonc
{
  "masterHost": "10.10.0.1",
  "port": 47816,
  "pairingCode": "",
  "onAssignmentChange": "powershell -ExecutionPolicy Bypass -File C:\\gala\apply-route.ps1 -Assignment \"{assignment}\""
}
```

- 命令串中 `{assignment}` 占位符 = assignment.json 完整路径（建议加引号防空格断参）；
  不含占位符时路径会作为最后一个参数追加。
- 服务端插件：手工编辑 `gala-agent.json` 加入该字段即可（连接动作不会冲掉它）；
  独立服务端还可用命令行参数 `--hook "命令"` 临时指定。
- 不启用钩子时，轮询消费照旧可用（§3.2 示例执行器）。

### 3.2 轮询消费（示例执行器）

`tools/assign-executor.ps1` 是 assignment.json 的最小可运行消费端示例（默认路径即服务端插件数据目录）：

```powershell
# 仅展示模式：轮询打印当前分配（服务器/路由/优先级/限速标记）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1

# 应用模式：把限速标记转成本机 Windows QoS 策略（需管理员；标记 0 自动解除）
powershell -ExecutionPolicy Bypass -File plugins-sdk\gala-dispatch\tools\assign-executor.ps1 -Apply
```

真实消费端（路由器 API / 代理切换 / 专用限速工具）参照其「读文件 → 差异化执行」骨架替换执行段即可；
有路由器 API 后优先改造成 §3.1 的钩子命令，变更即时生效、无轮询延迟。

## 4. 现场部署清单

1. 控制机与全部终端在同一城域网（各 IP 段互通），主控 `gala-topology.json` 配好 `routes` 段前缀。
   本校环境直接用 §2.3 的实况模板起步（`tools/gala-topology.feixian2zhong.json` 拷到主控数据目录
   改名 `gala-topology.json`；master 节点仅展示、无需填 IP，装好即用）。
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
- 交互命令：`status / nodes / clients / events`（全景与事件）、`assign / unpin / priority / throttle / ping / kick`
  （与面板同语义的远程操控，assign = 手动切换并锁定，unpin = 恢复自动调度，kick = 剔除会话）、
  `probe`（立即探测一轮节点可达性，验证拓扑）、`params / setparam`（调度参数热调，下一探测周期生效并写回配置文件）、
  `watch on|off`（每 10 秒一行态势摘要）、`quit`。
- 无人值守：`--watch` 启动即开态势摘要；输出重定向（计划任务）时自动跳过交互输入，
  持续常驻，Ctrl+C 或结束进程优雅退出。
- 端口被占等启动失败会给出明确错误文案；首次连接的防火墙放行同 §4 第 3 条。

### 5.3 独立服务端（QingShu.GalaAgent）

- 配置：exe 同目录 `gala-agent.json`（首次运行自动生成模板）：`masterHost`（主控机 IP，必填）
  / `port` / `pairingCode` / `dataFolder` / `onAssignmentChange`（§3.1 变更钩子，可空）；命令行参数优先：
  `--master 主控IP[:端口] --port 端口 --pairing 配对码 --data 数据目录 --hook "变更钩子命令"`。
- 行为与服务端插件一致：断线自动重连（3→10 秒温和退避）、连接后立即首报、周期上报指标、
  执行主控指令、当前分配落盘
  `assignment.json`（供 §3 消费端使用）+ 事件留痕 `gala-agent.log`。
- 交互命令：`status / events / quit`；重定向输出时同样保持常驻。

## 6. 边界说明

- 插件下发的是「终端应使用哪台服务器/线路」的分配与标记；实际网络路径由校园网策略/路由侧生效。
  `assignment.json` 就是为这类执行器准备的对接点（真实拓扑资料到位后可加路由器 API 对接）。
- 协议为明文 JSON over TCP，靠**内网准入 + 配对码**控制接入，不含公网对抗设计。
- 客户端上报指标在主控侧统一钳制（RTT ≤ 10 分钟、丢包/并发 CPU ≤ 100、速率 ≤ 10 Gbps 上限，
  NaN/无穷按探测失败处理）：异常/恶意上报不再污染压力评分与画布；终端身份（hostname）
  同样清洗限长。协议本身仍明文无认证，仅限可信内网使用。
- 客户端指标中的「RTT/丢包」是终端到主控的 ICMP 探测结果；禁 ICMP 的网段内 RTT 显示为「—」，
  但丢包不会虚报（TCP 会话仍在即链路通），调度退化为并发/带宽分量主导。
- 卸载任一插件只停自己的服务：卸主控 = 停监听（各终端自动重连等待），卸服务端 = 断本机连接；
  宿主统一收尾，面板自动收起。两个插件互不依赖，加载顺序无关。
