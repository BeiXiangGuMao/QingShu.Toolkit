# 构建与部署指南（docs/BUILD.md）

## 一、环境要求

| 组件 | 版本要求 |
|------|----------|
| 操作系统（开发机） | Windows 10 19041+ / Windows 11 |
| 目标运行系统 | Windows 10 1809（Build 17763）及以上 |
| Visual Studio | 2022 17.10 及以上 |
| 工作负载 | `.NET 桌面开发` + `Windows 应用程序开发`（含 WinUI 3 模板与 Windows App SDK 工具） |
| .NET SDK | 8.0（LTS） |
| NuGet | 联网还原（首次） |

## 二、NuGet 包清单（已锁定版本）

| 包 | 版本 | 用途 |
|----|------|------|
| Microsoft.WindowsAppSDK | 1.6.250602001 | WinUI 3 / Win10 Acrylic 兼容 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.1742 | 构建工具 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 源生成 |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | DI 容器 |
| QuestPDF | 2026.9.1 | PDF 报告（Community License） |
| System.Management | 8.0.0 | WMI |
| System.Diagnostics.PerformanceCounter | 8.0.0 | CPU 采样 |
| System.ServiceProcess.ServiceController | 8.0.0 | 服务状态只读 |

> QuestPDF 使用 Community License（公司/个人年收入低于 100 万美元免费）。启动时已在 `App` 构造函数声明。

## 三、打开与调试

1. 打开 `QingShu.Toolkit.sln`
2. 平台选 **x64**（或 ARM64），配置 Debug
3. 还原 NuGet → 生成 → F5

> 未打包应用（WindowsPackageType=None）调试时由 Bootstrap 自动解析 Windows App SDK 运行时，无需安装 Runtime 安装器。

## 四、发布

### 方式 A：未打包 exe（推荐机房部署，默认配置）

```bash
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true
# 输出：src/QingShu.Toolkit/bin/publish/win-x64/（或 bin\Release\...\win-x64\publish）
```

或使用项目内发布配置文件：

```bash
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 /p:PublishProfile=win-x64
```

产出为**自包含单目录**（含 .NET 8 运行时与 WASDK 自包含运行时），拷贝到任意 Win10 1809+ 机器直接运行 `QingShu.Toolkit.exe`，**免安装、免管理员**。

### 方式 B：MSIX 安装包（双模式）

1. `src/QingShu.Toolkit/QingShu.Toolkit.csproj` 中将 `<WindowsPackageType>None</WindowsPackageType>` 改为 `MSIX`
2. 取消注释 csproj 底部的 `AppxManifest` ItemGroup
3. 用 VS「打包 → 创建应用程序包」签名生成 `.msix`（测试可勾选自签名证书）

## 五、机房批量部署清单

1. 共享目录放置发布产物（方式 A）
2. 可选：编写 `install.cmd` 做 robocopy + 快捷方式
3. 首次运行无需管理员；修复类操作会合并为一次 UAC
4. 建议在设置中预填：城域网认证门户地址、内网探测地址（或下发 `config.json` 到 `%LOCALAPPDATA%\QingShu.Toolkit\`）

## 六、常见构建问题

| 现象 | 处理 |
|------|------|
| XAML 编译器报资源未找到 | 确认 `App.xaml` 中合并字典顺序：XamlControlsResources 必须第一 |
| `DISABLE_XAML_GENERATED_MAIN` 相关 | 保持 csproj 常量与 `Program.cs` 成对存在 |
| QuestPDF 版本冲突 | 项目统一引用同一版本；删除 bin/obj 后重新还原 |
| Win10 目标机提示缺运行时 | 使用自包含发布（本文方式 A 默认即自包含） |
| 中控/杀软误报 | 将发布目录加入白名单（工具未做任何注入，误报可申诉） |
| 网速模式显示“所有测速节点均不可达” | 校园网白名单拦截属正常现象：低速判断自动回退到分段延迟画像与链路协商；可在 `config.json` 填 `SpeedTestUrl` 指向校内可达的大文件地址 |

## 七、版本与更新

- 应用版本在 csproj `<Version>` 中维护；发布 Release 时上传命名如 `QingShu.Toolkit-1.1.0-win-x64.zip` 的压缩包到 GitHub Releases
- 客户端按 `v1.1.0` 语义比较版本，询问式更新（见 README）
