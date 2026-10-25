# 青枢工具箱 | QingShu.Toolkit

> 学校专用系统工具箱 —— **高性能 · 高颜值 · 高扩展**
> 网络全链路诊断 / 磁盘清理 / 安全优化 / 功耗管理 / 询问式自动更新
> 目标平台：**Windows 10 1809+**（兼容 Win11，材质自动升级 Mica）

![技术栈](https://img.shields.io/badge/.NET-8.0_LTS-2563EB) ![UI](https://img.shields.io/badge/UI-WinUI_3_(WASDK_1.6)-3B82F6) ![PDF](https://img.shields.io/badge/报告-QuestPDF-10B981) ![License](https://img.shields.io/badge/License-MIT-64748B)

---

## 功能总览

### 1. 网络诊断（傻瓜模式：一键诊断 → 一键修复 → 一键导出）

按「物理链路 → 硬件驱动 → IP 配置 → 网关路由 → DNS → 互联网 → 校园内网 → 城域网认证 → 时钟 → 代理 VPN → 系统栈 → 连接质量」**全链路分层 28 项检查**：

| 分层 | 检查项 |
|------|--------|
| 物理链路 | 物理连接状态 · Wi-Fi 信号质量 |
| 硬件驱动 | 网卡 PnP 状态 / ConfigManagerErrorCode / 驱动版本 |
| IP 配置 | IPv4 与掩码 · APIPA(169.254) 识别 · DHCP 租约到期 |
| 网关路由 | 网关连通（丢包/延迟/抖动）· ARP 表项与欺骗检测 · 默认路由与多出口冲突 |
| DNS | 配置有效性 · 系统解析 · 逐台 DNS 服务器 UDP 探测 · Hosts 劫持 |
| 互联网 | ICMP 多目标 · HTTP(NCSI) · HTTPS/TLS |
| 校园内网 | 内网资源 ping / TCP 兜底（地址可在设置配置） |
| 城域网认证 | 认证门户检测（NCSI 重定向识别）· 认证过期精确判定（状态接口/门户联动）· 被踢下线识别 |
| 系统时钟 | 与服务器时间偏差（证书/认证失败的隐形杀手） |
| 代理 VPN | 用户代理 · WinHTTP 代理 · VPN/多网卡干扰 |
| 系统网络栈 | 网络服务状态（**只读，不代改**）· 防火墙 · Winsock LSP 完整性 |
| 连接质量 | 外网延迟/抖动/丢包 · MTU 探测 |

- **一键修复**：DNS 缓存 / ARP / DHCP 续租 / 网卡启停 / 时间同步 / 代理复位 / Winsock 重置 / 打开认证页 —— 管理类动作**合并为一次 UAC**，危险项默认不勾选。
- **日志留存**：`logs/yyyy-MM-dd/diag-yyyyMMdd-HHmmss-NNN.log|.jsonl`（按日期、按当日次数、双文件），另有 `app-yyyyMM.log` 应用日志，保留期可配。
- **PDF 报告（QuestPDF 正规模板）**：报告编号按日累计（QS-DIAG-yyyyMMdd-NNN），自动写入**设备编号 + SMBIOS 序列号 + MachineGuid**，生成时**必填年级/班级**，含分层明细表、修复记录、P1/P2/P3 分级处置建议与签名栏。

### 2. 磁盘清理（全部询问式确认）

- **常规清理**：用户/系统临时文件、更新缓存、传递优化、缩略图、Edge/Chrome/Firefox 缓存、WER 错误报告、D3DSCache、崩溃转储、Office 缓存（默认不勾）、Windows.old（高风险默认不勾）、回收站。
- **微信 / QQ 久远文件**：兼容 `WeChat Files`、`xwechat_files`（微信 4.x）、`Tencent Files`（含 nt_qq）布局；只列超过阈值天数（默认 180 天）的文件；**绝不触碰聊天记录数据库（.db/.sqlite）**；删除方式可选回收站/直接删除，并有“历史图片将显示过期”的明确提示。
- **大文件扫描**：全盘兜底（跳过系统目录），阈值可调，打开位置/勾选删除。

### 3. 性能优化（安全可逆，不越界）

- **内存整理**：EmptyWorkingSet 工作集回收（进程不死），释放量实时统计。
- **进程 EcoQoS**：对所选后台进程启用/恢复功耗遏制（SetProcessInformation）。
- **被动弹窗拦截**：**全局白名单策略** —— 只放行系统/办公/教学软件（希沃、WPS、Office、QQ/微信、浏览器等，可增删），其余新出现的可见窗口被动关闭；启动前已存在窗口、前台窗口、系统外壳、工具窗口永不拦截；每次拦截写入日志。
- **启动项只读**：注册表 Run + 启动文件夹展示，一键跳转任务管理器。
- **设计边界承诺**：不改 services.msc 服务、不改虚拟内存、不导入注册表、不精简系统、不内置全家桶、不关闭 Defender —— 写在 UI 与代码里。

### 4. 功耗管理（自动高/低功耗切换）

- `自动`：接通电源 → **高性能（红）**；使用电池 → **低功耗（绿，自动 EcoQoS）**；也可手动选平衡（蓝）/高性能/低功耗。
- 状态栏三色小圆点标识（禁止大面积色块，符合视觉规范）。

### 5. 自动更新（询问式）+ 标题栏手动更新按钮

- 每 24h 静默检查 GitHub Releases → 发现新版本弹窗询问 → 下载（进度可见）→ 再次确认 → 退出后 `update.cmd` 自动替换并重启。
- 标题栏右上角（最小化按钮左侧）常驻手动更新按钮：待更新时带红点提示。

### 6. 设置页

主题切换（跟随系统/浅色/深色）、亚克力材质开关、城域网认证地址/状态接口/在线关键字、内网探测地址、DNS、久远阈值、大文件阈值、删除方式、功耗模式、EcoQoS、弹窗拦截、更新开关与仓库、日志保留期、恢复默认。

---

## 视觉规范（详见 docs/DESIGN.md 与《架构设计与视觉规范》PDF）

- **Fluent 原生**：Win10 用 `DesktopAcrylic`（深色自动提高不透明度保证 WCAG AA），Win11 自动升级 Mica。
- **青枢蓝**：浅色 `#2563EB` / 深色 `#3B82F6`，整套 Light/Dark 主题字典见 `Themes/Palette.xaml`（卡片、分割线、正文、辅助文字、占位、Hover/Pressed 全套）。
- **状态色**：成功 `#10B981` / 警告 `#F59E0B` / 错误 `#EF4444`；状态**文字**使用 WCAG AA 深化变体（如 `SuccessTextBrush`），状态**底色**用 10~15% 透明浅底。
- **功耗标识色**：`#059669` 低功耗 / 主色 平衡 / `#DC2626` 高性能 —— 仅用于状态栏小标识。

## 目录结构

```
QingShu.Toolkit/
├── src/QingShu.Toolkit/
│   ├── Themes/            # Palette.xaml（双色主题字典）+ Styles.xaml
│   ├── Interop/           # NativeMethods（EcoQoS/窗口/回收站/代理刷新）+ UAC 提升
│   ├── Helpers/           # 命令执行/网络探测/原生DNS查询/文件操作/WMI
│   ├── Models/            # 设置/诊断/清理/报告模型（CheckItem 可直接绑定 UI）
│   ├── Services/
│   │   ├── Logging/       # 分日期/次数/多文件日志
│   │   ├── Network/       # 28 项检查 + 编排器 + 一键修复（合并 UAC）
│   │   ├── Cleanup/       # 常规/微信QQ/大文件
│   │   ├── Optimize/      # 内存整理/启动项只读
│   │   ├── Power/         # 自动功耗切换 + EcoQoS
│   │   ├── Popup/         # 全局白名单弹窗拦截
│   │   ├── Update/        # GitHub Releases 询问式更新
│   │   └── Reporting/     # QuestPDF 正式报告模板
│   ├── ViewModels/        # MVVM（CommunityToolkit.Mvvm 8.4）
│   └── Views/             # 6 页面（仪表盘/诊断/清理/优化/日志/设置）
├── packaging/Package.appxmanifest   # MSIX 双模式备用清单
├── tools/make_assets.py             # 图标资产生成脚本
└── docs/                            # BUILD / DESIGN
```

## 构建

```bash
# 环境要求：Windows 10 1809+，VS 2022 17.10+（含 “.NET 桌面开发” 与 “Windows 应用程序开发” 工作负载），.NET 8 SDK
git clone https://github.com/BeiXiangGuMao/QingShu.Toolkit.git
# 用 VS 打开 QingShu.Toolkit.sln → 还原 NuGet → F5

# 或命令行发布（自包含单目录，U盘/机房直接部署）
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true
```

MSIX 双模式切换、常见构建问题、部署清单见 **docs/BUILD.md**。

## 数据与隐私

全部诊断、清理、报告数据仅在本机处理与留存：

| 数据 | 位置 |
|------|------|
| 配置 | `%LOCALAPPDATA%\QingShu.Toolkit\config.json` |
| 日志 | `%LOCALAPPDATA%\QingShu.Toolkit\logs\` |
| PDF 报告 | `我的文档\QingShu.Toolkit\Reports\yyyy-MM-dd\` |
| 更新缓存 | `%LOCALAPPDATA%\QingShu.Toolkit\update\` |

唯一联网行为是检查 GitHub 更新（可在设置关闭）。

## License

MIT © 2026 BeiXiangGuMao
