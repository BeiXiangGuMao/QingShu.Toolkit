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

## 三、机房批量部署（脚本已交付：`scripts/deploy_install.ps1`）

1. **共享目录 + 一键装机脚本**（已落地）：发布 zip + 同名 `.sha256`（make_release.ps1 §3.1
   自动生成）+ `deploy_install.ps1` 三件一起放教研组共享目录；装机机一条命令：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\deploy_install.ps1 `
     -SharePath '\\server\share\QingShu' -Autostart `
     -WithConfig '\\server\share\QingShu\config.json'
   ```

   六步语义（全流程幂等：失败修正后原样重跑，从未完成处续装）：
   - ① **SHA256 核验**（硬要求）：读 zip 旁同名 `.sha256`（或 `-ExpectedHash` 显式给出），
     `Get-FileHash` 比对，不一致即中止——机房共享目录多人写，防传输损坏/误替换；
     sidecar 记录的文件名与所选 zip 不一致也中止（防错放文件）
   - ② **探测安装根并解压**：`D:\Tools` 优先，无 D 盘退 `C:\Tools`（`-InstallRoot` 可指定）；
     解压用 `Expand-Archive -Force` **覆盖式增量**——zip 内有则覆盖、zip 外一律保留
     （与更新器 robocopy `/e` 增量不删同一教义，永不先删旧目录）；已安装且未加 `-Force`
     时跳过解压（保留 plugins\ 用户资产）
   - ③ **基线配置**（可选 `-WithConfig`）：拷贝公共 `config.json` 到
     `%LOCALAPPDATA%\QingShu.Toolkit\`；本机已有配置且未加 `-Force` 时保留本机
   - ④ **开机自启**（可选 `-Autostart`）：写 HKCU Run 键 `QingShu.Toolkit` → exe 全路径
     （公共机场景；用户级免管理员）
   - ⑤ **安装摘要**：安装目录/发布包/版本（zip 名 + exe 文件版本）/SHA256/插件清单/
     配置来源/自启状态，供装机点名核验
   - ⑥ **失败处置**：任一步失败非 0 退出并打印已完成步骤；重跑从未完成处续装

   多个 zip 共存时按文件名倒序取最新并给出「共享目录只保留当前推荐版本」提醒
   （日常升级走应用内自动更新，见 §四）。

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
  退出后 update.cmd 解压 + robocopy 覆盖 → 自重启。关键语义（E2E 实测逐项断言，
  见 update_e2e_sim）：robocopy `/e /is /it` 是**覆盖式增量、永不删除**——
  `plugins\` 下 zip 外文件全部保全（用户自装插件、默认插件目录内的用户附加文件、
  `plugin.sig` 签名随包更新器不生成故不删）；随包默认插件的 `plugin.json` 以新版
  随包版本为准（更新即刷新到随包内容），用户数据/插件私有配置在
  `%LOCALAPPDATA%` 与安装目录无关、逐字节不受影响。
- **手动重装**：下载新 zip → 解压覆盖安装目录（等价于更新器的 robocopy / 装机脚本
  Expand-Archive 覆盖式增量）→ 用户数据在 `%LOCALAPPDATA%` 天然保留。
  **不建议**先删后解压（会丢 plugins\ 用户文件）。
- **降级**：不支持应用内降级（版本比较只升不降）；确需回退时手动解压旧 zip 覆盖，
  且注意新版可能升级 config.json 字段（向后兼容，回退不丢数据但新增字段被忽略）。

## 五、卸载

1. 退出应用（含托盘/后台线程：主窗关闭即 BeginShutdown）。
2. 删安装目录 `D:\Tools\QingShu.Toolkit\`。
3. 删用户数据 `%LOCALAPPDATA%\QingShu.Toolkit\`（**含白名单/插件配置/历史报告，不可逆**；
   保留则下次重装自动继承全部配置）。
4. 若配置过开机自启：删 HKCU Run 键 `QingShu.Toolkit`。

## 六、发布/取用校验

- 发布侧：make_release.ps1 打包后自动计算 SHA256 并**写同名 `.sha256` sidecar**
  （`<哈希>  <文件名>` 格式，`sha256sum`/`Get-FileHash` 通用），控制台同步打印
  （RELEASE.md §2.4）；zip 与 sidecar 一同放共享目录/Release 说明引用。
- 部署侧：装机脚本部署前读取 sidecar 核验 `Get-FileHash` 一致，不一致即中止——
  机房共享目录多人写，防传输损坏/误替换；无 sidecar 且未显式给 `-ExpectedHash`
  时同样中止（拒绝安装未核验的发布包）。
- 应用内更新侧：GitHub API 资产自带 `digest` 字段（sha256:…），下载完整性三级
  校验（PK 头 → SHA-256 摘要比对 → EOCD 兜底）与其一致。

## 七、排期索引（与本仓库其他文档的关系)

- 打包与发布操作：RELEASE.md（ZIP 命令/校验/上传；§2.4 含 .sha256 sidecar 说明）
- 插件构建验证：RELEASE.md §2.6 / scripts/build_all_plugins.ps1
- 功能测试基线：TESTING.md（本规划 §三.3 验收清单是其部署子集；装机脚本验收见
  TESTING.md §B.3 第 79 条）
- 已交付交付物：`scripts/deploy_install.ps1` 一键装机脚本（§三.1，锚点+语义仿真
  门禁 scripts/deploy_sim.py）

## 八、故障处置（装机与升级常见失败）

| 现象 | 原因 | 处置 |
| --- | --- | --- |
| 双击 exe 无反应/SmartScreen 拦截 | 未签名 Beta 包 | 「更多信息 → 仍要运行」；机房场景可预先将安装根加入信任（RELEASE.md FAQ） |
| 解压后启动报缺 dll | zip 传输损坏（SHA256 不符） | 重新下载/拷贝并核验哈希；装机脚本 ① 步已拦截 |
| 应用内「检查更新」超时 | 机房网络未放行 GitHub | 见 §九 放行清单；或用共享目录新 zip 手动覆盖（等价 robocopy 语义） |
| 更新下载完成后重启未生效 | update.cmd 被杀软拦截 | 将安装目录加入杀软白名单；重试一次；仍失败手动覆盖解压 |
| 更新后插件消失 | 曾「先删后解压」重装（丢 plugins） | 重装/升级**永远解压覆盖，禁止先删**；用户自装插件重新放入并核对 plugin.json |
| 升级后启动白屏/崩溃 | 新旧版本配置字段冲突（罕见） | 备份后删除 %LOCALAPPDATA%\QingShu.Toolkit\config.json 再启动（其余插件数据不动） |

## 九、机房网络放行清单（应用内更新与门户检测）

- `api.github.com`（版本检查，HTTPS 443）——必需
- `github.com` 与 `objects.githubusercontent.com`（更新包下载，HTTPS 443）——必需
- 加速线路域名（设置页可选线路，HTTPS 443）——备用，二选一放行即可
- 校园认证门户地址（网络诊断/门户自动登录用，HTTP/HTTPS）——按学校实际配置
- 放行方式：出口防火墙/上网行为管理按域名白名单；无法放行时走共享目录手动升级（§四）
