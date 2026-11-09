# 弹窗拦截插件（qingshu.plugin.popupguard）

内置「弹窗拦截」工具的真外移版本（内置工具插件化第一批）：宿主只保留薄壳导航，
拦截引擎与全部 UI 随插件程序集动态加载（ALC 隔离、可卸载）。

## 接管语义

- `manifest.navTag = "popupguard"` 与宿主左侧导航 Tag 一致 → 插件加载成功后：
  - 导航项原位不动（图标/位置不变），点击进入的是本插件的纯代码视图；
  - 宿主内置拦截服务**运行时挂起**（停计时器/钩子，不改 `config.json` 的 `PopupBlockEnabled`）；
  - 状态栏「拦截 N」入口隐藏（内置计数已无意义，插件页内有自己的计数）；
  - 设置页「弹窗拦截」卡片隐藏（全局开关已由插件自治）。
- 插件卸载（插件管理页关闭开关/移除目录/宿主退出前收尾）→ 内置实现自动恢复运行，无感回退。
- 首次启动：插件从宿主 `%LOCALAPPDATA%\QingShu.Toolkit\config.json` 一次性迁移
  `PopupBlockEnabled` / `PopupWhitelist` 到自身 `plugins-data\qingshu.plugin.popupguard\settings.json`，
  此后两边互不影响（宿主字段保留仅为回退兼容）。

## 拦截语义（与内置实现同源移植）

被动原则：不做注入、不挂钩 DLL，仅扫描可见顶层窗口并发送 `WM_CLOSE`；
顽固弹窗（忽略关闭消息）下一轮 `SW_HIDE` 兜底。护栏逐条对应内置版：
启动基线窗口放行 / 前台新窗口宽限 2 秒 / 系统外壳类永不拦 / 工具窗口永不拦 /
`ApplicationFrameHost` 在默认白名单 / 前缀通配白名单（`wps*`）。

## 构建

```powershell
# 0. 发布宿主（产物落 src\QingShu.Toolkit\bin\publish\win-x64\，以 dotnet publish 输出「→」为准）
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true

# 1. 构建插件（csproj 自动探测仓库内宿主发布目录；探测落空时显式传参必生效）
dotnet build plugins-sdk/popup-guard/QingShu.Plugin.PopupGuard -c Release -p:Platform=x64
# 探测失败时：dotnet build ... -p:QingshuHome="<发布目录>"

# 2. 组装（发现式拷贝：bin 递归找主 dll/pdb，排除 obj\ref 中间程序集；清单从模板补齐）
$pub = "<发布目录>"; $bin = "plugins-sdk/popup-guard/QingShu.Plugin.PopupGuard/bin"
New-Item -ItemType Directory -Force "$pub\plugins\qingshu.plugin.popupguard" | Out-Null
Get-ChildItem $bin -Recurse -Include QingShu.Plugin.PopupGuard.dll,QingShu.Plugin.PopupGuard.pdb |
  Where-Object { $_.FullName -notmatch '\\obj\\' } |
  Select-Object -First 2 | Copy-Item -Destination "$pub\plugins\qingshu.plugin.popupguard" -Force
Copy-Item "plugins-sdk/popup-guard/QingShu.Plugin.PopupGuard/plugin.json.template" `
  "$pub\plugins\qingshu.plugin.popupguard\plugin.json" -Force
```

`scripts\make_release.ps1` 已把本插件列入默认装载清单（第 2.5 步）：宿主打包时若已构建
即自动带入发布包 `plugins\` 目录；装载的插件无 `plugin.sig`，按可选签名策略直接放行
（需要官方签名徽章则装载后用 `tools\plugin-sign.ps1 -PluginFolder <目录>` 重签）。

## 状态徽章

插件页列表中显示「未签名 · 可加载」（蓝）；签名后显示「官方签名」（绿）。
宿主对声明 `navTag` 的插件同样走完整验签（有 sig 则签名/哈希/目录一致性全查）。
