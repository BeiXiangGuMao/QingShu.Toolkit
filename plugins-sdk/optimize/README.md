# 系统优化插件 · qingshu.plugin.optimize

内置工具真外移第四批。`manifest.navTag = "optimize"`（对齐宿主 MainWindow 导航 Tag 字面值），
插件加载后接管宿主左侧「性能优化」导航页；插件卸载 / 停用后宿主自动回退内置实现
（内置页与 DI 保留兜底）。

## 接管语义

- 加载成功：导航「性能优化」进入本插件的 `OptimizeView`（每次导航新建视图与 VM，
  与内置页 transient 语义一致；VM 构造即拉起进程/启动项两次刷新，与内置页同语义）。
- 卸载 / 停用 / 加载失败：宿主自动回退内置 `OptimizePage`。
- 无设置迁移需求：本页 VM 与两服务均无宿主设置/日志依赖（插件化最干净的一批）；
  启动项禁用备份**沿用宿主同路径**（`%LocalAppData%\QingShu.Toolkit\startup_disabled.json`
  与 `startup_disabled_files\`）——内置/插件共用，插件卸载后内置页接管同一份备份记录
  （usb 批隔离区同款口径）。

## 自包含移植清单（与宿主内置实现逐行对应）

| 插件文件 | 宿主来源 | 说明 |
| --- | --- | --- |
| `OptimizeInterop.cs` | `Helpers/FormatHelper`、`Interop/NativeMethods`（EcoQoS + 工作集两区段）、`Interop/ElevationHelper`（仅 IsCurrentUserAdmin）、`Helpers/ProcessKnowledge` | 字节格式化 / SetProcessInformation EcoQoS（失败原因 os·open·set 细分）/ psapi EmptyWorkingSet / HKLM 权限预检 / 76 进程作用知识库（教学场景全覆盖） |
| `OptimizeServices.cs` | `Services/Optimize/MemoryOptimizeService.cs`、`StartupListService.cs` | 内存整理（受保护进程集 / 释放字节统计）/ 启动项管理（HKCU/HKLM Run 键 + 启动文件夹，禁用可逆备份，权限不足明确提示） |
| `OptimizeViewModel.cs` | `ViewModels/OptimizeViewModel.cs` | VM 全量移植；历史护栏原样（EcoReady 补发通知防按钮永久禁用 / 命令内接异常防全局吞错等）；**顺手修正一处内置潜伏缺陷**：`EcoApplied` 由普通属性改为可观察属性——应用节能后「EcoQoS」徽章即时显示（内置页该徽章不刷新） |
| `OptimizeView.cs` | `Views/OptimizePage.xaml`（210 行） | 纯代码 UI：ScrollViewer 单列流式布局（页头 + 两区块标题 + 三卡 + 边界 InfoBar） |

## 运行时模板（与磁盘清理批同一实锤）

列表保留 `ListView` 虚拟化，DataTemplate 经 `XamlReader.Load` 运行时解析，经典 Binding +
行 VM 的 Visibility 类型属性（`IsSystemVisState` / `EcoVisState` / `EnabledVisState` /
`DisabledVisState`）+ 样式键缺失两级降级。细节与护栏见
`plugins-sdk/disk-cleanup/README.md`「运行时模板」节（三条实锤共用，不再重复）。

启动项行模板内的 `Command="{Binding Owner.ToggleStartupCommand}"` +
`CommandParameter="{Binding}"`：经典 Binding 支持子属性路径，`{Binding}` 空路径即行对象
本身——与内置页 `x:Bind Owner.Xxx` + `CommandParameter="{x:Bind}"` 语义逐字对应。

## 构建与组装

```powershell
dotnet build plugins-sdk/optimize/QingShu.Plugin.Optimize -c Release -p:Platform=x64 -p:QingshuHome="<宿主发布目录>"

# 组装插件目录 plugins\qingshu.plugin.optimize\：plugin.json（由 template 填充）+ 主 dll/pdb（发现式拷贝）
Get-ChildItem plugins-sdk/optimize/QingShu.Plugin.Optimize/bin -Recurse -Include QingShu.Plugin.Optimize.dll,QingShu.Plugin.Optimize.pdb |
  Where-Object { $_.FullName -notmatch '\\obj\\' } | Copy-Item -Destination <发布目录>\plugins\qingshu.plugin.optimize\ -Force
```

`scripts/make_release.ps1` 第 2.5 步已把本插件加入默认装载清单（SdkRoot = `plugins-sdk\optimize`）。

## 权限与安全边界

- `permissions: ["storage"]`：启动项备份文件读写（宿主同路径）。
- 安全铁律原样：工作集整理不结束进程；EcoQoS 可逆且失败可解释；启动项只动三类标准位置、
  禁用全可逆、不触碰计划任务/服务/WMI 订阅；不改虚拟内存/不导入注册表/不关 Defender。
