# ZIP 便携版安装与部署规划（docs/DEPLOY_ZIP.md）

> 适用对象：便携版 ZIP（自包含单目录，见 RELEASE.md §2）。规划覆盖单机安装、
> 机房批量部署、升级与卸载全生命周期。Beta 阶段按「先单机 → 再批量」节奏推进。

## 〇、为什么选 ZIP 作为主部署形态

| 维度 | 便携 ZIP | MSIX |
| --- | --- | --- |
| 安装权限 | 免管理员、免安装 | 需导入 .cer 证书（装机脚本一次） |
| 更新方式 | 应用内自动更新（robocopy 增量，用户数据/插件配置不丢） | 需重新签名整包 |
| 插件目录 | `plugins\` 直改直看，运维直观 | 安装在 WindowsApps，目录受系统保护 |
| 卸载 | 删目录 + 清 `%LOCALAPPDATA%\QingShu.Toolkit` | 系统卸载入口 |
| 适用 | 教学机/公共机/礼堂控制机（主形态） | 需要系统级安装入口的场景（备选） |

结论：**默认发 ZIP**；MSIX 保留为可选产出（make_release.ps1 默认同时打两种，只要 ZIP 时加 `-NoMsix`）。

## 一、目录规划（单机）

推荐安装根：`D:\Tools\QingShu.Toolkit\`（机房机普遍有 D 盘且免重装）；无 D 盘机器退 `C:\Tools\QingShu.Toolkit\`。
**不要**解压到 `C:\Program Files`（写日志/下载更新需管理员）或网络盘/漫游配置文件目录（性能与权限）。

```text
QingShu.Toolkit\
├─ QingShu.Toolkit.exe          主程序（自包含，目标机无需 .NET/WASDK 运行时）
├─ plugins\                     默认七插件（galamaster/galaagent/popupguard/usbtool/
│                                 diskcleanup/optimize/networkdiag）+ 用户自装插件
├─ plugin-downloads\            插件下载暂存（自动创建）
└─ （其余为运行时与依赖 dll）
```

用户数据（不在安装目录，卸载/重装需一并处理）：
`%LOCALAPPDATA%\QingShu.Toolkit\`——`config.json`（设置/白名单/DisabledBuiltIns）、
`logs\`、`reports\`、`plugins-data\<插件id>\`（各插件私有设置，如 gala-topology.json、
磁盘清理记忆、网络诊断表单记忆）。

## 二、首次安装流程（单机，3 步）

1. **解压**：把发布 zip 解压到安装根（保持 zip 内目录结构）。
2. **（可选）装载默认插件**：仓库内 `.\scripts\build_all_plugins.ps1 -Install` 只在
   「本机自行构建」时需要；**发布包已自带七插件**，直接解压即可，无需此步。
3. **首启配置**：双击 `QingShu.Toolkit.exe`（SmartScreen 提示→「仍要运行」，见 RELEASE.md FAQ）；
   首启后按需完成：弹窗拦截白名单、网络诊断默认参数、功耗模式。
   配置全部落 `%LOCALAPPDATA%\QingShu.Toolkit\config.json`，可拷贝到同类机器复用。

## 三、机房批量部署规划（后续版本实现项）

当前 Beta 阶段手动逐机执行 §二；批量方案按优先级排期：

1. **共享目录 + 一键装机脚本**（低成本，优先做）：发布 zip + `deploy_install.ps1`
   放教研组共享目录；装机脚本职责：
   - 解压到本机 `D:\Tools\QingShu.Toolkit\`（已存在则跳过或按 `-Force` 覆盖）
   - 可选 `-WithConfig <config.json>`：预置弹窗白名单/功耗模式等公共配置
   - 可选 `-Autostart`：写 HKCU Run 键实现开机自启（公共机场景）
   - 输出安装摘要（版本/插件清单/config 来源）供点名核验
2. **配置基线**：机房统一一份 `config.json` 基线（白名单 + 关闭演示数据 + 功耗 auto），
   随脚本分发；机器个性配置（插件启停）装机后本机改。
3. **验收清单**（脚本末尾自动跑 + 人工抽查）：
   - 启动 → 插件管理页七插件「已加载」
   - 左侧导航各工具页能进（被接管 = 插件视图；卸载插件可回退内置页）
   - 弹窗拦截开启后手工弹一个测试窗（如 notepad 新窗）被关闭并计数
   - 设置页「检查更新」能连通仓库（机房网络需放行 api.github.com 或加速线路）
4. **版本管理**：共享目录只放**当前推荐版本**的 zip + SHA256；升级走应用内自动更新
   （下一节），机房无需逐机重装。

## 四、升级路径

- **首选：应用内自动更新**。更新器已实现多线路检查 → 下载（三级完整性校验）→
  退出后 update.cmd 解压 + robocopy 覆盖 → 自重启。关键语义：robocopy `/e` 增量
  **不删**——`plugins\` 用户自装插件、已改 `plugin.json`、`plugin.sig` 不受更新影响。
- **手动重装**：下载新 zip → 解压覆盖安装目录（等价于更新器的 robocopy）→ 用户数据
  在 `%LOCALAPPDATA%` 天然保留。**不建议**先删后解压（会丢 plugins\ 用户文件）。
- **降级**：不支持应用内降级（版本比较只升不降）；确需回退时手动解压旧 zip 覆盖，
  且注意新版可能升级 config.json 字段（向后兼容，回退不丢数据但新增字段被忽略）。

## 五、卸载

1. 退出应用（含托盘/后台线程：主窗关闭即 BeginShutdown）。
2. 删安装目录 `D:\Tools\QingShu.Toolkit\`。
3. 删用户数据 `%LOCALAPPDATA%\QingShu.Toolkit\`（**含白名单/插件配置/历史报告，不可逆**；
   保留则下次重装自动继承全部配置）。
4. 若配置过开机自启：删 HKCU Run 键 `QingShu.Toolkit`。

## 六、发布/取用校验

- 发布侧：make_release.ps1 打包后输出 SHA256（RELEASE.md §2.4），随 Release 说明发布。
- 部署侧：装机脚本部署前核验 `Get-FileHash` 与 Release 说明一致，不一致即中止——
  机房共享目录多人写，防传输损坏/误替换。

## 七、排期索引（与本仓库其他文档的关系)

- 打包与发布操作：RELEASE.md（ZIP 命令/校验/上传）
- 插件构建验证：RELEASE.md §2.6 / scripts/build_all_plugins.ps1
- 功能测试基线：TESTING.md（本规划 §三.3 验收清单是其部署子集）
- 规划中未实现的交付物（后续任务）：`deploy_install.ps1` 一键装机脚本（§三.1）
