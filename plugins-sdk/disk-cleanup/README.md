# 磁盘清理插件 · qingshu.plugin.diskcleanup

内置工具真外移第三批。`manifest.navTag = "cleanup"`（对齐宿主 MainWindow 导航 Tag 字面值），
插件加载后接管宿主左侧「磁盘清理」导航页；插件卸载 / 停用后宿主自动回退内置实现
（内置页与 DI 保留兜底，与弹窗拦截 / U盘工具外移同一策略）。

## 接管语义

- 加载成功：导航「磁盘清理」进入本插件的 `DiskCleanupView`（每次导航新建视图与 VM，
  与内置页 transient 语义一致）；导航项图标与位置不变。
- 卸载 / 停用 / 加载失败：宿主自动回退内置 `DiskCleanupPage`，正在进行的操作随 VM Dispose 取消。
- 设置交接：插件私有 `settings.json`（久远阈值天数 / 大文件阈值 MB / 删除是否进回收站），
  首次启动从宿主 `config.json` 一次性迁移同名设置（幂等：settings.json 建立后不再迁移）；
  插件卸载回退内置页后，内置页继续读写宿主 config.json，互不破坏。

## 自包含移植清单（与宿主内置实现逐行对应）

| 插件文件 | 宿主来源 | 说明 |
| --- | --- | --- |
| `CleanupInterop.cs` | `Helpers/FormatHelper`、`Helpers/FileOpsHelper`、`Interop/NativeMethods`（回收站区段）、`Interop/ElevationHelper` | 字节格式化 / 目录体积统计与逐文件安全删除 / SHQueryRecycleBin 逐分区查询 / SHEmptyRecycleBin / STA SHFileOperation（FOF_ALLOWUNDO，16K 字符预算分批）/ UAC 提权脚本（1223 哨兵） |
| `CleanupModels.cs` | `Models/Cleanup.cs` | CleanRisk / CleanTargetDef（14 目标）/ CleanupReport / ImFileItem / LargeFileInfo |
| `CleanupServices.cs` | `Services/Cleanup/CleanupService.cs`、`ImScanService.cs`、`LargeFileScanService.cs` | 清理引擎（扫描→勾选→逐文件删除；提权目标合并一次 UAC；回收站实时复查 + STA 批量 + 逐项复核）/ 微信 QQ 久远文件（新旧版目录布局，绝不触碰聊天记录数据库）/ 大文件扫描（跳过系统目录，120s / 800 条上限） |
| `CleanupSettings.cs` | `ISettingsService` 三项设置 | 插件私有 settings.json + 宿主 config.json 一次性迁移（popup-guard 模式） |
| `CleanupViewModel.cs` | `ViewModels/CleanupViewModel.cs` | VM 全量移植；设置适配插件 store；日志适配 `Action<string,string>`；历史护栏原样（扫描结果必须携带进清理 / 清理后重扫 / 全选挂起逐行事件等） |
| `DiskCleanupView.cs` | `Views/DiskCleanupPage.xaml`（351 行） | 纯代码 UI：页头 + Pivot 四标签 + 底部状态卡 |

## 与前两批的一个关键差异：运行时模板保虚拟化

U盘工具批用「手工行重建」（列表 ≤ 10 行）；本批微信 / QQ 久远文件可达数千行，
无虚拟化的 StackPanel 重建会卡死 UI。故列表保留 `ListView` 虚拟化，DataTemplate 经
`XamlReader.Load` 运行时解析（零 XAML 编译依赖不变），并落实三条实锤：

1. **经典 Binding 而非 x:Bind**：`x:Bind` 是编译期生成器行为，运行时模板只能用
   `{Binding}`；行 VM 实现的 INotifyPropertyChanged（CommunityToolkit 源生成器）经典
   Binding 可正常感知。
2. **bool→Visibility 无隐式转换**：经典 Binding 没有 x:Bind 的 bool→Visibility 隐式转换，
   行 VM 直接暴露 `Visibility` 类型属性（`AdminVisState` / `HeaderVisState` / `FileVisState`，
   Microsoft.UI.Xaml.* 跨 ALC 类型同一），模板绑定免转换器；视图层（非模板）绑定沿用
   usb 模式的本地 `IValueConverter`。
3. **样式两级降级**：模板内的 `StaticResource`（CardStyle 等）在 XamlReader 解析期对
   Application.Resources 求值；宿主样式键缺失时解析抛错 → 自动降级为无资源模板
   （默认前景 + 半透明兜底色），构造期一次性选定并留插件日志。

## 构建与组装

```powershell
# 前置：宿主已发布（dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true）
dotnet build plugins-sdk/disk-cleanup/QingShu.Plugin.DiskCleanup -c Release -p:Platform=x64 -p:QingshuHome="<宿主发布目录>"

# 组装插件目录 plugins\qingshu.plugin.diskcleanup\：plugin.json（由 template 填充）+ 主 dll/pdb
# 主 dll 用发现式拷贝（dll 路径随 SDK/平台在 bin\x64 与 bin 间漂移，硬编码必踩）：
Get-ChildItem plugins-sdk/disk-cleanup/QingShu.Plugin.DiskCleanup/bin -Recurse -Include QingShu.Plugin.DiskCleanup.dll,QingShu.Plugin.DiskCleanup.pdb |
  Where-Object { $_.FullName -notmatch '\\obj\\' } | Copy-Item -Destination <发布目录>\plugins\qingshu.plugin.diskcleanup\ -Force
```

`scripts/make_release.ps1` 第 2.5 步已把本插件加入默认装载清单（SdkRoot = `plugins-sdk\disk-cleanup`）。

## 权限与安全边界

- `permissions: ["storage"]`：插件私有数据目录（settings.json / 清理报告 JSON）。
- 清理目标与安全铁律与内置页逐字一致：逐文件 try/catch 绝不整目录粗暴删除、
  系统目录删后重建、Windows.old 先 takeown/icacls、回收站 STA 操作、
  微信/QQ 数据库文件（.db/.sqlite 等）绝不列出或删除、大文件扫描跳过系统目录。
- 清理报告落盘改为插件私有 `DataFolder\reports\yyyy-MM-dd\`（结构与宿主 SaveCleanupReport 同口径）。
- 无新增宿主接口依赖：仅契约接口（IQToolkitPlugin / IQToolkitNavPageProvider / IPluginContext）。
