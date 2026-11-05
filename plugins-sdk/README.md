# 青枢工具箱 · 插件 SDK（官方签名插件体系）

与 DeepSeek harness 的插件思路同构：**只有官方（BeiXiangGuMao 本人）用专用私钥签名的插件才能被加载**，加载后完全隔离、可随时卸载并整体回收；插件可以声明权限访问宿主能力，其中 `releases` 权限允许读取/下载官方仓库 Release（与本机自动更新共用同一套多线路链与 Token 治理）。

## 1. 安全模型（宿主强制）

一个合法插件 = `plugins\<插件id>\` 目录，内含：

```
plugins/
  qingshu.sample/
    plugin.json      ← 清单（含 mainAssemblySha256）
    plugin.sig       ← 官方签名（对 plugin.json 原始字节的 RSA-2048/SHA-256 签名）
    QingShu.Plugin.Sample.dll   ← 主程序集
    *.dll            ← 自带依赖（与宿主同名依赖会被宿主版本覆盖）
```

加载前宿主逐项校验，任何一项不过即拒绝并显示原因：

| # | 校验项 | 说明 |
|---|--------|------|
| 1 | 清单存在且可解析 | `plugin.json`，允许注释与尾逗号 |
| 2 | 官方签名 | 内置公钥验签（指纹 `846C:65C7:BDD7:1029`，插件管理页可见）；无 sig = 未签名，验不过 = 被篡改或非官方钥 |
| 3 | 目录名 = `manifest.id` | 防冒名（目录随便起名装别人的清单） |
| 4 | 主程序集 SHA-256 = 清单登记值 | 清单与 dll 绑定，改任一都要重签 |
| 5 | `mainAssembly` 为纯文件名 | 防路径穿越 |
| 6 | `minHostVersion` ≤ 宿主版本 | 版本门槛 |

运行期隔离：每个插件一个**可卸载 `AssemblyLoadContext`**（collectible）。系统程序集与宿主程序集（含契约接口）走默认上下文共享；插件目录内私有 dll 优先从插件目录加载。卸载时宿主回调 `OnUnloadedAsync()`（5s 超时兜底）→ `Unload()` → GC 回收，并在界面上报告「完全释放 / 待后台释放」。

## 2. manifest（plugin.json）字段

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | ✅ | 目录名必须与之完全一致；建议小写 `qingshu.<名>` |
| `name` | ✅ | 显示名 |
| `version` | 建议 | 插件版本（展示用） |
| `author` | 建议 | 作者 |
| `description` | 可选 | 卡片描述 |
| `mainAssembly` | ✅ | 主程序集**纯文件名** |
| `entryType` | 可选 | 入口类型全名；省略则扫描程序集中唯一 `IQToolkitPlugin` 实现 |
| `minHostVersion` | 可选 | 宿主版本门槛，如 `1.2.0` |
| `permissions` | 建议 | 权限数组，见下 |
| `mainAssemblySha256` | 签名时自动写入 | 不要手填，`plugin-sign.ps1` 会覆盖 |

**权限**（未声明 = 拒绝）：

- `releases` —— `context.Releases` 非空；否则为 `null`
- `storage` —— 允许使用插件私有数据目录（未声明时访问 `DataFolder` 抛 `UnauthorizedAccessException`）

## 3. 开发一个插件

工程模板见 `sample/`：一个 `net8.0` 类库，**引用发布目录里的 `QingShu.Toolkit.exe`**（契约接口由宿主程序集提供，运行期类型同一）：

```xml
<Reference Include="QingShu.Toolkit">
  <HintPath>$(QingshuHome)\QingShu.Toolkit.exe</HintPath>
</Reference>
```

实现 `QingShu.Toolkit.Services.Plugins.IQToolkitPlugin`：

```csharp
public sealed class MyPlugin : IQToolkitPlugin
{
    public string DisplayName => "我的插件";

    public Task OnLoadedAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log("info", "已启动");
        context.RegisterQuickAction("跑一次巡检", async token =>
        {
            // ……你的逻辑，返回一行结果文字显示在卡片上
            return "巡检完成：全部正常";
        });
        return Task.CompletedTask;
    }

    public Task OnUnloadedAsync()
    {
        // 释放句柄/停后台任务/落盘状态；返回后宿主立即卸载 ALC
        return Task.CompletedTask;
    }
}
```

### IPluginContext 能力一览

| 成员 | 说明 |
|------|------|
| `PluginId` / `PluginFolder` / `DataFolder` | 身份与目录（数据目录按权限自动创建） |
| `Log(level, message)` | 宿主应用日志（`plugin:<id>` 分类），level = debug/info/warn/error |
| `HasPermission(p)` | 权限查询 |
| `Releases` | `IPluginReleaseAccess`（需 `releases` 权限），见下节 |
| `RegisterQuickAction(name, action)` | 在插件管理页注册按钮（最多 8 个，重名抛错）；`action` 返回的文字即按钮下方的输出 |

> 说明：`RegisterQuickAction` 请在 `OnLoadedAsync` 内完成注册；运行期新注册的按钮在「重新扫描」后显示。

### 访问官方仓库 Release（需 `releases` 权限）

```csharp
var access = context.Releases!;
var releases = await access.ListAsync(10, token);          // 最新 10 条（含预发布）
foreach (var r in releases)
    context.Log("info", $"{r.TagName} · {r.Name} · 资产 {r.Assets.Count}");

var path = await access.DownloadAssetAsync("v1.2.0", "QingShu.Toolkit-v1.2.0.zip", token);
// 落地：%LOCALAPPDATA%\QingShu.Toolkit\plugin-downloads\v1.2.0\<资产名>
```

网络行为与本机自动更新完全一致：多线路自动回退、`Token` 只随直连 GitHub 请求发送（绝不发给加速镜像）、私库必须在「设置」里填过 GitHub Token。404 时先检查 Token 是否覆盖该仓库。

### 3.5 插件 UI 面板（可选能力 IQToolkitUiProvider）

快捷动作只适合"点一下出文本"的场景；需要富交互（拓扑图 / 列表 / 操控台）的插件实现可选接口
`IQToolkitUiProvider`，面板就会挂载在「插件管理」页的面板区：

```csharp
public sealed class MyPlugin : IQToolkitPlugin, IQToolkitUiProvider
{
    public string PanelTitle => "我的面板";
    public FrameworkElement CreatePanelView() => new MyPanelView(this); // 宿主 UI 线程调用
}
```

要点：

- 插件工程需 `UseWinUI=true` + 与宿主**同版本**的 `Microsoft.WindowsAppSDK`（参见
  `gala-dispatch/QingShu.Plugin.GalaDispatch.csproj`，含 `WindowsSdkPackageVersion` 对齐说明）。
  `Microsoft.UI.Xaml.*` 在默认加载上下文共享，跨 ALC 类型同一，宿主可直接把返回元素放进视觉树。
- 面板元素建议纯代码构建（不写 .xaml，免去插件工程的 XAML 编译配置），范例见 GalaPanelView。
- 生命周期：同一时刻宿主只挂载一个面板；「卸载插件」前宿主自动收起面板（视图引用会阻止 ALC
  回收）；面板关闭（Unloaded）≠ 插件停止——插件应在视图 Unloaded 里停纯 UI 资源（定时器），
  后台服务由 `OnUnloadedAsync` 统一收尾。
- 未实现该接口的插件完全不受影响；旧宿主加载带面板的插件只是没有「面板」按钮。

## 4. 签名与安装（官方流程）

```powershell
# ① 一次性：生成官方密钥对（私钥务必离线保管）
powershell -ExecutionPolicy Bypass -File tools\plugin-keygen.ps1

# ② 构建插件
dotnet build plugins-sdk\sample\QingShu.Plugin.Sample -c Release

# ③ 组装插件目录（目录名必须 = manifest.id）
#    plugins\qingshu.sample\{plugin.json, QingShu.Plugin.Sample.dll, 自带依赖...}

# ④ 签名（写入 mainAssemblySha256 并对清单签名）
powershell -ExecutionPolicy Bypass -File tools\plugin-sign.ps1 -PluginFolder plugins\qingshu.sample

# ⑤ 安装：把整个目录拷到 <工具箱目录>\plugins\ 下
# ⑥ 「插件管理」页 → 重新扫描 → 勾选启用 → 加载
```

改任何清单字段或更换 dll 后**必须重新签名**。启动时宿主自动加载全部「已启用且验签通过」的插件。

## 5. 换钥

1. `plugin-keygen.ps1` 生成新对（脚本拒绝覆盖旧私钥，先备份）；
2. 把新公钥 XML 替换进 `Services/Plugins/PluginSigning.cs` 的 `OfficialPublicKeyXml` 常量；
3. 重新发布宿主；存量插件全部用新私钥重签。

## 6. 边界与已知限制

- **插件无自定义 UI**：V1 插件是无界面工具（快捷动作按钮 + 文字输出 + 日志），不能注入页面；后续版本可扩展面板挂载点。
- **签名覆盖范围**：签名锁定「清单 + 主程序集」；插件自带的第三方依赖 dll 不在签名覆盖内，请通过可信渠道分发整个插件目录（未来可加整目录清单哈希）。
- **卸载语义**：`OnUnloadedAsync` 超时 5s 即继续卸载；若插件线程仍持有其程序集内的对象引用，上下文会延迟到引用消亡后由 GC 回收（界面如实提示，不影响宿主稳定）。
- **初始化超时**：`OnLoadedAsync` 超过 10s 视为加载失败，宿主回滚注册并回收上下文——插件初始化应尽快返回，长任务自行 `Task.Run`。
- **插件间冲突**：不同插件的目录相互隔离；同名依赖以各目录内副本为准（System./Microsoft. 前缀除外，恒用宿主版本）。
