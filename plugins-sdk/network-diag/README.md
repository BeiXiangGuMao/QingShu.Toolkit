# 网络诊断插件 · qingshu.plugin.networkdiag

内置工具真外移第五批。`manifest.navTag = "network"`（对齐宿主 MainWindow 导航 Tag 字面值），
插件加载后接管宿主左侧「网络诊断」导航页；插件卸载 / 停用后宿主自动回退内置实现
（内置页与 DI 保留兜底，与弹窗拦截 / U盘工具 / 磁盘清理 / 性能优化外移同一策略）。

## 接管语义

- 加载成功：导航「网络诊断」进入本插件的 `NetworkDiagView`（每次导航新建视图、VM 与四服务）；
  导航项图标与位置不变。
- 卸载 / 停用 / 加载失败：宿主自动回退内置 `NetworkDiagnosticsPage`，在途诊断 / 修复随
  VM Dispose 取消。
- 服务生命周期：宿主内置诊断服务为 DI 单例（LastContext 并发写入兜底）；插件版改为
  每导航新实例、不共享 LastContext，语义等价且无并发交叉写风险（旧操作随视图 Unloaded
  → VM.Dispose 取消，新会话天然隔离）。
- 设置交接：插件私有 `settings.json`（PDF 报告表单记忆 Last* 五项 + 宿主诊断设置只读镜像），
  首次启动从宿主 `config.json` 一次性迁移同名键（幂等：settings.json 建立后不再迁移）；
  诊断开始前 `RefreshDiagFromHost` 重读宿主诊断域字段（门户/DNS/测速参数等，插件绝不回写）。
  插件卸载回退内置页后，内置页继续读写宿主 config.json，互不破坏。

## 自包含移植清单（与宿主内置实现逐行对应）

| 宿主来源 | 插件文件 | 说明 |
| --- | --- | --- |
| 宿主 `QingShu.Toolkit.csproj` 口径 | `QingShu.Plugin.NetworkDiag.csproj` | TFM / WASDK 投影 / MinVersion 与宿主一致（跨 ALC 类型同一）；CommunityToolkit.Mvvm / System.Management / System.ServiceProcess.ServiceController / QuestPDF 仅编译期引用（ExcludeAssets=runtime，运行期共享宿主同版本程序集） |
| `Helpers/FormatHelper`、`Helpers/ProcessRunner`、`Interop/ElevationHelper` | `NetworkDiagInterop.cs` | 字节/时长格式化、UTF-8 命令执行（netsh/arp/route/tracert/netstat）、UAC 提权脚本（1223 哨兵）；NativeKernel32 / NativeWinINet 与诊断会话双文件日志（DiagSessionLog）为本插件自包含补齐段 |
| `Models/Diagnostics.cs`、`Models/Speed.cs`、`Models/NetworkTopology.cs` | `NetworkDiagModels.cs` | CheckItem / RepairAction（含 Visibility 类型 VisState 派生属性，供运行时模板经典 Binding）/ SlowCause / SpeedTestResult / NetworkTopology 快照模型 |
| `Services/Network/*`（编排 + 43 项检查 + 会话日志 + 拓扑构建） | `NetworkDiagCore.cs` | 诊断引擎三模式计划 + 逐项硬超时 + 一键修复计划（管理类合并一次 UAC）+ NetworkTopologyBuilder 纯函数构建 |
| `Services/Reporting/PdfReportService.cs`、`Services/Reporting/ReportInference.cs`、`Services/HardwareProbeService.cs` | `NetworkReporting.cs` | QuestPDF 正式报告模板（中文注册字体/编号按日累计/分层明细/修复记录/综合推断/分级处置建议）+ 设备事实探测（序列号/MachineGuid/CPU/内存/网卡） |
| `ISettingsService` 诊断域字段 + Last* 五项 | `NetworkDiagSettings.cs` | 插件私有 settings.json（原子写/损坏回退默认）+ 宿主 config.json 一次性迁移 + 诊断设置只读镜像刷新 |
| `ViewModels/NetworkDiagViewModel.cs` | `NetworkDiagViewModel.cs` | VM 全量移植；诊断/修复/报告/探测四服务对接插件实现；设置对接插件 store；日志适配 `Action<string,string>`；护栏原样（诊断与修复取消源分立、修复成功转 Fixed、open_portal 不翻转、winsock 标注重启生效、修复后重建拓扑、修复提权中禁重开诊断） |
| `Views/NetworkDiagnosticsPage.xaml` + `.xaml.cs`（373 + 272 行） | `NetworkDiagView.cs` | 纯代码 UI 七行逐区对应：页头 → 进度卡 → 实时测速卡 → 低速原因分析卡 → 网络拓扑 Canvas 卡 → 检查结果列表 → 修复 + 导出双卡；PDF 导出五字段 ContentDialog 逐字对应 |
| ——（本批新增） | `NetworkDiagPlugin.cs`、`plugin.json.template` | 插件入口与清单（navTag = network，接管「网络诊断」导航页） |

## 报告输出目录与日志口径（与宿主共用）

- PDF 报告统一落 `%LocalAppData%\QingShu.Toolkit\reports\yyyy-MM-dd\`（宿主内置版写「我的文档」，
  插件版统一改本地数据根 reports 子目录），「打开所在文件夹」explorer /select 选中该文件。
- 诊断会话日志沿用宿主同口径写入 宿主数据根`\logs\yyyy-MM-dd\diag-yyyyMMdd-HHmmss-NNN.log|jsonl`
  双文件；页脚「诊断日志按日期与次数分文件留存」展示同一位置，宿主日志查看器两态可读。

## 运行时模板与 Canvas 拓扑说明

检查项 / 修复动作 / 低速嫌疑三个列表保留 `ListView` 虚拟化，DataTemplate 经 `XamlReader.Load`
运行时解析（零 XAML 编译依赖不变），并落实三条实锤：

1. **经典 Binding 而非 x:Bind**：`x:Bind` 是编译期生成器行为，运行时模板只能用 `{Binding}`；
   行 VM 实现的 INotifyPropertyChanged（CommunityToolkit 源生成器）经典 Binding 可正常感知。
2. **bool→Visibility 无隐式转换**：内置页状态 Chip 用 `{x:Bind PassVis}`（x:Bind 隐式转
   Visibility）；经典 Binding 无此转换，行模型直接暴露 `Visibility` 类型属性
   （`PassVisState` / `WarnVisState` / `FailVisState` / `FixedVisState` / `SkippedVisState` /
   `RunningVisState` / `PendingVisState` / `SuggestionVisState` / `DetailsVisState` /
   `RiskyVisState` / `OkVisState` / `FailVisState` / `HighVisState` / `MidVisState` /
   `LowVisState`，Microsoft.UI.Xaml.* 跨 ALC 类型同一），模板绑定免转换器；低速嫌疑三档在
   Models 为 bool 属性，已同步补 Visibility 三态属性。视图层（非模板）绑定沿用本地
   `IValueConverter`（Vis 辅助）。
3. **样式两级降级**：模板内的 `StaticResource`/`ThemeResource`（CardStyle / 各文本样式 /
   主题色刷）在 XamlReader 解析期对 Application.Resources 求值；宿主样式键缺失时解析抛错 →
   自动降级为无资源模板（默认前景 + 半透明兜底色），构造期一次性选定并留插件日志。

Canvas 拓扑逐行移植内置 code-behind：连线（Line，虚线段 StrokeDashArray）+ 连线标注 +
节点（Border 徽标/标题/状态 + ToolTip 完整证据）+ 底部图例；节点/连线颜色沿用内置页本地
FromHex 调色板（深浅主题各一套），文本刷退化为 Application.Resources 当前变体 + 本地兜底色
（宿主内部主题解析服务不进插件）；`VM.Topology` 变化触发重画，`ActualThemeChanged` 按新主题
重画。诊断完成 / 取消（部分结果灰节点）/ 修复后（Fixed 映射绿色）三个时机均自动重建。

## 构建与组装

```powershell
# 前置：宿主已发布（dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true）
dotnet build plugins-sdk/network-diag/QingShu.Plugin.NetworkDiag -c Release -p:Platform=x64 -p:QingshuHome="<宿主发布目录>"

# 组装插件目录 plugins\qingshu.plugin.networkdiag\：plugin.json（由 template 填充）+ 主 dll/pdb
# 主 dll 用发现式拷贝（dll 路径随 SDK/平台在 bin\x64 与 bin 间漂移，硬编码必踩）：
Get-ChildItem plugins-sdk/network-diag/QingShu.Plugin.NetworkDiag/bin -Recurse -Include QingShu.Plugin.NetworkDiag.dll,QingShu.Plugin.NetworkDiag.pdb |
  Where-Object { $_.FullName -notmatch '\\obj\\' } | Copy-Item -Destination <发布目录>\plugins\qingshu.plugin.networkdiag\ -Force
```

宿主 `scripts/make_release.ps1` 的默认装载清单当前登记至第四批（disk-cleanup / optimize）；
本插件发布时在第 2.5 步数组仿照 optimize 追加一行：
`@{ Id = "qingshu.plugin.networkdiag"; Sdk = "QingShu.Plugin.NetworkDiag"; SdkRoot = "plugins-sdk\network-diag" }`。

## 权限与安全边界

- `permissions: ["storage"]`：插件私有数据目录（settings.json 报告表单记忆）。
- 只读诊断为主：全部检查项为探测/读取类操作（WMI / 注册表只读 / ping / tracert / HTTP 探测 /
  下载测速只读不落盘不上传），不改系统配置。
- 一键修复安全铁律与内置页逐字一致：管理类操作合并为一次 UAC 提权批处理（1223 哨兵回传）、
  危险项（需重启/谨慎）默认不勾选、open_portal 成功不翻转认证状态、winsock 重置标注重启生效、
  修复与诊断取消源分立互不误伤。
- 无新增宿主接口依赖：仅契约接口（IQToolkitPlugin / IQToolkitNavPageProvider / IPluginContext）。
