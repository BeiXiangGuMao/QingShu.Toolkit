# 青枢工具箱 · 功能可行性与模拟测试清单

> 本文档由两部分组成：
> A. 自动化模拟测试（沙箱静态验证套件，每轮提交前运行）
> B. 场景模拟矩阵与人工验收清单（VS2022 编译后按表逐项验证）

## A. 自动化模拟测试（已通过 ✓）

运行方式：`python3 scripts/check_runtime_v3.py`（+ `check_lint_v2.py` 回归）

| 验证项 | 覆盖内容 | 结果 |
|---|---|---|
| x:Bind 路径交叉核对 | 7 个 XAML × 6 个 VM，逐段解析绑定路径到 C# 成员（模拟 XamlCompiler WMC1110），含 DataTemplate x:DataType 上下文与 Owner 多级路径 | ✓ 0 命中 |
| 事件线程审计 | Power.Changed / Popup.Blocked / Update.StatusChanged / Settings.Changed 四类事件必须在 UiDispatcher.Run 内触发 | ✓ 全部已调度 |
| 同步上下文 | App.OnLaunched 安装 DispatcherQueueSynchronizationContext（null 守卫）| ✓ |
| 资源键存在性 | 全部 ThemeResource / StaticResource 引用可解析（自定义 + 系统白名单）| ✓ |
| 配置安全 | 保存互斥（SemaphoreSlim）+ 临时文件 + 原子替换 + Changed 调度 | ✓ |
| using 完整性 | 53+ 文件 / 160+ 类型跨命名空间扫描 | ✓（2 处已知误报）|
| XAML 语法 | 7 个 XAML expat 解析 | ✓ |
| 括号平衡 | 本轮修改文件 | ✓ |

## B. 场景模拟矩阵

标记说明：✅ 代码路径已审查/修复 · 🔍 需人工按 B.2 清单确认

### B.1 UI 线程与稳定性（本轮修复核心）

| # | 场景 | 预期行为 | 状态 |
|---|---|---|---|
| 1 | 启动约 10s 后功耗状态自动切换（Timer 在线程池触发 Changed）| 事件经 UiDispatcher 调度回 UI，状态栏三色标识正常刷新，无 COMException | ✅ |
| 2 | 弹窗拦截事件驱动扫描（前台切换 + 窗口显示双钩子；20s 克备轮询、宽限观察期自动压回 3s，钩子全失败退化 3s 轮询）| 抢前台新窗 2s 宽限后拦截；**后台弹出**（不抢前台）即时拦截不再等轮询；计数经调度更新，标题栏 + 仪表盘同步 | ✅ |
| 3 | 启动 8s 后自动检查更新（ConfigureAwait(false) 链）| StatusChanged 经调度回 UI，进度环/圆点/图标正确切换 | ✅ |
| 4 | 诊断执行中每项完成的进度回调（Progress<CheckItem>）| 同步上下文已安装，回调回 UI 线程，进度条逐项推进 | ✅ |
| 5 | 网速模式 400ms 一拍的实时测速刷新 | SpeedProgress 回调回 UI 线程，Mbps 大数字实时跳动 | ✅ |
| 6 | 诊断/清理/修复完成后的属性赋值（ConfigureAwait(true) 链）| 全部回 UI 线程执行，列表与结果文本正常显示 | ✅ |
| 7 | 日志页选中文件预览（FromCurrentSynchronizationContext）| 同步上下文已安装，TaskScheduler 有效，尾部预览正常 | ✅ |
| 8 | 深浅色切换瞬间（RootGrid.RequestedTheme + ActualThemeChanged）| 标题栏系统按钮前景/悬停色随主题重着色，无跨线程、无割裂感 | ✅ |
| 9 | 仪表盘 2s 采样（PerformanceCounter/DriveInfo 为慢操作）| 采样移入线程池 + 重入保护，UI 不卡顿，数据照常刷新 | ✅ |
| 10 | 快速连续切换页面（VM 反复构造）| 各页 VM 独立实例，Timer/事件随 Dispose 释放（Dashboard 已实现）| 🔍 |

### B.2 人工验收清单（编译通过后逐项打勾）

**启动与仪表盘**
1. 启动后仪表盘自动显示 CPU/内存/C 盘/网卡/序列号（无需手动刷新）
2. 等待 ≥10s（或切换电源/拔插电源）→ 无弹错，状态栏功耗标识变化
3. 点击"开始全面诊断"→ 直达网络诊断页并自动开始

**网络诊断（模拟不同网络问题）**
4. 断网/拔线状态跑诊断 → 物理层 FAIL，后续层按依赖跳过，无异常崩溃
5. 校园网认证过期（浏览器打开任意 HTTP 网站被重定向到 1.1.1.1/10.x 门户）→ "城域网认证门户检测"FAIL 且建议文案**含检测到的门户地址**；一键修复"打开城域网认证页面"直接打开该地址（无需先在设置里手填）
6. 认证正常 → 该项 PASS"网络直通"
7. 手动填错 DNS 后跑诊断 → dns 项 FAIL，一键修复（普通权限）刷新 DNS 缓存成功
8. 网速模式：Mbps 实时跳动 → 结束后显示档位 + 低速原因分析卡（三色徽标正确显示）

**磁盘清理**
9. 扫描 → 各目标显示大小；取消按钮生效；清理后自动重扫
10. 微信/QQ 页扫描 .db/.sqlite 文件不被列入（硬保护）；删除进回收站开关生效

**性能优化**
11. 进程列表加载（无 COMException）；勾选进程整理内存 → 结果文本显示释放量
12. EcoQoS 启用/恢复 → 状态文本正确
13. 弹窗拦截开/关 → 状态栏计数联动；添加白名单 → 徽标出现；点 ✕ 删除 → 徽标消失（本轮 Owner 路由修复项）

**设置**
14. 任一开关/下拉改动 → 1 秒内出现"设置已保存"提示（本轮原子写盘 + 调度修复项）
15. 深色/浅色/跟随系统 → 整窗（含标题栏按钮、导航窗格、卡片）一致变化
16. 强杀进程后重启 → 设置不丢失（原子替换生效）；人为把 config.json 改成半截 JSON → 启动不崩，回退默认值

**日志与更新**
17. 日志页按日期浏览 → 预览正常（本轮同步上下文修复项）；打开日志目录按钮有效
18. 标题栏更新按钮图标为"同步箭头"（SymbolThemeFontFamily 自适应字体）；点击 → 进度环 → 完成后 tooltip 变化

**小众/极端场景**
19. 非管理员运行 → 管理员类修复项有"需管理员"徽标，执行时单次 UAC 合并确认
20. 白名单清空 → 开启拦截后，除前台/系统窗口外新弹窗被关闭且计数增加；本程序自身窗口永不被拦（_ownPid 排除）
21. 系统时间偏差 >5 分钟 → 时钟项 FAIL，一键修复时间同步（UAC）
22. 代理/VPN 开启 → 对应项 WARN/FAIL，修复可关闭用户级代理
23. 全程无网络环境（飞行模式）→ 诊断快速分层失败而非长时间挂起；测速模式给出"未测得带宽"而非卡死

### B.3 v1.2.0 新增/修复项（本轮验收重点）

**设置即时生效（修复“下面的设置没用”）**
24. 修改门户地址 / DNS / 内网探测 / 久远阈值 / 大文件阈值 / 更新仓库 / 日志天数 → 失焦后 1 秒内出现“设置已保存”提示，重新进入页面值保留
25. 开启“门户自动登录”并填账号密码 → 关闭重启后仍保留（本机 config.json）

**仪表盘 CPU 首采**
26. 启动后首屏约 1-2 秒内 CPU 显示真实占用（不再是长期 0%）；内存/磁盘同时就绪

**常规清理可用性（修复“默认扫描无法使用”）**
27. 首次进入磁盘清理 → “开始扫描”按钮立即可点（无需先有任何扫描结果）
28. 扫描中某目录无权限 → 该项统计为 0/失败但其余项照常出结果（日志可见单目标告警）
29. 微信/QQ 扫描结果按账号文件夹分组（组头显示 账号 · 文件数 · 总量，组内按大小降序）；全选/全不选/已选统计/删除均不把组头算进去

**性能优化改版**
30. 页面分为“应用级优化 / 系统级优化”两档；进程列表每行显示作用说明（如“微信”“希沃白板”），“系统组件”有黄色徽标
31. 状态栏右侧“拦截 N”可点击 → 弹出白名单管理对话框（列表/添加/移除/最近拦截记录）；对话框修改后「性能优化」页白名单自动同步

**门户自动登录（实验性——仅预览版 main；正式版 stable 分支已编译期剔除）**
32. 配置账号 + 开启自动登录 → 认证过期时一键修复直接提交表单：提交成功即提示（是否真正在线由修复后自动复核实测判定，不内置登录后 NCSI 复核）；无法提交（纯 JS 门户/异常/超时）回退浏览器并说明原因
33. 纯 JS 渲染门户 → 提示“未找到标准登录表单”并回退浏览器（不误报成功）

**疑难排查层（全面诊断新增 8 项，只读）**
34. 全面诊断末尾出现“疑难排查”分组：IPv6 连通 / DNS 劫持与污染 / 网关级丢包 / 默认路由冲突 / 代理与直连差异 / TCP 53 出境 / 认证客户端检测 / 协议栈自检；快速体检与测速模式不出现
35. 模拟 DNS 劫持（改 Hosts 指向假 IP 后解析受控域名）→ hosts 项 FAIL；不存在域名被强制解析时 rare.dnshijack FAIL
36. 坏网环境下跑全面诊断 → 单项最多等约 25 秒（少数项 30-90 秒）即标记“检查超时（已强制中止）”并继续下一项，不再出现大片“待检查”滞留

**本轮新增：硬件健康 / IP 冲突 / 报告强制生成（网络诊断插件）**
37. 全面诊断 → 硬件驱动层出现“网络硬件探查”：正常机器 PASS（含各适配器双工/速率/驱动日期与错误包计数明细）；网络类设备（PNPClass='Net'，含 WAN Miniport/虚拟网卡）有真实感叹号的机器 FAIL 并列出设备名与人话错误码（历史幽灵设备 24/45 只记明细不计故障）；拔线换劣质网线使链路协商为半双工或 10M → WARN 且建议含更换网线/端口；AllowComputerToTurnOffDevice=2（Enabled）的网卡在明细中记节能线索但不计故障
38. 快速体检 → IP 配置层出现“IP 地址冲突检测”：正常环境 PASS（3 次 ARP 探测无冒用方）；验证冲突可在同网段另一台机器手配相同静态 IP 后复测 → 本机 FAIL 且建议含占用方 MAC；有 Tcpip 4199 历史记录但当前无现行 → WARN
39. 诊断完成 → 无需任何操作，reports\yyyy-MM-dd\ 下自动出现网络诊断报告 PDF，右下 InfoBar 提示“报告已自动生成”+ 完整路径，并带「补全信息」动作按钮（引导正式上报）；进度行追加“· 报告已自动生成”
40. 一键修复执行完毕 → 报告自动重新生成一份（含修复后状态），修复提示追加“报告已同步重新生成”
41. 自动生成失败场景（如 reports 目录被只读）→ InfoBar 红色“自动生成报告失败”+ 原因，诊断主流程结论不受影响；「重新导出 PDF 报告」按钮仍可手动补全信息导出
42. 表单记忆隔离：只跑自动生成（未点过手动导出）→ 手动导出对话框中年级/班级仍为上次手填值（自动生成的兜底值不回写记忆）
43. 报告准确性：快速体检/有线环境出报告 → 概览结论区分「实际检查 N 项、另有 M 项环境不适用自动跳过」（不再把跳过项混入“检查全部项目”），综合推断健康档同样注明跳过数；IP 冲突实锤的报告在「交叉验证」首条注明“属同网段地址占用、与本机硬件无关”的归属定界；测速未成功的报告首部链路速率改用主适配器协商速率兜底不再缺失
44. 强制填写信息覆盖（自动生成语义的补全闭环）：点「重新导出 PDF 报告」补全学校/年级/班级后生成 → 同一会话内不再操作直接跑「快速体检」→ 自动生成的新报告直接带已填年级班级（手动强制填写已覆盖表单记忆，后续自动生成读覆盖后的值，报告编号按日累计递增）；自动生成的兜底值（登录用户名/主机名/「未填写」占位）任何时候不回写表单记忆（与 42 双向互证）
45. 报告顶替（自动生成 → 要求填写信息 → 正式版替换兜底版；Task 82 修复顶替删除失效——历史版本按报告编号前缀 QS-DIAG- 校验文件名恒不命中，兜底版从未被真实删除，已改为「网络诊断报告_*.pdf 文件名 + 报告根目录」双保险校验）：自动报告 InfoBar 点「补全信息」（或右卡按钮）填写学校/年级/班级生成正式报告 → 本诊断会话的兜底报告（诊断后 + 修复后）从 reports 目录真实移除（兜底 .pdf 与同名 .summary.json 基线摘要一并消失），仅留正式版，LastReportPath/「打开所在文件夹」指向正式版；上一诊断会话的历史报告不受影响；兜底文件删除失败（占用/权限）仅日志告警，正式报告照常交付
46. 删除进度条(磁盘清理):常规清理执行中工具栏出现确定性进度条并按目标推进(状态行同步显示「正在清理 2/8:xxx」);微信/QQ 删除与大文件删除执行中列表下方出现进度条,状态行实时显示「正在删除 3/12:文件名(已处理 2.1 GB)」;全部取消后进度条立即复位隐藏,已完成部分按取消口径汇报
47. 回收站删除无错误核验:默认「删除进回收站」模式执行常规清理/微信/QQ/大文件删除 → 回收站内可见被删文件可还原;被占用的文件在结果中计为「失败/跳过」而非误报成功;清空回收站目标执行后逐分区回收站清空;切「直接删除」模式重跑 → 文件不进回收站直接消失(回收站容量不变)
48. 大文件扫描排除系统重要文件:全盘扫描结果中不出现 pagefile.sys/hiberfil.sys/swapfile.sys/bootmgr;Windows/Program Files/ProgramData/Recovery/PerfLogs/Config.Msi 目录与其嵌套同名目录不进入结果;微信/QQ/课程视频等用户大文件照常列出且可正常删除(AppData 自指 junction 不导致扫描卡死,有目录计数进度可见)
49. 微信/QQ/大文件显示优化:扫描中状态行滚动显示「已遍历 N 个目录·发现 M 个大文件·累计 X GB」(微信/QQ 为「已发现 N 个久远文件·累计 X」)不再静默干等;扫描完成列表按账号分组/大小倒序不变;删除结果带回收站/永久模式标注与失败原因提示(微信/QQ 运行中提醒退出后重试)
50. 内存优化进度与保护:整理所选内存执行中出现进度条与「正在整理 2/12:进程名」实时提示;svchost/csrss/lsass/Defender(MsMpEng)/搜索索引/打印服务等关键进程不出现在进程列表且无法被整理;整理完成后提示释放字节数,进程未被结束(窗口不关闭,列表占用数值回落)
51. 报告健康评分：结论概览标题下出现评分徽标（0-100 大字 / 100）与评级（优≥90 / 良≥75 / 中≥60 / 差<60）+ 扣分明细行（未通过 -15/项 · 警告 -5/项 · 已修复不扣分 · 跳过不计入）；结论句以「本次健康评分 N 分（X）」开头；全绿诊断 = 100 优，5 项未通过 = 25 差，扣分可在报告内按明细人工复核
52. 报告历史基线对比：第二次及以后生成报告时，结论概览尾部出现「与上一份报告对比」行（上一份编号/时间/模式 + 通过/警告/未通过/评分四组前后值 + 较上一份改善/下降/持平结论）；首次诊断无历史基线则不渲染本行；每份报告在 reports 目录留存同名 .summary.json 基线摘要；兜底报告被正式版顶替时其 .summary.json 同步移除
53. HTTPS 证书链校验（全面诊断专属）：证书链正常 = 通过并列出证书主体与签发者；上网行为管理 TLS 解密或自签 = 警告「HTTPS 证书不受信任」并说明本校部署行为管理属正常、未部署则核查出口设备；证书过期或未生效 = 警告并引导结合系统时钟校准；443 不通或握手未完成 = 跳过（连通性结论由 HTTPS/TLS 连通项负责，不重复报警）
54. 大文件类型徽章：大文件列表每行文件名右侧显示类型徽章（视频/音频/压缩包/镜像/虚拟机磁盘/安装包/文档/备份转储/邮件存档/数据库/无后缀，未知扩展名显示小写后缀名），徽章样式与微信/QQ 类型徽章一致（浅色深色主题与样式降级模板均正常渲染）
55. 扫描完成态汇总增强：微信/QQ 扫描完成显示「发现 N 个久远文件（M 个账号）· 共 X」并注明已按账号分组默认全选；大文件扫描完成显示「发现 N 个 ≥阈值 MB 的文件 · 合计 X，展示前 200 个最大的」——合计为全部命中文件体积而非仅展示部分
56. 网卡节能修复可达：速率与双工正常但节能托管开启的诊断结果（仅硬件探查线索提示）→ 修复计划出现「关闭网卡节能托管」动作可勾选执行；执行成功后重新诊断节能状态为关闭
57. 网关不通标记修正：网关完全不通时不再显示可自动修复标记（物理层断开无软件修复手段），建议文案保持引导检查网线/交换机端口/重启路由器
58. 正式版分支（stable）：checkout stable 后发布（csproj 定义 STABLE_BUILD，版本无后缀）→ 设置页「网络诊断」卡片不再出现「门户自动登录」开关与认证账号/密码输入（卡片其余设置项照常），一键修复的「打开城域网认证页面」动作仍正常（仅浏览器人工登录路径）；标题栏/关于页版本号无 -beta 后缀；main 预览版设置页开关组与自动登录修复路径一切照旧
59. U盘工具列为实验性：main 预览版左侧导航显示「U盘工具（实验性）」且页面顶部有实验性提示条（内置页与插件页均有）；stable 正式版左侧导航无 U盘工具入口、插件页手动加载被明确拒绝（提示「实验性功能：正式版构建不包含」）、启动自动加载静默跳过且日志只有一条 Info 而非 Error
60. U盘可见性诊断（识别但不显示·脱机）：磁盘管理中把U盘设为脱机 → 设备选中后「检测结论」出现异常「磁盘处于脱机状态」+修复清单含「联机磁盘」（管理员）→ 执行后重新枚举磁盘在线、盘符恢复显示
61. U盘可见性诊断（无盘符分区）：diskpart 对U盘分区 remove letter 后资源管理器不再显示 → 诊断异常「有 N 个分区未分配盘符」+「重建盘符映射」→ 执行后盘符恢复（多分区/非首分区场景也生效，PowerShell 优先 + diskpart 兜底）
62. U盘可见性诊断（策略隐藏）：注册表 HKCU Policies\Explorer 设 NoDrives 隐藏对应盘符 → 诊断异常「组策略隐藏了盘符」+ 清除策略（当前用户/全机）与「重启资源管理器」（默认不勾选）→ 执行后盘符重新显示，设备卡可见性结论行同步更新
63. U盘可见性诊断（RAW/无分区）：RAW 卷 → 诊断注意「卷文件系统无法识别」引导先 chkdsk 后重建；无分区U盘 → 诊断注意「未检测到任何分区」并说明重建入口；两者均不出现无效的「重建盘符映射」动作
64. 过滤器深度修复：存在第三方 UpperFilters/LowerFilters 时修复清单出现「清理存储类过滤器」（默认不勾选）→ 执行自动 reg export 备份到临时目录、仅移除第三方条目（partmgr/fvevol/volsnap 等微软内置保留）、提示重启生效
65. U盘工具布局优化：设备卡新增「诊断「识别但不显示」」按钮与可见性结论行（可见性异常/注意/正常三态）；修复动作区高度加高容纳新增动作；健康检测前即可见可见性诊断结论（选设备即出，无需先跑检测）
66. 启动提速（R2R）：`make_release.ps1` 默认以 `-p:PublishReadyToRun=true` 发布（控制台打印「R2R 预编译提速启动」），同机对比启动明显加快；加 `-NoR2R` 可关闭（打印不带 R2R 字样），产物与 ZIP 体积相应减小
67. 常驻占用：窗口失焦或最小化时仪表盘 3s 采样自动暂停（切回窗口后 3 秒内恢复刷新，数值不长期停滞）；应用启动时旧日志清理在后台执行，不再阻塞首帧
68. 修复后自动复核（闭环）：一键修复执行成功的动作 → 自动用全新网络快照重跑其关联检查——复检通过检查项才翻「已修复」（摘要「已修复，复检通过：…」），未通过保留红/黄并把展开证据更新为复检实测（摘要「修复动作已执行，但复检仍未通过：…」）；修复区提示汇总「复检通过 N 项，仍有 M 项未通过」；winsock（需重启生效）与 ARP 清缓存（表项需流量重新学习）两项不立即复核，按原因翻转并注明
69. 修复假成功根除（nic_powersave_off）：提权脚本 PowerShell 显式退出码传播——查询失败 / 全部关闭失败 → 动作标「失败」；此前 PowerShell 非终止错误恒退出 0 导致修复恒显「成功」而实际未生效（复检「节能托管：开启」依旧）
70. open_portal 语义（门户验证已删除）：动作只承诺「把认证页带到面前」——自动登录表单提交成功或回退打开浏览器均标「成功」；登录后 NCSI 连通性复核已删除（校园网常拦截/延迟放行 NCSI 域名，提交后立即复核易假阴性误报「账号可能输错」），是否真正在线由修复后自动复核的 portal.detect 复检给出实测结论（open_portal 提交后按 4 秒入账延时再复检）
71. 网卡驱动检查超时根治（hardware.nic_pnp）：逐卡 WMI 慢类查询（Win32_PnPEntity/Win32_PnPSignedDriver 各查 N 次 = N 次全表枚举，教学低配机 25 秒硬超时必触顶，且该检查纯本机 WMI 与网络无关）改为一次全网络类查询+内存匹配；检查硬超时升 40 秒；超时提示区分网络类与本机类原因（不再误导「多为网络无应答」）；WMI 枚举统一 20 秒硬超时（孤儿任务封顶，超出后按「信息不可得」降级）
72. 服务启动修复证据链（start_net_services v2）：Windows PowerShell 5.1 的 Get-Service 无 StartType 属性改用 Get-CimInstance Win32_Service StartMode 判禁用；启动后复核状态；失败明细（逐服务：已被禁用/启动失败/启动后未保持运行/不存在，ASCII 标记防编码歧义）经 ##DETAIL## 行映射为可读文案并入修复动作输出，不再一句笼统 FAIL；服务被禁用时按引导联系信息中心解锁启动类型
73. 内存防御收尾（切换标签页内存持续升高）：插件导航页 Unloaded 先退订 VM.PropertyChanged 再释放 VM；复测口径——1.4.0-beta 反复切换全部标签页 20 轮后，任务管理器内存应回到切换前水平上下（.NET GC 滞后允许小幅波动），而非持续单调上涨；此前 main 已有 ClearBackStack + Unloaded Dispose + VM Transient 链路（若旧版本实测请先升级）
74. 复检终态实测防误绿（nic_powersave_off）：ReverifyAsync 返回 ReverifyReport（检查项快照+复检会话布尔共享键终值）；nic_powersave_off 的关联检查（speed.nic_rate/hardware.nic_probe）对节能开启只记线索不判级、复检 Pass 不代表目标达成——共享键 nic.powersave 仍为 true 时把被翻绿项拦回为警告并给出手动处置路径（设备管理器电源管理页）；其余动作复检语义不受影响
75. portal.expiry 孤立复检防假红：internet.http.ok 键缺失（复核会话孤立跑本项，如前置 portal.detect 超时中止）时改判警告「门户可达，但本次复核会话无外网实测依据」，不再与 portal.detect 的实测绿结论自相矛盾；诊断主流程（Full 模式 detect 先行写入）原语义保留
76. 提权动作空网卡名显式失败：enable_adapter/adapter_reset/dns_set_public 在主网卡名称不可得时生成带原因的 FAIL 行（此前要么静默无标记被解析端误判「可能取消了 UAC」、要么拼出空名命令报错被吞）；FAIL 行带原因后缀时解析端把原因提为主文案
77. adapter_reset 延时改 ping：timeout /t 在 stdin 非控制台（隐藏窗口/服务环境）立即报错失效改 ping -n 4 127.0.0.1；修复完成后修复清单按复检后状态重建（复检通过/无复核语义翻绿的项不再挂着可重试，剩余问题生成新计划）

**本轮新增：更新链路 E2E 实测 + 机房一键装机 + 内置页徽章同步**
78. 更新链路端到端可用性（沙箱已实测 51/51，见 scripts/update_e2e_sim.py：真实本地 HTTP mock 服务器 + 真实文件系统安装段）——Windows 侧复验口径：发布新版（如 v1.5.1-beta）→ 应用内「立即检查更新」发现 → 下载（进度/速度可见）→ 退出后 update.cmd 解压覆盖 → 自重启：新 exe 就位；`plugins\` 用户自装插件、默认插件目录内用户附加文件、`plugin.sig` 全部保全（zip 外文件永不删除）；随包默认插件 plugin.json 刷新为随包版本；`%LOCALAPPDATA%` 用户数据（config/logs/reports/plugins-data）逐字节不受影响；下载残留（app-new/zip）清理干净；用户名含撇号（如 O'Brien）机器更新正常（zip/newDir 路径 PS 单引号双写转义）；中途杀掉 robocopy（errorlevel ≥8）→ 提示「更新失败，新版本已保存在下载目录」且新包保留、不重启、旧版本可继续运行，修正后原样重试幂等成功
79. 机房一键装机（scripts/deploy_install.ps1，沙箱已仿真 48/48，见 scripts/deploy_sim.py）：共享目录放发布 zip + `.sha256`（make_release.ps1 自动生成）+ 装机脚本 → 一条命令完成首装；哈希不符/缺 sidecar/缺 -ExpectedHash 中止（拒绝未核验包）；sidecar 文件名与 zip 不符中止（防错放）；D 盘优先无 D 盘退 C:\Tools（-InstallRoot 可指定）；已安装未加 -Force 跳过解压（保留 plugins 用户资产），-Force 覆盖式增量（zip 外文件保留）；-WithConfig 基线不覆盖本机已有配置；-Autostart 写 HKCU Run 键；安装摘要（目录/版本/SHA256/七插件/配置来源/自启）可点名；任一步失败打印已完成步骤、修正后重跑从未完成处续装
80. 大文件类型徽章内置回退页同步：卸载磁盘清理插件回退内置磁盘清理页（或直接使用内置页）时，大文件行文件名右侧同样显示类型徽章（视频/音频/压缩包/镜像/虚拟机磁盘/安装包/文档/备份转储/邮件存档/数据库/无后缀），样式与插件页一致（InfoChipFillBrush 浅色/深色主题均正常）——此前仅插件页有徽章（0b95a22 遗留项）

**本轮新增：全部功能端到端验证（静态全链路门禁 + 逻辑级/协议级仿真 831 检全绿）**
81. 插件加载放行口径修复（plugin_sim.py 68/68 实锤驱动）：未签名插件（State=Unsigned）手动与自动加载均被旧口径 `!verify.Ok` 拒绝（「缺少 plugin.sig（未签名）」），与插件页 CanLoad（VerifyOk = Ok or Unsigned）、AutoLoadEnabledAsync 过滤器、SDK README「未签名插件默认放行加载」三处承诺相悖——已改为 Ok|Unsigned 放行（防篡改红线不变：BadSignature/HashMismatch/IdMismatch/HostTooOld/ManifestInvalid 仍一律拒绝）；沙箱复刻验签全状态机验证：RSA-2048+SHA-256+PKCS#1 v1.5 数学级验签（官方公钥指纹复刻 = 84:6C:65:C7:BD:D7:10:29，与插件页展示口径一致）、签名后篡改清单/程序集一字节必拒、防冒名/哈希锁定/宿主版本门逐项过
82. 宿主磁盘清理回退页完成态汇总与插件版同源（cleanup_scan_sim.py 68/68 同源断言驱动）：微信/QQ 完成态补「（N 个账号）· 已按账号分组」、大文件完成态补「合计 X」——此前插件版有、宿主内置回退页缺失（0b95a22 汇总增强只落了插件侧，2fbdd90 只同步了徽章）；验收口径：卸载磁盘清理插件回退内置页，扫描完成文案与插件页逐字一致
83. 僵尸配置项移除（settings_sim.py 41/41 全库引用扫描实锤）：AppSettings.CleanupExcludeDirs 与 FirstRunDone 自 v1.0.0 起零引用（宿主/插件/文档均无消费点），已删除；旧机器 config.json 中的对应键由 System.Text.Json 未知字段忽略语义自然兼容（roundtrip 41 项实测：中文明文落盘、键大小写不敏感、未知字段容忍、任一字段类型不符整份回退默认、.tmp 原子写崩溃不留半截配置）；PopupRule 三组模式为注释明示保留项，不在清除范围
84. 全功能端到端验证矩阵（scripts/e2e_all.py 一键全量）：12 项静态门禁 + 16 套行为/逻辑仿真（新增插件 68 / 设置 41 / 清理 68 / 优化 35 / 仪表盘 27 / Gala 115 六套，合既有 10 套）合计 831 检，28 个功能维度全 PASS；Gala 套件含真实 socketpair 协议闭环（hello→welcome→metrics→switch→ack）与生产拓扑（飞线二中 49 节点）Normalize/Validate/MatchRoute 实测；沙箱无 dotnet/Windows，编译与真机运行仍按各功能既有 Windows 实测条目执行
85. 发布包默认插件随包自动构建（make_release.ps1 §2.5）：bin 下无产物的插件工程自动 dotnet build（显式 -p:QingshuHome= 指向宿主发布目录，插件 csproj 五级探测兜底），构建失败即中止发布（宁可不发不发残缺包）；-SkipPlugins 显式发布纯宿主包。Windows 验收：删掉任一插件 bin 后跑 make_release.ps1 -PackOnly，观察「自动 dotnet build」提示且包内 plugins\ 含该插件
86. plugin.json 清单装载：从插件工程根 plugin.json.template 装载落为 plugin.json（旧口径从 bin 找 template 永远落空——插件工程未设 CopyToOutputDirectory，装载出的插件缺清单宿主无法加载）；仅缺失时补建，用户已改配置与 plugin.sig 不被重置（与 build_all_plugins.ps1 装载语义一致）。Windows 验收：包内每个 plugins\<id>\ 必有 plugin.json，插件页七插件全部加载
87. 正式版剔除实验性插件：宿主 csproj 定义 STABLE_BUILD 时（正式版构建），qingshu.plugin.usbtool 不入包（与 AppBuildProfile.UsbToolEnabled=false 及宿主三重门禁同口径），日志打印剔除提示；预览版（main，无 STABLE_BUILD）七插件全量入包。Windows 验收：stable 分支打包后包内 plugins\ 无 qingshu.plugin.usbtool 目录且含其余 6 个
88. 插件 csproj 宿主引用指向托管主程序集（全部 8 个插件工程含 Sample）：自包含 publish 目录的 QingShu.Toolkit.exe 是原生 apphost（无 CLI 元数据），旧 HintPath 指向 exe 必致 MSB3246「PE image does not have metadata」+ CS0234/CS0246 级联（make_release.ps1 自动构建 GalaMaster 时 Windows 实测踩中）；现 HintPath 统一改 $(QingshuHome)\QingShu.Toolkit.dll（R2R 编译过的托管主程序集元数据完整可引用；exe 存在性仅用作 QingshuHome 探测标志不作引用目标）；防回归锚点 check_runtime_v3 plugin_ref_audit（8 个 csproj 全扫：exe HintPath 必 FAIL / dll HintPath 缺失必 FAIL / 零工程扫到必 FAIL）。Windows 验收：make_release.ps1 自动构建七插件（stable 六个）全部成功，插件页各插件加载正常
89. gala 四工程共享核心 Compile Include 完整（两插件 + 两控制台）：core/Gala 共享源码以 Compile Include 链接，工程代码引用了 GalaFile/GalaEventLog/GalaHook 而其所在文件未链入即 CS0103——main 分支 make_release.ps1 自动构建 GalaMaster 时 Windows 实测炸出 15 错（4×CS0103 缺链入 + CS0029 ValueTask void await / SendAsync 静态调实例方法 / 数组裸 Count 方法组 / ValueTask.WaitAsync 不存在 + 面板 CS0266 SelectionMode / CS0234 UI.Text.TextAlignment / CS1503 Grid.SetRow 收 FrameworkElement / 快捷动作 lambda 参数 _ 是 CancellationToken 时体内 _= 赋值 + GalaAgent 面板缺三参 Grid.Add 扩展），本条随修复入库；防回归锚点 check_runtime_v3 gala_include_audit（四工程全扫：按「代码引用类型 → 所在核心文件」闭包核对 include 集，缺链入必 FAIL / 链入路径不存在必 FAIL；阴性验证：删任一 include 门禁即红）。Windows 验收：make_release.ps1 自动构建 GalaMaster 与 GalaAgent 编译零错误，插件页两 gala 插件加载正常
90. WinUI 插件 csproj PRI 生成整链关闭（全部 7 个 UseWinUI 插件工程显式 AppxGeneratePriEnabled=false）：dotnet CLI 构建时 MrtCore.PriGen.targets 的 AddPriPayloadFilesToCopyToOutputDirectoryItems 在引用闭包出现 .pri 即调 ExpandPriContent，其程序集 Microsoft.Build.Packaging.Pri.Tasks.dll 只随 Visual Studio 安装、dotnet SDK 目录没有 → MSB4062（首次编译成功即触发该链——main 分支 make_release.ps1 自动构建 GalaMaster 编译零错误后 Windows 实测踩中；其余六插件因 bin 已有产物被跳过未暴露）。插件全是纯代码构建（零 resw / 零 XAML / 零 PRIResource）本就无需 .pri，且 make_release 装载只取 dll + plugin.json，关闭 PRI 不改变发布产物；GalaFrame.WriteFrameAsync 同轮连带修正——Stream.WriteAsync 全部重载 await 结果均为 void（返回字节数是 ReadAsync 的语义），上一轮误记 (byte[],int,int,ct) 重载为 Task<int> 致残留 CS0029，改整帧一次写入（Stream 契约「写完或抛异常」，NetworkStream 底层部分发送内部循环补齐），gala_sim 写帧锚点同步改正确语义并加哨兵断言全树不存在 "= await stream.WriteAsync"；防回归锚点 check_runtime_v3 plugin_pri_audit（7 个 UseWinUI csproj 全扫：缺 AppxGeneratePriEnabled=false 必 FAIL / 零工程扫到必 FAIL；阴性验证：删任一属性门禁即红并点名）。Windows 验收：make_release.ps1 全链路（宿主 publish + 两 gala 插件自动构建零错误 + 装载 + zip + .sha256）一次通过

91. WinUI 插件关闭 PRI 生成还需同时设置 EnableCoreMrtTooling=false：AppxGeneratePriEnabled=false 仅关闭 PRI 生成目标，MrtCore.PriGen.targets 仍会在 CopyLocalFilesOutputGroup 调用 Visual Studio 专属 RemovePayloadDuplicates（dotnet SDK 缺 Microsoft.Build.AppxPackage.dll）并报 MSB4062。七个纯代码 WinUI 插件统一关闭 Core MRT tooling；Windows 验收：不传额外 MSBuild 属性，GalaMaster dotnet build 成功。
92. 正式版发布清理实验插件残留：stable 打包前删除 publish\plugins\qingshu.plugin.usbtool，避免此前插件安装到发布目录的旧文件绕过正式版插件清单过滤；Windows 验收：stable ZIP 与 MSIX 均含其余 6 个默认插件，不含 USB Tool；main ZIP 含 7 个插件。
93. 插件切换三处原生崩溃面根治（用户 Windows 实测「切换插件闪退」，闪退无日志 = 原生 stowed-exception 0xc000027b 类，托管 UnhandledException 捕获不到，与 App e.Handled=true 吞托管异常的行为互证）：①PluginNavHostPage.OnNavigatedFrom 不再 Content=null——Frame 换页过渡窗口内新旧内容共存，此刻强拆视觉树是 WinUI 3 原生布局竞态崩溃面（E_INVALIDARG → fail-fast），本页 transient + 宿主导航后即清 BackStack，旧页整体失去引用无需手动断链；②MainWindow 导航在途守卫（_navInFlight + NavigationCompleted/Failed 复位 + 排队重放丢回下一条 UI 消息）：过渡动画未完成时二次 Frame.Navigate 是同族原生竞态面，快速连点导航项或插件加载/卸载触发的 SyncPluginDelegation 撞上用户导航均会踩中；NavigateToPluginNav 同时补同 tag 短路（同页重入不再重复建视图）与挂载失败回退（try/catch 记日志回插件管理页）；③PluginManager 卸载策略按 UiCreated 分流：创建过导航页/面板视图的插件软卸载（断引用移出运行集，ALC 驻留待 GC）——WinUI 3 不支持回收承载过 XAML 对象的 collectible ALC，Alc.Unload+强 GC 后原生残留引用随时 AV；未创建过 UI 的插件维持硬卸载完全回收。Windows 验收：加载 3 个以上插件后在插件导航页/内置页/插件管理页之间连续快速切换 30 秒无闪退；卸载正在显示过导航页的插件后立即重新加载并再次进入，无闪退；应用日志出现「加载上下文驻留防原生崩溃」字样（UI 插件卸载路径）

## C. 已知边界（设计使然，非缺陷）

- QuestPDF 社区许可声明仅在生成 PDF 时设置（App ctor）
- 日志预览只读尾部 600 行，防止大文件卡 UI
- 测速节点全部不可达时回退分段延迟画像归因（不阻塞诊断主流程）
- 更新包安装依赖 update.cmd 覆盖重启，属于"退出即装"模型，不做静默安装
