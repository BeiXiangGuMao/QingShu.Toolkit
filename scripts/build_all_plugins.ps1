<#
.SYNOPSIS
    青枢工具箱七插件一键构建（可选装载）验证脚本：gala 双插件 + 弹窗拦截 / U盘工具 /
    磁盘清理 / 系统优化 / 网络诊断，按 make_release.ps1 同一份默认插件清单逐个
    dotnet build，任一失败给出 -p:QingshuHome 人话提示；-Install 把产物按发布规则
    装进宿主发布目录 plugins\（plugin.json 已存在不覆盖，重复运行安全）。

.DESCRIPTION
    用法（仓库根目录 PowerShell，宿主已发布过一次即可——脚本自动探测发布目录）：
      # 只构建验证（最常用：Windows 侧先编译验证）
      .\scripts\build_all_plugins.ps1

      # 构建 + 装载进宿主发布目录 plugins\，随即可在工具箱「插件管理」页看到七插件加载
      .\scripts\build_all_plugins.ps1 -Install

      # 宿主发布目录不在仓库内时显式指定（最高优先级，绕过自动探测）
      .\scripts\build_all_plugins.ps1 -QingshuHome "D:\deploy\QingShu.Toolkit"

      # 某一工程失败后继续构建其余工程（收全错误清单再一起修）
      .\scripts\build_all_plugins.ps1 -KeepGoing

    宿主发布目录探测顺序（与插件 csproj 内探测链同序）：
      ① -QingshuHome 参数（命令行传参最可靠）
      ② 仓库内 src\QingShu.Toolkit\bin\publish\win-x64（make_release 与新版 SDK 产物路径）
      ③ 仓库内 src\QingShu.Toolkit\bin\publish（旧式）
      ④ 仓库内 src\QingShu.Toolkit\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish
      ⑤ C:\Tools\QingShu.Toolkit
    均未找到宿主 exe 时：给出宿主发布命令后退出（不盲目构建——引用缺失只会产生
    几十条 CS0246 级联，没有任何诊断价值）。

    装载规则（-Install）：dll/pdb 始终覆盖为最新构建（面向本机「构建→装载→试运行」
    循环，验证的必须是新产物）；plugin.json 仅缺失时从 plugin.json.template 创建——
    与 make_release.ps1 §2.5 同规则，用户已改的配置与已重签的 plugin.sig 不被重置。
    重复运行安全；更新器 robocopy /e 增量不删，用户自装插件与已改配置不受影响。

.PARAMETER QingshuHome
    宿主发布目录（含 QingShu.Toolkit.exe）。缺省按上述五级自动探测。

.PARAMETER Configuration
    构建配置，默认 Release。

.PARAMETER Install
    构建成功后把七插件装载进宿主发布目录 plugins\（缺省只构建验证不装载）。

.PARAMETER KeepGoing
    某工程构建失败后继续构建其余工程（缺省即停，快速失败）。

.PARAMETER ListOnly
    只打印将构建的工程与探测到的宿主目录，不实际构建（快速核对环境）。
#>
[CmdletBinding()]
param(
    [string]$QingshuHome,
    [string]$Configuration = "Release",
    [switch]$Install,
    [switch]$KeepGoing,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$HostProjectDir = Join-Path $RepoRoot "src\QingShu.Toolkit"

# 与 make_release.ps1 §2.5 同一份默认插件清单（Id 必须与 plugin.json.template 的 id 一致）。
# SdkRoot = 插件工程所在子目录；galamaster/galaagent 同住 gala-dispatch。
$defaultPlugins = @(
    @{ Id = "qingshu.galamaster";        Sdk = "QingShu.Plugin.GalaMaster";  SdkRoot = "plugins-sdk\gala-dispatch" },
    @{ Id = "qingshu.galaagent";         Sdk = "QingShu.Plugin.GalaAgent";   SdkRoot = "plugins-sdk\gala-dispatch" },
    @{ Id = "qingshu.plugin.popupguard"; Sdk = "QingShu.Plugin.PopupGuard";  SdkRoot = "plugins-sdk\popup-guard" },
    @{ Id = "qingshu.plugin.usbtool";    Sdk = "QingShu.Plugin.UsbTool";     SdkRoot = "plugins-sdk\usb-tool" },
    @{ Id = "qingshu.plugin.diskcleanup"; Sdk = "QingShu.Plugin.DiskCleanup"; SdkRoot = "plugins-sdk\disk-cleanup" },
    @{ Id = "qingshu.plugin.optimize";   Sdk = "QingShu.Plugin.Optimize";    SdkRoot = "plugins-sdk\optimize" },
    @{ Id = "qingshu.plugin.networkdiag"; Sdk = "QingShu.Plugin.NetworkDiag"; SdkRoot = "plugins-sdk\network-diag" }
)

Write-Host "==> 青枢工具箱 · 七插件一键构建验证（harness 默认插件清单）" -ForegroundColor Cyan

# ---- 1. 宿主发布目录五级探测（与插件 csproj 内探测链同序） ----
if (-not $QingshuHome) {
    $candidates = @(
        (Join-Path $HostProjectDir "bin\publish\win-x64"),
        (Join-Path $HostProjectDir "bin\publish"),
        (Join-Path $HostProjectDir "bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"),
        "C:\Tools\QingShu.Toolkit"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "QingShu.Toolkit.exe")) { $QingshuHome = $c; break }
    }
}
if (-not $QingshuHome -or -not (Test-Path (Join-Path $QingshuHome "QingShu.Toolkit.exe"))) {
    Write-Host "    未找到宿主程序集 QingShu.Toolkit.exe（已探测：-QingshuHome 参数、" -ForegroundColor Yellow
    Write-Host "    src\QingShu.Toolkit\bin\publish\win-x64、bin\publish、bin\x64\Release\...\publish、C:\Tools\QingShu.Toolkit）。" -ForegroundColor Yellow
    Write-Host "    先发布宿主：" -ForegroundColor Yellow
    Write-Host '      dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true' -ForegroundColor Yellow
    Write-Host "    或用 -QingshuHome <目录> 指定现有发布目录后重试。" -ForegroundColor Yellow
    exit 1
}
$QingshuHome = (Resolve-Path $QingshuHome).Path
Write-Host ("    宿主发布目录：{0}" -f $QingshuHome) -ForegroundColor DarkCyan

# ---- 2. 核对工程与清单模板在位 ----
$jobs = @()
foreach ($dp in $defaultPlugins) {
    $proj = Join-Path $RepoRoot (Join-Path $dp.SdkRoot (Join-Path $dp.Sdk "$($dp.Sdk).csproj"))
    $tpl  = Join-Path (Split-Path $proj -Parent) "plugin.json.template"
    if (-not (Test-Path $proj)) { throw "插件工程不存在：$proj" }
    if (-not (Test-Path $tpl))  { throw "清单模板不存在：$tpl" }
    $jobs += @{ Id = $dp.Id; Sdk = $dp.Sdk; Proj = $proj; Tpl = $tpl }
}
Write-Host ("    待构建工程：{0} 个" -f $jobs.Count) -ForegroundColor DarkCyan

if ($ListOnly) {
    foreach ($j in $jobs) { Write-Host ("      · {0,-34} {1}" -f $j.Sdk, $j.Proj) }
    return
}

# ---- 3. 逐工程构建（-p:QingshuHome 命令行传参必生效，不受 MSBuild 节点缓存影响） ----
$results = @()
$failed = 0
foreach ($j in $jobs) {
    Write-Host ("==> 构建 {0} ..." -f $j.Sdk) -ForegroundColor Cyan
    dotnet build $j.Proj -c $Configuration -p:Platform=x64 -p:QingshuHome="$QingshuHome"
    if ($LASTEXITCODE -ne 0) {
        $failed++
        Write-Host "    构建失败（exit=$LASTEXITCODE）。排查顺序：" -ForegroundColor Red
        Write-Host "      ① 首个 error 才是根因，后面的 CS0246/CS0234 多为级联；" -ForegroundColor Red
        Write-Host "      ② 找不到宿主类型时改用命令行传参：-p:QingshuHome=「$QingshuHome」（环境变量可能被 MSBuild 节点复用吞掉）；" -ForegroundColor Red
        Write-Host "      ③ 跨 ALC 类型同一：插件工程 TFM / WindowsAppSDK / CommunityToolkit.Mvvm 版本必须与宿主一致。" -ForegroundColor Red
        $results += [pscustomobject]@{ Plugin = $j.Id; Project = $j.Sdk; Result = "FAIL"; Dll = "" }
        if (-not $KeepGoing) { break }
    } else {
        # 发现式定位产物（bin 下多 TFM/配置混存时不硬编码布局；排除 obj\ 与 obj\ref\ 中间产物）
        $binRoot = Join-Path (Split-Path $j.Proj -Parent) "bin"
        $dll = Get-ChildItem $binRoot -Recurse -Filter "$($j.Sdk).dll" -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -match ('\\' + $Configuration + '\\') } |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $dll) {
            $failed++
            Write-Host ("    构建命令成功但未在 bin\{0} 下找到 {1}.dll，请目检输出目录。" -f $Configuration, $j.Sdk) -ForegroundColor Red
            $results += [pscustomobject]@{ Plugin = $j.Id; Project = $j.Sdk; Result = "NO-DLL"; Dll = "" }
            if (-not $KeepGoing) { break }
        } else {
            Write-Host ("    OK：{0}" -f $dll.FullName) -ForegroundColor Green
            $results += [pscustomobject]@{ Plugin = $j.Id; Project = $j.Sdk; Result = "OK"; Dll = $dll.FullName }
        }
    }
}

# ---- 4. 汇总 ----
Write-Host ""
Write-Host "==== 七插件构建汇总 ====" -ForegroundColor Cyan
foreach ($r in $results) {
    $color = if ($r.Result -eq "OK") { "Green" } else { "Red" }
    Write-Host ("  [{0}] {1,-34} {2}" -f $r.Result, $r.Plugin, $r.Dll) -ForegroundColor $color
}
if ($failed -gt 0) {
    Write-Host ("  失败 {0} 个——按上面排查顺序处理后重跑本脚本。" -f $failed) -ForegroundColor Red
    exit 1
}

# ---- 5. 装载（-Install）：与 make_release.ps1 §2.5 同规则，仅补缺失文件 ----
if (-not $Install) {
    Write-Host ""
    Write-Host "全部构建通过（未装载）。要装进宿主发布目录试运行：.\scripts\build_all_plugins.ps1 -Install" -ForegroundColor Cyan
    return
}

Write-Host ""
Write-Host "==> 装载七插件 → $QingshuHome\plugins\ ..." -ForegroundColor Cyan
foreach ($j in $jobs) {
    $r = $results | Where-Object { $_.Plugin -eq $j.Id } | Select-Object -First 1
    if (-not $r -or $r.Result -ne "OK") { Write-Host ("    · 跳过 {0}（构建未成功）" -f $j.Id) -ForegroundColor Yellow; continue }

    $target = Join-Path $QingshuHome (Join-Path "plugins" $j.Id)
    New-Item -ItemType Directory -Force $target | Out-Null
    $staged = 0
    # dll/pdb 始终覆盖：本脚本面向「构建→装载→试运行」循环，验证的必须是最新构建；
    # plugin.json 仅缺失时创建：与 make_release §2.5 同规则，用户已改的配置不被重置。
    foreach ($f in @($r.Dll, [IO.Path]::ChangeExtension($r.Dll, ".pdb"))) {
        if (-not (Test-Path $f)) { continue }
        Copy-Item $f $target -Force
        $staged++
    }
    if (-not (Test-Path (Join-Path $target "plugin.json"))) {
        Copy-Item $j.Tpl (Join-Path $target "plugin.json") -Force
        $staged++
    }
    Write-Host ("    · {0,-34} 装载 {1} 个文件 → plugins\{2}\（dll/pdb 覆盖为最新，plugin.json 仅缺失时创建）" -f $j.Id, $staged, $j.Id) -ForegroundColor DarkCyan
}
Write-Host "    注：装载的默认插件无 plugin.sig，宿主按可选签名策略直接放行加载；如需官方签名徽章，" -ForegroundColor DarkGray
Write-Host "    装载后运行 tools\plugin-sign.ps1 -PluginFolder <插件目录> 重签（plugin.json 已存在不会被覆盖）。" -ForegroundColor DarkGray
Write-Host "完成：启动工具箱 → 插件管理页应看到七插件加载；被接管工具的导航页此时进插件视图。" -ForegroundColor Green
