# 青枢工具箱 · 机房一键装机脚本（deploy_install.ps1）
# 配套发布 zip 使用：把 QingShu.Toolkit-<版本>.zip、同名 .sha256、本脚本一起放共享目录，
# 装机机上一条命令完成首装/续装。全流程幂等：失败修正后原样重跑，从未完成处续装。
# 用法示例：
#   powershell -ExecutionPolicy Bypass -File .\deploy_install.ps1 -SharePath '\\server\share\QingShu' -Autostart
#   powershell -ExecutionPolicy Bypass -File .\deploy_install.ps1 -SharePath 'D:\share\QingShu' -WithConfig 'D:\share\QingShu\config.json'
# 规划与语义：docs/DEPLOY_ZIP.md §三（装机六步）与 §四（机房日常升级走应用内自动更新，
# 本脚本用于首装与故障重装；解压永远覆盖式增量，禁止先删后解压——防丢 plugins\ 用户自装插件）。

param(
    [string]$SharePath  = '',      # 共享目录（含发布 zip 与同名 .sha256；也可直接传 zip 全路径）
    [string]$InstallRoot = '',     # 安装根（默认自动探测 D:\Tools，无 D 盘退 C:\Tools）
    [string]$WithConfig = '',      # 可选：基线 config.json 路径（预置弹窗白名单/功耗等公共配置）
    [string]$ExpectedHash = '',    # 可选：显式 SHA256（优先于 .sha256 sidecar；两者皆无则中止）
    [switch]$Autostart,            # 可选：写 HKCU Run 键开机自启（公共机场景）
    [switch]$Force                 # 可选：已存在安装时覆盖解压/覆盖基线配置
)

$ErrorActionPreference = 'Stop'

# ---------- ⑥ 失败处置（统一出口：非 0 退出 + 打印已完成步骤，幂等续装） ----------
$script:DoneSteps = @()
function Fail {
    param([string]$Step, [string]$Msg)
    Write-Host ''
    Write-Host ('[失败] 第 {0} 步：{1}' -f $Step, $Msg) -ForegroundColor Red
    if ($script:DoneSteps.Count -gt 0) {
        Write-Host ('已完成步骤：{0} —— 幂等设计：修正问题后原样重跑，从未完成处续装。' -f ($script:DoneSteps -join '、')) -ForegroundColor Yellow
    } else {
        Write-Host '尚无完成的步骤 —— 幂等设计：修正问题后原样重跑即可。' -ForegroundColor Yellow
    }
    exit 1
}

# ---------- 定位发布 zip ----------
if ($SharePath -eq '') {
    Write-Host '用法：.\deploy_install.ps1 -SharePath <含 zip 与 .sha256 的共享目录> [-InstallRoot D:\Tools] [-WithConfig config.json] [-Autostart] [-Force]' -ForegroundColor Yellow
    Write-Host '（哈希核验为硬要求：zip 旁无 .sha256 且未传 -ExpectedHash 时中止——机房防传输损坏/误替换。）' -ForegroundColor Yellow
    exit 1
}
$zipPath = ''
if (Test-Path -LiteralPath $SharePath -PathType Leaf) {
    $zipPath = $SharePath
} else {
    $zips = @(Get-ChildItem -LiteralPath $SharePath -Filter '*.zip' -File -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
    if ($zips.Count -eq 0) {
        Fail '定位发布包' ('目录内没有 zip：{0}' -f $SharePath)
    }
    if ($zips.Count -gt 1) {
        Write-Host ('目录内有多个 zip，按文件名倒序取最新：{0}（建议共享目录只保留当前推荐版本）' -f $zips[0].Name) -ForegroundColor Yellow
    }
    $zipPath = $zips[0].FullName
}
if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
    Fail '定位发布包' ('zip 不存在：{0}' -f $zipPath)
}

# ---------- ① SHA256 核验（发布侧 make_release.ps1 写 sidecar → 部署侧核验，DEPLOY_ZIP.md §六） ----------
Write-Host '==> ① SHA256 核验 ...' -ForegroundColor Cyan
$expected = $ExpectedHash
$hashSource = '命令行 -ExpectedHash'
if ($expected -eq '') {
    $sidecar = '{0}.sha256' -f $zipPath
    if (Test-Path -LiteralPath $sidecar) {
        $first = (Get-Content -LiteralPath $sidecar -TotalCount 1).Trim()
        $tokens = @($first -split '\s+')
        $expected = $tokens[0]
        $hashSource = 'sidecar {0}' -f (Split-Path $sidecar -Leaf)
        if ($tokens.Count -ge 2 -and $tokens[1] -ne (Split-Path $zipPath -Leaf)) {
            Fail '① SHA256 核验' ('sidecar 记录的文件名与所选 zip 不一致：{0} vs {1} —— 疑似错放文件' -f $tokens[1], (Split-Path $zipPath -Leaf))
        }
    }
}
if ($expected -eq '') {
    Fail '① SHA256 核验' ('找不到 {0}.sha256 且未传 -ExpectedHash —— 拒绝安装未核验的发布包；可在发布机运行 make_release.ps1 重新生成 sidecar' -f (Split-Path $zipPath -Leaf))
}
$actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
if ($actual -ne $expected.ToUpperInvariant()) {
    Fail '① SHA256 核验' ('哈希不一致（来源 {0}）：期望 {1} 实际 {2} —— zip 可能传输损坏或被误替换，重新拷贝后再装' -f $hashSource, $expected.ToUpperInvariant(), $actual)
}
Write-Host ('    核验通过（{0}）：{1}' -f $hashSource, $actual) -ForegroundColor Green
$script:DoneSteps += '①SHA256核验'

# ---------- ② 探测安装根 + 解压（覆盖式增量：zip 内有则覆盖、zip 外保留） ----------
Write-Host '==> ② 探测安装根并解压 ...' -ForegroundColor Cyan
$root = ''
if ($PSBoundParameters.ContainsKey('InstallRoot')) {
    $root = $InstallRoot
    $drv = $root.Substring(0, 1) + ':'
    if (-not (Test-Path -LiteralPath $drv)) {
        Fail '② 安装根' ('指定的安装根 {0} 所在盘符 {1} 不存在' -f $root, $drv)
    }
} else {
    if (Test-Path -LiteralPath 'D:\') { $root = 'D:\Tools' } else { $root = 'C:\Tools' }
    Write-Host ('    安装根自动探测：{0}' -f $root)
}
$target = Join-Path $root 'QingShu.Toolkit'
if ((Test-Path -LiteralPath (Join-Path $target 'QingShu.Toolkit.exe')) -and -not $Force) {
    Write-Host ('    {0} 已存在且未加 -Force：跳过解压（保留现有文件与 plugins\ 用户资产）' -f $target)
} else {
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $target -Force
        Write-Host ('    解压完成 → {0}（覆盖式增量：不先删旧目录，zip 外文件一律保留）' -f $target)
    } catch {
        Fail '② 解压' ('{0}（exe 被占用时请先退出正在运行的青枢工具箱）' -f $_.Exception.Message)
    }
}
$script:DoneSteps += '②解压'

# ---------- ③ 基线配置（可选：-WithConfig 公共 config.json；不覆盖本机已有，除非 -Force） ----------
Write-Host '==> ③ 基线配置 ...' -ForegroundColor Cyan
$script:ConfigCopied = $false
if ($WithConfig -ne '') {
    if (-not (Test-Path -LiteralPath $WithConfig -PathType Leaf)) {
        Fail '③ 基线配置' ('基线 config.json 不存在：{0}' -f $WithConfig)
    }
    $appData = Join-Path $env:LOCALAPPDATA 'QingShu.Toolkit'
    New-Item -ItemType Directory -Path $appData -Force | Out-Null
    $destCfg = Join-Path $appData 'config.json'
    if ((Test-Path -LiteralPath $destCfg) -and -not $Force) {
        Write-Host ('    本机已有 config.json 且未加 -Force：保留本机配置（{0}）' -f $destCfg)
    } else {
        Copy-Item -LiteralPath $WithConfig -Destination $destCfg -Force
        $script:ConfigCopied = $true
        Write-Host ('    基线配置已就位：{0}' -f $destCfg)
    }
} else {
    Write-Host '    未指定 -WithConfig：跳过（装机后本机首启配置，可拷贝到同类机复用）'
}
$script:DoneSteps += '③基线配置'

# ---------- ④ 开机自启（可选：-Autostart 写 HKCU Run，公共机场景） ----------
Write-Host '==> ④ 开机自启 ...' -ForegroundColor Cyan
$script:AutostartDone = $false
if ($Autostart) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    $exe = Join-Path $target 'QingShu.Toolkit.exe'
    New-ItemProperty -Path $runKey -Name 'QingShu.Toolkit' -Value $exe -PropertyType String -Force | Out-Null
    $script:AutostartDone = $true
    Write-Host ('    HKCU Run 自启键已写：QingShu.Toolkit → {0}' -f $exe)
} else {
    Write-Host '    未加 -Autostart：跳过（需自启的公共机重跑加开关即可）'
}
$script:DoneSteps += '④自启'

# ---------- ⑤ 安装摘要（供装机点名核验） ----------
Write-Host '==> ⑤ 安装摘要 ...' -ForegroundColor Cyan
$versionText = ''
if ($zipPath -match 'QingShu\.Toolkit-(.+)\.zip') { $versionText = $Matches[1] }
$exeInstalled = Join-Path $target 'QingShu.Toolkit.exe'
$exeVer = ''
if (Test-Path -LiteralPath $exeInstalled) {
    $exeVer = (Get-Item -LiteralPath $exeInstalled).VersionInfo.FileVersion
}
$verLine = $versionText
if ($exeVer -ne '') { $verLine = '{0}（exe 文件版本 {1}）' -f $versionText, $exeVer }
$pluginNames = @()
$pluginsDir = Join-Path $target 'plugins'
if (Test-Path -LiteralPath $pluginsDir) {
    $pluginNames = @(Get-ChildItem -LiteralPath $pluginsDir -Directory | Sort-Object Name | ForEach-Object { $_.Name })
}
$configSource = '未指定（本机首启配置）'
if ($script:ConfigCopied) { $configSource = '基线复制（-WithConfig）' } elseif ($WithConfig -ne '') { $configSource = '保留本机已有' }
$autostartText = '未启用'
if ($script:AutostartDone) { $autostartText = '已写 HKCU Run 键' }
Write-Host '    ------------------------------'
Write-Host ('    安装目录：{0}' -f $target)
Write-Host ('    发布包  ：{0}' -f (Split-Path $zipPath -Leaf))
Write-Host ('    版本    ：{0}' -f $verLine)
Write-Host ('    SHA256  ：{0}' -f $actual)
Write-Host ('    插件({0}个)：{1}' -f $pluginNames.Count, ($pluginNames -join ', '))
Write-Host ('    配置来源：{0}' -f $configSource)
Write-Host ('    开机自启：{0}' -f $autostartText)
Write-Host '    ------------------------------'

Write-Host ''
Write-Host '[完成] 安装成功。人工验收（DEPLOY_ZIP.md §三.3）：启动 → 插件页七插件已加载 → 各工具页可进 →' -ForegroundColor Green
Write-Host '       弹窗拦截试拦（notepad 新窗）→ 设置页「检查更新」连通（机房网络放行清单见 §九）。' -ForegroundColor Green
exit 0
