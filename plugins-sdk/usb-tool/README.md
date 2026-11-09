# U盘工具插件（qingshu.plugin.usbtool）—— 内置工具真外移第二批

内置「U盘工具」页面的插件化版本，与弹窗拦截（第一批）同一 harness 架构：宿主薄壳 + 动态加载 + 导航接管 + 内置兜底。

## 接管语义

- `manifest.navTag = "usb"`：插件加载后接管宿主左侧「U盘工具」导航页（导航项图标位置不变）。
- 插件未安装 / 已卸载 / 加载失败：自动回退宿主内置实现（内置页保留兜底）。
- 每次导航新建视图与 VM（与内置页 transient 语义一致）；视图 Unloaded 即取消在途破坏性操作
  （扩容检测 / 表面扫描 / 重建分区在步骤边界生效，不会打断已开始的写入块）。

## 功能与内置版逐项对应

| 能力 | 说明 |
| --- | --- |
| 设备探查 | WMI 物理盘 → 分区 → 逻辑卷三级关联（USBSTOR 过滤，系统盘天然隔离）+ STORAGE_DEVICE_DESCRIPTOR 描述符兜底 |
| 健康检测（只读） | 尾段只读探测 + 表面读扫描（限时 150s）+ 读吞吐评估 + 写速度实测（写穿模式） |
| 扩容盘检测（破坏性） | 4 探针点写+回读校验（4MB 特征数据），二次卸载卷防缓存伪装；二次确认对话框 |
| 查杀（只读） | U盘蠕虫落盘特征：autorun 自启 / 双扩展名伪装 / 同名文件夹模仿 / .lnk 劫持（字节级）/ 回收站落体 |
| 处置 | 隔离（可恢复，manifest 清单）/ 解除隐藏（attrib 全盘）/ 免疫目录（autorun.inf 目录占位） |
| 修复 | 设备栈重置（PnP）/ 卷配置刷新 / chkdsk / 写保护清除 / 盘符重建 / 分区表重建（危险项单独确认） |

安全铁律不变：只对 USBSTOR 设备操作；系统卷绝不参与破坏性动作；破坏性操作均由 UI 二次确认后调用。

## 自包含移植清单（零宿主内部依赖）

| 文件 | 内容 | 来源 |
| --- | --- | --- |
| `UsbInterop.cs` | FormatHelper.Bytes / WmiHelper / NativeStorageIo / ElevationHelper / ProcessRunner | 宿主 Helpers 与 Interop 同源移植 |
| `UsbModels.cs` | UsbDrive / UsbVolume / UsbCheckOutcome / CapacityProbe / UsbThreat / UsbFixAction | 宿主 Services/Usb/UsbModels.cs + UsbRepairService 内嵌类 |
| `UsbServices.cs` | 四服务：探查 / 健康检测 / 查杀 / 修复 | 宿主 Services/Usb 全部逻辑逐行对应 |
| `UsbToolViewModel.cs` | VM + 行模型（ILogService → IPluginContext.Log 适配） | 宿主 ViewModels/UsbToolViewModel.cs |
| `UsbToolView.cs` | 纯代码 UI（零 XAML 编译依赖）+ 二次确认 ContentDialog | 宿主 Views/UsbToolPage.xaml + code-behind |

路径约定：隔离区与提权脚本目录沿用宿主同一路径
（`%LocalAppData%\QingShu.Toolkit\quarantine` 与 `...\temp`），内置/插件两版共用，人工寻迹一致。

## 依赖与 ALC 说明

- `CommunityToolkit.Mvvm`（8.4.2）与 `System.Management`（8.0.0）**仅编译期引用**
  （csproj `ExcludeAssets="runtime"`，不拷贝到插件目录）——运行期经 PluginLoadContext 回退
  默认上下文共享宿主同版本程序集，跨 ALC 类型同一、杜绝双份。
- `Microsoft.WindowsAppSDK` 与宿主同版本：Microsoft.UI.Xaml.* 在默认加载上下文共享。

## 构建与组装

```powershell
# 1. 发布宿主（如已发布可跳过）
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true

# 2. 构建插件（-p: 传参最可靠；仓库内发布目录存在时可不传）
dotnet build plugins-sdk/usb-tool/QingShu.Plugin.UsbTool -c Release -p:Platform=x64

# 3. 组装到宿主发布目录 plugins\<id>\（发现式拷贝，排除 obj 中间产物）
$bin = Get-ChildItem plugins-sdk/usb-tool/QingShu.Plugin.UsbTool/bin -Recurse -Filter "QingShu.Plugin.UsbTool.dll" |
       Where-Object { $_.FullName -notmatch '\\obj\\' } | Select-Object -First 1
$dst = "src/QingShu.Toolkit/bin/publish/win-x64/plugins/qingshu.plugin.usbtool"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item $bin.FullName $dst -Force
Copy-Item ([IO.Path]::ChangeExtension($bin.FullName, ".pdb")) $dst -Force
Copy-Item "plugins-sdk/usb-tool/QingShu.Plugin.UsbTool/plugin.json.template" "$dst/plugin.json" -Force
```

或直接 `scripts/make_release.ps1` 打包：默认装载清单已含 `qingshu.plugin.usbtool`，
自动把构建产物放入发布包 `plugins\qingshu.plugin.usbtool\`（仅补缺失，不覆盖已改配置）。

装载的默认插件无 `plugin.sig`，宿主按可选签名策略直接放行；需要官方签名徽章时运行
`tools/plugin-sign.ps1 -PluginFolder <插件目录>` 重签。

## 护栏

- 健康检测/扩容检测/查杀/修复全程可取消（步骤边界生效）；离开页面自动取消在途操作。
- 扩容检测前逐卷系统盘拦截（`IsSystemVolume` 最后防线）；重建分区危险项默认不勾选。
- 视图创建抛异常时宿主自动回退内置页（CreateNavViewFor 兜底），功能不受损。
