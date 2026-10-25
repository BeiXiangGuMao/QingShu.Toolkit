# 打包发布指南（docs/RELEASE.md）

适用范围：青枢工具箱 v1.2.0-beta 及之后版本（WinUI 3 / .NET 8 / x64 / 未打包默认模式）。
本文描述两种官方发布格式的完整操作流程；构建环境与调试请先阅读 [BUILD.md](BUILD.md)，回归验收请阅读 [TESTING.md](TESTING.md)。

## 〇、两种格式怎么选

| 对比项 | 格式一：便携版 ZIP（自包含） | 格式二：MSIX 安装包 |
|--------|------------------------------|----------------------|
| 形态 | 压缩包，解压即用，无安装动作 | 标准安装包，开始菜单/卸载器齐全 |
| 运行时依赖 | 无（.NET 8 + Windows App SDK 全部内置） | 无（同样自包含） |
| 适用场景 | 机房批量部署、U 盘携带、免管理员环境 | 普通用户装机、需要干净卸载的场景 |
| 应用内自动更新 | 支持（更新器只识别 Release 中的 .zip 资产） | 需手动下载新 MSIX 覆盖安装 |
| 制作成本 | 一条 publish 命令 + 压缩 | 需临时切换 csproj 双模式 + 签名 |
| 对目标系统要求 | Win10 1809 (17763) 及以上 | 同左，另需允许 sideload（默认已允许） |

> 建议：每次对外发布**同时提供两种格式**；GitHub Release 资产中必须包含格式一的 ZIP，
> 否则内置更新器将"发现新版本却无可下载资产"。

## 一、发布前检查清单

1. **版本号**：`src/QingShu.Toolkit/QingShu.Toolkit.csproj` 的 `<Version>` 已改为本次发布版本
   （如 `1.2.0-beta`）。Beta 阶段保留 `-beta` 后缀：展示层（标题栏/关于页/日志/PDF 报告）
   会自动带上后缀，而更新比较、UserAgent 等机器可读场景自动取纯数字前缀，二者无需分别维护。
   转正发布时改为如 `1.3.0` 即可，后缀自动消失。
2. **回归验证**：Release 配置编译 0 错误；按 TESTING.md 完成核心路径手工验证。
3. **干净构建**：关闭 Visual Studio，删除 `src/QingShu.Toolkit/bin` 与 `obj` 目录，
   重新还原 NuGet（避免旧产物混入包内）。
4. **提交与打标**：所有改动已推送，工作区 clean；为本次发布打标签：
   ```powershell
   git tag v1.2.0-beta
   git push origin v1.2.0-beta
   ```
   标签必须形如 `v<版本号>`：内置更新器按 `releases/latest` 拉取并用 `v1.2.0` 语义比较版本。

## 二、格式一：便携版 ZIP（自包含）

### 2.1 发布命令

在仓库根目录（PowerShell 或终端）执行：

```powershell
dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true
```

发布为**自包含单目录**：.NET 8 运行时与 Windows App SDK 运行时全部进入产物，
目标机器**无需安装任何运行时**，免管理员、免安装。

### 2.2 定位与校验输出目录

产物位于 `src/QingShu.Toolkit/bin/` 下的 `publish` 目录
（不同 SDK 小版本可能在 `bin/x64/Release/net8.0-windows10.0.*/win-x64/publish/`，
以实际为准）。用以下特征确认目录完整：

- `QingShu.Toolkit.exe`（主程序，约数百 KB）
- `resources.pri`、`Microsoft.UI.Xaml.dll` 等 WinUI 资源与框架程序集
- `Microsoft.WindowsAppRuntime.Bootstrap.dll`（WASDK 自包含引导）
- `QuestPDF.*`、`SkiaSharp.*`（PDF 报告链路）
- `Assets/` 目录（图标等资源）

**整个 publish 目录必须原样分发，缺一个文件都可能导致启动失败。**
可选：删除全部 `*.pdb` 减小体积（保留则崩溃堆栈更易定位，权衡自定）。

### 2.3 打 ZIP 包

```powershell
# 路径按实际 publish 目录调整
$pub  = "src/QingShu.Toolkit/bin/x64/Release/net8.0-windows10.0.26100.86/win-x64/publish"
$zip  = "QingShu.Toolkit-1.2.0-beta-win-x64.zip"
Compress-Archive -Path "$pub/*" -DestinationPath $zip -CompressionLevel Optimal
```

> 内置更新器下载后按"压缩包内直接覆盖程序目录"处理（robocopy 整目录镜像），
> 因此 ZIP 内层**不要多套一层文件夹**：压缩 `$pub/*` 的内容而非 `$pub` 目录本身。

### 2.4 生成校验值

```powershell
Get-FileHash $zip -Algorithm SHA256
```

把哈希值附在 Release 说明中，供用户/机房管理员核验完整性。

### 2.5 用户侧使用方式

1. 解压到**任意可写目录**（如 `D:\Tools\QingShu.Toolkit`）；不建议解压到
   `C:\Program Files` 等需要管理员写权限的目录（日志、下载更新等写入会受影响）。
2. 双击 `QingShu.Toolkit.exe` 运行。首次运行如出现 SmartScreen 提示，见第五节 FAQ。

## 三、格式二：MSIX 安装包（csproj 双模式切换）

项目已预留 MSIX 双模式（详见 csproj 内注释与 `packaging/Package.appxmanifest`）。
流程为"临时切换 → 打包 → 还原"，**打包完成后务必切回未打包模式再提交**。

### 3.1 临时切换为打包模式（两处改动）

1. `src/QingShu.Toolkit/QingShu.Toolkit.csproj` 中把
   `<WindowsPackageType>None</WindowsPackageType>` 改为 `MSIX`；
2. 取消 csproj 底部 `AppxManifest` ItemGroup 的注释（引用 `packaging/Package.appxmanifest`）；
3. 打开 `packaging/Package.appxmanifest` 核对版本号与发布版本一致。

### 3.2 生成安装包（Visual Studio）

1. 平台选 **x64**、配置 **Release**，生成一次确认 0 错误；
2. 解决方案资源管理器右键项目 → **打包 → 创建应用程序包**；
3. 选择「旁加载（Sideload）」；签名方式二选一：
   - **测试/内部分发**：勾选「自动创建」自签名证书（`LocalMachine/My`，密码自定）；
   - **正式发布**：使用已购代码签名证书（.pfx），可信度更高、无证书安装提示；
4. 输出目录得到 `QingShu.Toolkit_<版本>_x64.msix`（及 `.msixupload` 可忽略），
   建议重命名为 `QingShu.Toolkit-1.2.0-beta-x64.msix`；
5. 生成 SHA256（命令同 2.4）。

### 3.3 用户侧安装与卸载

- **安装**：双击 `.msix` → 安装。自签名证书首次安装需先信任证书
  （双击 `.cer` → 安装到「本地计算机 → 受信任的人」），正式证书签名的包可直接安装；
- **安装位置**：MSIX 由系统统一管理（`C:\Program Files\WindowsApps`），不暴露目录；
- **卸载**：设置 → 应用 → 找到「青枢工具箱」→ 卸载，无残留；
- **升级**：同证书更高版本直接覆盖安装。

### 3.4 还原为未打包模式（勿忘）

把 `<WindowsPackageType>` 改回 `None`，重新注释 `AppxManifest` ItemGroup，
与本次打包相关改动**不要**提交到 main（如确需提交，另开分支保存配置）。

## 四、发布到 GitHub Releases

1. 确认 tag 已推送（第一章第 4 步）；
2. 仓库页 → **Releases** → **Draft a new release** → 选择已有 tag `v1.2.0-beta`；
3. 标题：`青枢工具箱 v1.2.0-beta`；说明中粘贴更新要点 + 两种格式各自的 SHA256；
4. 上传资产：
   - `QingShu.Toolkit-1.2.0-beta-win-x64.zip`（**必备**：内置更新器只识别 `.zip` 资产）；
   - `QingShu.Toolkit-1.2.0-beta-x64.msix`（可选，格式二）；
5. **pre-release 勾选策略**（重要）：
   - GitHub 的 `releases/latest` **不包含** pre-release；beta 包若勾选
     「Set as a pre-release」，内置"检查更新"将不会提示它，仅能手动下载 —— 适合小范围灰度；
   - 面向全体用户的正式发布**不要勾选** pre-release，否则更新器永远不推送；
6. **Publish release**。

> 内置更新器比较逻辑：`tag` 去掉 `v` 前缀后按 `-` 截断取数字前缀，
> 与本地程序集数字版本比较，`1.2.0-beta` 与 `1.2.0` 视为同一数字版本，不会互推。

## 五、常见问题（FAQ）

**Q1：双击 exe / 安装时提示「Windows 已保护你的电脑」（SmartScreen）**
程序未做代码签名的正常提示：点「更多信息」→「仍要运行」。长期方案是购买代码签名证书
对 zip 内主程序或 MSIX 签名；学校机房环境也可由管理员提前加白名单。

**Q2：启动闪退或提示缺 DLL**
确认使用的是自包含 publish 产物且整个目录完整分发（见 2.2 特征清单）；
若被杀毒软件隔离，恢复并加白名单后重试。

**Q3：应用内"检查更新"提示检查失败**
内置更新走 `api.github.com`，校园网如拦截 GitHub 需先完成网络认证或使用可达网络；
检查失败不影响其他功能。

**Q4：更新器发现了新版本但提示"未提供压缩包资产"**
该 Release 没有上传 `.zip` 资产（只传了 MSIX 或只有源码）。补传格式一 ZIP 即可。

**Q5：MSIX 安装报错"此应用包需要证书"或签名无效**
自签名包需先按 3.3 信任证书；升级包必须与已装版本**同签名证书**，否则需先卸载旧版。

**Q6：便携版用户把程序放在只读目录，更新失败**
内置更新需对程序目录有写权限（robocopy 覆盖）。提示用户把程序放到可写目录，
或改用管理员身份运行一次完成更新。

**Q7：想出更小的包**
自包含包体积较大（约 150–250 MB）。若目标机器统一装有 .NET 8 Desktop Runtime 与
Windows App SDK Runtime，可去掉 `--self-contained` 与 `-p:WindowsAppSDKSelfContained=true`
做框架依赖发布，体积可降至数十 MB；但目标机缺运行时会无法启动，机房场景不建议。
