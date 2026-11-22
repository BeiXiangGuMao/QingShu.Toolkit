<#
.SYNOPSIS
    青枢工具箱 Beta 一键发布脚本（当前版本 1.5.0-beta）。
    更新系统（自动检查更新）依赖仓库中存在 Release，首次使用先运行本脚本发布一次。
    zip 供应用内自动更新使用；MSIX（+ .cer 证书）为手动安装格式，可选产出。

.DESCRIPTION
    从项目文件读取版本号，执行 win-x64 发布，生成 ZIP 与 SHA-256；默认还生成签名
    MSIX 和 .cer，并创建或更新 GitHub Release 后上传资产。-PackOnly 仅生成本地文件，
    不访问 GitHub。

    用法（在仓库根目录的 PowerShell 中执行）：
      # 发布当前 Beta 版（自动读取版本并提示输入 Token）
      .\scripts\make_release.ps1

      # 只打包不上传（先本地验收 zip / msix / cer）
      .\scripts\make_release.ps1 -PackOnly

      # 发布纯宿主包（跳过默认插件装载与自动构建；插件可后补）
      .\scripts\make_release.ps1 -PackOnly -SkipPlugins

      # 本机安装 MSIX（自动提权导入证书 → 安装；解决 0x800B0109 证书不受信任）
      .\scripts\install_msix.ps1 -Msix .\QingShu.Toolkit-1.5.0-beta.msix

    Token 需要目标仓库 Contents 读写权限。建议使用 GITHUB_TOKEN 环境变量或安全提示输入，
    不要把 Token 直接写入命令行历史。发布版本必须高于用户本机版本，更新检查才会提示升级。

.PARAMETER Token
    GitHub Personal Access Token。缺省使用 GITHUB_TOKEN 环境变量；若未设置则安全提示输入。

.PARAMETER Tag
    Release 标签。缺省自动取 src/QingShu.Toolkit/*.csproj 的 <Version> 并加 v 前缀。

.PARAMETER Stable
    标记为正式发布（非预发布）。缺省：Version 含 "-beta" 等后缀时自动标记预发布。

.PARAMETER SkipBuild
    跳过 dotnet publish，直接使用现有发布目录打包（适合反复上传调试）。

.PARAMETER SkipPlugins
    跳过默认插件装载（含自动构建）。缺省：装载前自动构建 bin 下无产物的插件工程
    （dotnet build 失败即中止发布），并从插件工程根的 plugin.json.template 装载 plugin.json；
    正式版构建（宿主 csproj 定义 STABLE_BUILD）自动剔除实验性插件 qingshu.plugin.usbtool。

.PARAMETER PackOnly
    只构建并打包，不创建 Release（上传前本地验收）。

.PARAMETER NoMsix
    跳过 MSIX 打包。缺省会打包签名 MSIX 并附 .cer。

.PARAMETER SdkBin
    手动指定含 x64\makeappx.exe 与 x64\signtool.exe 的目录。
    缺省自动查找：Windows Kits 路径/注册表 → NuGet 缓存 → 从 NuGet 下载官方打包工具。
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Tag,
    [switch]$Stable,
    [switch]$SkipBuild,
    [switch]$SkipPlugins,
    [switch]$PackOnly,
    [switch]$NoMsix,
    # 关闭 R2R 启动预编译（默认开启；产物体积敏感/更新下载慢时使用）
    [switch]$NoR2R,
    [string]$SdkBin
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 默认可能未启用 TLS1.2，访问 api.github.com 会报"基础连接已经关闭"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$Repo = "BeiXiangGuMao/QingShu.Toolkit"
$ProjectDir = Join-Path $PSScriptRoot "..\src\QingShu.Toolkit"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ---- 1. 版本号 ----
$csproj = Get-ChildItem (Join-Path $ProjectDir "*.csproj") | Select-Object -First 1
[xml]$projXml = Get-Content $csproj.FullName -Encoding UTF8
$version = ($projXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "csproj 中未找到 <Version> 节点" }
if (-not $Tag) { $Tag = "v$version" }
$isPrerelease = -not $Stable -and $version -match '-'

Write-Host "==> 版本 $version  标签 $Tag  预发布=$isPrerelease" -ForegroundColor Cyan

# ---- 2. 构建 ----
$publishDir = Join-Path $ProjectDir "bin\publish\win-x64"
if (-not $SkipBuild) {
    # R2R 预编译（默认开）：启动阶段少跑 JIT，教学机 HDD/低配 CPU 上首帧与首交互明显加快；
    # 代价是产物与 ZIP 体积增大约 30-40%，更新下载敏感的场合可加 -NoR2R 关闭。
    $r2rArgs = @()
    if (-not $NoR2R) { $r2rArgs += '-p:PublishReadyToRun=true' }
    Write-Host "==> dotnet publish（Release / win-x64 / 自包含$(if (-not $NoR2R) { '，R2R 预编译提速启动' })）..." -ForegroundColor Cyan
    dotnet publish $csproj.FullName -c Release -r win-x64 `
        --self-contained true -p:WindowsAppSDKSelfContained=true `
        -p:Platform=x64 -p:TargetFramework=net8.0-windows10.0.19041.0 `
        @r2rArgs -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit=$LASTEXITCODE）" }
} else {
    Write-Host "==> 跳过构建，使用现有 $publishDir" -ForegroundColor Yellow
}
if (-not (Test-Path (Join-Path $publishDir "QingShu.Toolkit.exe"))) {
    throw "发布目录缺少 QingShu.Toolkit.exe，请先构建（或去掉 -SkipBuild）"
}

# ---- 2.5 默认插件装载（自动构建 + harness 风格：发布包自带 plugins\；更新器 robocopy /e 增量不删，
#      用户自装插件与已改配置不受更新影响）。仅补缺失文件：重复打包不会覆盖已改的 plugin.json。
#      bin 下无构建产物时自动 dotnet build（2026-11 实测：文档发布流程未提"先构建插件"，照跑 7 插件全跳过），
#      构建失败即中止发布——宁可不发不发残缺包，-SkipPlugins 可显式发布纯宿主包。
#      plugin.json 从插件工程根的 plugin.json.template 装载（插件工程未设 CopyToOutputDirectory，
#      bin 下无该文件——旧口径从 bin 找 template 永远落空，装载出的插件缺清单无法加载）。
#      SdkRoot = 插件工程所在子目录（galamaster/galaagent 在 gala-dispatch，弹窗拦截在 popup-guard，
#      U盘工具在 usb-tool，磁盘清理在 disk-cleanup，系统优化在 optimize，网络诊断在 network-diag）。
$isStableBuild = (($projXml.Project.PropertyGroup | ForEach-Object { $_.DefineConstants } | Where-Object { $_ } | Select-Object -First 1) -match '(^|;)\s*STABLE_BUILD\s*(;|$)')
$defaultPlugins = @(
    @{ Id = "qingshu.galamaster"; Sdk = "QingShu.Plugin.GalaMaster";  SdkRoot = "plugins-sdk\gala-dispatch" },
    @{ Id = "qingshu.galaagent";  Sdk = "QingShu.Plugin.GalaAgent";   SdkRoot = "plugins-sdk\gala-dispatch" },
    @{ Id = "qingshu.plugin.popupguard"; Sdk = "QingShu.Plugin.PopupGuard"; SdkRoot = "plugins-sdk\popup-guard" },
    @{ Id = "qingshu.plugin.usbtool";    Sdk = "QingShu.Plugin.UsbTool";    SdkRoot = "plugins-sdk\usb-tool" },
    @{ Id = "qingshu.plugin.diskcleanup"; Sdk = "QingShu.Plugin.DiskCleanup"; SdkRoot = "plugins-sdk\disk-cleanup" },
    @{ Id = "qingshu.plugin.optimize"; Sdk = "QingShu.Plugin.Optimize"; SdkRoot = "plugins-sdk\optimize" },
    @{ Id = "qingshu.plugin.networkdiag"; Sdk = "QingShu.Plugin.NetworkDiag"; SdkRoot = "plugins-sdk\network-diag" }
)
if ($isStableBuild) {
    # 正式版（STABLE_BUILD 剔除口径）：U盘工具为实验性功能（AppBuildProfile.UsbToolEnabled=false，
    # 宿主导航入口隐藏/自动加载静默跳过/手动加载明确拒绝三重门禁），正式包不装载其插件文件
    # ——与"正式版=预览版砍掉实验性功能的产物、编译期整体剔除"哲学一致（docs/RELEASE.md 第五章）。
    $defaultPlugins = @($defaultPlugins | Where-Object { $_.Id -ne "qingshu.plugin.usbtool" })
    Write-Host "    正式版构建（STABLE_BUILD）：实验性插件 qingshu.plugin.usbtool 不入包（宿主已编译期剔除该功能）" -ForegroundColor DarkGray
    $excludedPluginDir = Join-Path $publishDir "plugins\qingshu.plugin.usbtool"
    if (Test-Path $excludedPluginDir) {
        Remove-Item -LiteralPath $excludedPluginDir -Recurse -Force
    }
}
function Find-PluginDll([string]$binRoot, [string]$sdk) {
    Get-ChildItem $binRoot -Recurse -Filter "$sdk.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' } | Select-Object -First 1
}
if ($SkipPlugins) {
    Write-Host "==> 跳过默认插件装载（-SkipPlugins：发布纯宿主包，插件可后补）" -ForegroundColor Yellow
} else {
    # 显式传 QingshuHome：宿主 §2 已 publish 完成，指向 $publishDir——命令行传参必生效，
    # 不受 MSBuild 节点复用按旧值求值影响（与 build_all_plugins.ps1 同口径；
    # 插件 csproj 另有五级探测兜底，显式传参后不依赖兜底）
    $hostHome = (Resolve-Path $publishDir).Path
    foreach ($dp in $defaultPlugins) {
        $binRoot = Join-Path (Join-Path $RepoRoot $dp.SdkRoot) "$($dp.Sdk)\bin"
        $srcDll = Find-PluginDll $binRoot $dp.Sdk
        if (-not $srcDll) {
            Write-Host "    · $($dp.Sdk)：bin 下无构建产物，自动 dotnet build（Release，QingshuHome=$hostHome）..." -ForegroundColor Cyan
            dotnet build (Join-Path (Join-Path $RepoRoot $dp.SdkRoot) "$($dp.Sdk)\$($dp.Sdk).csproj") -c Release -v minimal --nologo -p:QingshuHome="$hostHome"
            if ($LASTEXITCODE -ne 0) { throw "插件构建失败：$($dp.Sdk)（exit=$LASTEXITCODE）。修复后重跑；或加 -SkipPlugins 显式发布无插件包。" }
            $srcDll = Find-PluginDll $binRoot $dp.Sdk
            if (-not $srcDll) { throw "插件构建完成但未找到产物：$($dp.Sdk)（检查 $binRoot）" }
        }
        $target = Join-Path $publishDir "plugins\$($dp.Id)"
        New-Item -ItemType Directory -Force $target | Out-Null
        $staged = 0
        # plugin.json.template 与插件 csproj 同级（工程根），装载时落为 plugin.json
        $tpl = Join-Path (Join-Path (Join-Path $RepoRoot $dp.SdkRoot) $dp.Sdk) "plugin.json.template"
        foreach ($f in @($srcDll.FullName,
                         [IO.Path]::ChangeExtension($srcDll.FullName, ".pdb"),
                         $tpl)) {
            if (-not (Test-Path $f)) { continue }
            $dstName = (Get-Item $f).Name -replace '\.template$', ''
            $dst = Join-Path $target $dstName
            if (-not (Test-Path $dst)) {
                Copy-Item $f $dst -Force
                $staged++
            }
        }
        Write-Host ("    · 默认插件 {0}：装载 {1} 个文件 → {2}" -f $dp.Id, $staged, $target) -ForegroundColor DarkCyan
    }
    Write-Host "    注：装载的默认插件无 plugin.sig，宿主按可选签名策略直接放行加载；如需官方签名徽章，装载后运行 tools\plugin-sign.ps1 -PluginFolder <插件目录> 重签。" -ForegroundColor DarkGray
}

# ---- 3. 打包 ----
$zipPath = Join-Path $RepoRoot "QingShu.Toolkit-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "==> 打包 $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1048576)
Write-Host "    完成（$zipSize）" -ForegroundColor Green

# ---- 3.1 SHA256 摘要与 sidecar（发布→部署哈希链闭环：DEPLOY_ZIP.md §六） ----
# zip 旁写同名 .sha256（格式：<哈希>  <文件名>，sha256sum/Get-FileHash 通用）；
# 机房装机脚本 deploy_install.ps1 读取它做部署前核验，不一致即中止（防共享目录传输损坏/误替换）。
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$hashSidecar = "$zipPath.sha256"
Set-Content -Path $hashSidecar -Value ('{0}  {1}' -f $zipHash, (Split-Path $zipPath -Leaf)) -Encoding ascii
Write-Host ('    SHA256: {0}' -f $zipHash) -ForegroundColor DarkCyan
Write-Host ('    摘要已写入 {0}（随 zip 一同放共享目录，deploy_install.ps1 自动核验）' -f (Split-Path $hashSidecar -Leaf)) -ForegroundColor DarkCyan

# ---- 4. 打包签名 MSIX（Windows SDK MakeAppx + SignTool；-NoMsix 跳过） ----
# MSIX 为手动安装格式：必须先装 .cer 证书（本地计算机 → 受信任的人）再装 .msix，
# 否则 Add-AppxPackage 报 0x800B0109（签名根证书不受信任）。仓库内一键完成：
# .\scripts\install_msix.ps1（自动提权导证书 + 安装，证书已信任时不重复提权）。
# 应用内自动更新始终使用 zip 资产，与 MSIX 互不影响（MSIX 安装目录只读，勿在其内测自动更新）。
$msixPath = ""
$cerPath = ""
if (-not $NoMsix) {
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
        throw "无法从版本号 '$version' 解析数字段；MSIX 要求 x.y.z.w 四段版本号"
    }
    $msixVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

    # 定位 MakeAppx/SignTool：-SdkBin 手动指定 → Windows Kits 路径/注册表 →
    # NuGet 全局缓存 → 从 NuGet 下载微软官方 Microsoft.Windows.SDK.BuildTools。
    # 注意：Windows App SDK（构建 WinUI 用）不提供 MakeAppx，它们是两个东西。
    $makeappx = ""
    $signtool = ""
    $toolRoot = ""
    if ($SdkBin) {
        if ((Test-Path (Join-Path $SdkBin "x64\makeappx.exe")) -and (Test-Path (Join-Path $SdkBin "x64\signtool.exe"))) {
            $toolRoot = $SdkBin
        } else { throw "-SdkBin 指定目录下没有 x64\makeappx.exe / x64\signtool.exe：$SdkBin" }
    }
    if (-not $toolRoot) {
        $searchDirs = @(
            (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"),
            (Join-Path $env:ProgramFiles "Windows Kits\10\bin")
        )
        foreach ($regPath in @(
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SDKs\Windows\v10.0",
            "HKCU:\SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0",
            "HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots",
            "HKCU:\SOFTWARE\Microsoft\Windows Kits\Installed Roots"
        )) {
            try {
                $props = Get-ItemProperty -Path $regPath -ErrorAction Stop
                $root10 = $props.InstallationFolder
                if (-not $root10) { $root10 = $props.KitsRoot10 }
                if ($root10) { $searchDirs += (Join-Path $root10 "bin") }
            } catch { }
        }
        foreach ($dir in $searchDirs) {
            if ($toolRoot) { break }
            if (-not (Test-Path $dir)) { continue }
            if ((Test-Path (Join-Path $dir "x64\makeappx.exe")) -and (Test-Path (Join-Path $dir "x64\signtool.exe"))) {
                $toolRoot = $dir
                break
            }
            $hit = Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue |
                Where-Object { (Test-Path (Join-Path $_.FullName "x64\makeappx.exe")) -and (Test-Path (Join-Path $_.FullName "x64\signtool.exe")) } |
                Sort-Object Name -Descending | Select-Object -First 1
            if ($hit) { $toolRoot = $hit.FullName }
        }
    }
    if (-not $toolRoot) {
        # NuGet 全局缓存兜底（装过含 Microsoft.Windows.SDK.BuildTools 引用的项目即有）
        $nugetGlobal = ""
        try {
            $locLine = (& dotnet nuget locals global-packages --list) -join " "
            if ($locLine -match 'global-packages:\s*(.+)$') { $nugetGlobal = $Matches[1].Trim() }
        } catch { }
        if ($nugetGlobal -and (Test-Path (Join-Path $nugetGlobal "microsoft.windows.sdk.build.tools"))) {
            $pkgRoot = Join-Path $nugetGlobal "microsoft.windows.sdk.build.tools"
            $hit = Get-ChildItem $pkgRoot -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -like "*x64" } |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($hit) {
                $toolRoot = $hit.DirectoryName
                Write-Host "    已从 NuGet 缓存定位打包工具：$toolRoot" -ForegroundColor Yellow
            }
        }
    }
    if (-not $toolRoot) {
        # 最后兜底：下载微软官方打包工具包（一次性，缓存到本地复用）
        $toolsCache = Join-Path $env:LOCALAPPDATA "QingShu.Toolkit\sdk-build-tools"
        $marker = Join-Path $toolsCache "makeappx.located"
        if (Test-Path $marker) {
            $toolRoot = (Get-Content $marker -Raw).Trim()
        } else {
            Write-Host "==> 本机未安装 Windows 10/11 SDK，改从 NuGet 下载微软官方打包工具（约 70MB，仅一次）..." -ForegroundColor Yellow
            $downloaded = $false
            foreach ($pkgVer in @("10.0.28000.2705", "10.0.28000.2526", "10.0.22621.756")) {
                $zipFile = Join-Path $env:TEMP "winsdk-buildtools-$pkgVer.zip"
                try {
                    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.build.tools/$pkgVer/microsoft.windows.sdk.build.tools.$pkgVer.nupkg" -OutFile $zipFile -UseBasicParsing
                    $downloaded = $true
                    break
                } catch { }
            }
            if (-not $downloaded) { throw "无法从 NuGet 下载打包工具且本机无 Windows 10/11 SDK。解决：VS 安装器 → 单个组件 → 勾选『Windows 10/11 SDK』，或加 -NoMsix 跳过，或 -SdkBin 手动指定。" }
            New-Item -ItemType Directory -Path $toolsCache -Force | Out-Null
            Expand-Archive -Path $zipFile -DestinationPath $toolsCache -Force
            Remove-Item $zipFile -Force
            $hit = Get-ChildItem $toolsCache -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.DirectoryName -like "*x64" } | Select-Object -First 1
            if (-not $hit) { throw "下载的工具包内容异常（未找到 x64 makeappx.exe）。" }
            $toolRoot = $hit.DirectoryName
            Set-Content -Path $marker -Value $toolRoot -Encoding ASCII
        }
    }
    $makeappx = Join-Path $toolRoot "x64\makeappx.exe"
    $signtool = Join-Path $toolRoot "x64\signtool.exe"
    foreach ($tool in @($makeappx, $signtool)) {
        if (-not (Test-Path $tool)) { throw "缺少工具：$tool。可加 -NoMsix 跳过 MSIX。" }
    }
    Write-Host "    打包工具：$toolRoot"

    # 签名证书按主题复用（各版本签名一致 → 用户只需装一次证书），没有才新建。
    # 复用候选必须私钥可打开——KSP 持久化失败的机器会出现"证书在、密钥容器丢"
    # 的废证书（certutil 显示'找不到储存的密钥集'），必须排除并在新建前清理
    $certSubject = "CN=QingShu.Toolkit"
    $certPfxPath = ""
    $cert = $null
    foreach ($cand in @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $certSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
            Sort-Object NotAfter -Descending)) {
        try {
            if ($cand.HasPrivateKey -and $cand.PrivateKey) { $cert = $cand; break }
        } catch { }
    }
    if (-not $cert) {
        foreach ($junk in @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -eq $certSubject })) {
            $junkDead = $false
            try { if (-not $junk.HasPrivateKey -or -not $junk.PrivateKey) { $junkDead = $true } } catch { $junkDead = $true }
            if ($junkDead) {
                Remove-Item ('Cert:\CurrentUser\My\{0}' -f $junk.Thumbprint) -Force
                Write-Host ('    已清理私钥丢失的废证书：{0}' -f $junk.Thumbprint) -ForegroundColor Yellow
            }
        }
        # 四级兜底：部分机器 CertEnroll/CNG 创建密钥报 0x80090020 NTE_FAIL
        # （KSP 服务异常/密钥存储损坏/组策略限制），逐级降级直到成功
        $textExt = @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $t1err = ""
        $t2err = ""
        $t3err = ""
        $t4err = ""
        try {
            $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature -FriendlyName "QingShu.Toolkit MSIX" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) -TextExtension $textExt
            Write-Host "    已新建签名证书（5 年有效）：$($cert.Thumbprint)" -ForegroundColor Yellow
        } catch {
            $t1err = $_.Exception.Message
            Write-Host "    默认 KSP 创建证书失败，改用传统加密提供程序重试 ..." -ForegroundColor Yellow
        }
        if (-not $cert) {
            # T2：传统 CAPI CSP 轮试——不同机器注册表里 CSP 注册项可能部分缺失
            foreach ($csp in @("Microsoft Enhanced Cryptographic Provider v1.5", "Microsoft Strong Cryptographic Provider", "Microsoft Base Cryptographic Provider v1.5")) {
                try {
                    $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject -KeyUsage DigitalSignature -KeySpec KeyExchange -FriendlyName "QingShu.Toolkit MSIX" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) -KeyLength 2048 -Provider $csp -TextExtension $textExt
                    Write-Host ('    已用 {0} 新建证书（5 年有效）：{1}' -f $csp, $cert.Thumbprint) -ForegroundColor Yellow
                    break
                } catch {
                    $t2err = ('{0} / {1}' -f $t2err, $_.Exception.Message).Trim(' /')
                }
            }
            if (-not $cert) { Write-Host "    传统提供程序全部无效，改用 .NET 密钥直签兜底 ..." -ForegroundColor Yellow }
        }
        if (-not $cert) {
            # T3：.NET 直签全内存路径——密钥生成/自签/导 PFX 全程在内存完成，
            # 不依赖证书存储与 KSP/CAPI 持久化（CertEnroll/KSP/certreq 全挂的
            # 极端机器上的救命路径）。密钥源两级：CAPI 容器 → CNG 临时密钥。
            # 注意 RSASignaturePadding 位于 System.Security.Cryptography 命名空间
            # （不在 X509Certificates 下，误加前缀时 PS5.1 报"找不到类型"）
            $certPfxPath = Join-Path $env:TEMP "qs-msix-memory.pfx"
            if (Test-Path $certPfxPath) { Remove-Item $certPfxPath -Force }
            $errCapi = ""
            $errCng = ""
            foreach ($keySrc in @("capi", "cng")) {
                try {
                    if ($keySrc -eq "capi") {
                        $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(2048)
                    } else {
                        # CNG 临时密钥（Ephemeral）：完全不落盘，绕开一切持久化故障
                        $kp = New-Object System.Security.Cryptography.CngKeyCreationParameters
                        $kp.ExportPolicy = [System.Security.Cryptography.CngExportPolicies]::AllowExport -bor [System.Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
                        $cngKey = [System.Security.Cryptography.CngKey]::Create([System.Security.Cryptography.CngAlgorithm]::Rsa, $null, $kp)
                        $rsa = New-Object System.Security.Cryptography.RSACng($cngKey)
                    }
                    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest($certSubject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
                    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
                    $oids = New-Object System.Security.Cryptography.OidCollection
                    $null = $oids.Add("1.3.6.1.5.5.7.3.3")
                    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oids, $false)))
                    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $false)))
                    $selfSigned = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(5))
                    # 导出带私钥 PFX（纯内存序列化，签名凭据不依赖任何密钥存储）
                    [System.IO.File]::WriteAllBytes($certPfxPath, $selfSigned.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, "qs-toolkit-local-pfx"))
                    $cert = $selfSigned
                    break
                } catch {
                    if ($keySrc -eq "capi") { $errCapi = $_.Exception.Message } else { $errCng = $_.Exception.Message }
                }
            }
            if ($cert) {
                # Best-effort 入库（成功则后续运行复用同一证书，用户只需装一次 .cer）。
                # 入库后强制回查验证私钥可打开——假入库（KSP 静默丢私钥）时降级为
                # 临时证书模式：本轮照常出包，但证书每轮重建、.cer 需重新安装
                $storeOk = $false
                try {
                    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store([System.Security.Cryptography.X509Certificates.StoreName]::My, [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
                    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    $store.Add($cert)
                    $store.Close()
                    $inStore = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $cert.Thumbprint } | Select-Object -First 1
                    if ($inStore -and $inStore.HasPrivateKey -and $inStore.PrivateKey) { $storeOk = $true }
                } catch { $storeOk = $false }
                if ($storeOk) {
                    Write-Host "    已用 .NET 直签兜底新建证书（5 年有效）：$($cert.Thumbprint)" -ForegroundColor Yellow
                } else {
                    Write-Host "    本机证书存储无法可靠保存私钥（KSP 异常），改用临时证书签名——本轮照常出包；下次发布将重建证书，届时需重新安装 .cer" -ForegroundColor Yellow
                }
            } else {
                $t3err = ('{0} / {1}' -f $errCapi, $errCng).Trim(' /')
                if (Test-Path $certPfxPath) { Remove-Item $certPfxPath -Force -ErrorAction SilentlyContinue }
                $certPfxPath = ""
                Write-Host "    .NET 直签也失败，改用 certreq 兜底 ..." -ForegroundColor Yellow
            }
        }
        if (-not $cert) {
            # T4：certreq.exe + INF 自签证书——独立进程、传统 CAPI 路径，
            # 与 CertEnroll cmdlet 实现互异，前三级挂掉时仍可能成功
            try {
                $prePrints = @(Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | ForEach-Object { $_.Thumbprint })
                $infPath = Join-Path $env:TEMP "qs-msix-cert.inf"
                $reqPath = Join-Path $env:TEMP "qs-msix-cert.req"
                $infText = @"
[Version]
Signature="`$Windows NT`$"

[NewRequest]
Subject = "CN=QingShu.Toolkit"
KeyLength = 2048
; KeySpec must be 1 (AT_KEYEXCHANGE): KeySpec=2 (AT_SIGNATURE) private keys
; cannot be exported to PFX, breaking Export-PfxCertificate later
KeySpec = 1
Exportable = TRUE
MachineKeySet = FALSE
KeyUsage = 0x80
RequestType = Cert
ValidityPeriod = Years
ValidityPeriodUnits = 5

[EnhancedKeyUsageExtension]
OID = 1.3.6.1.5.5.7.3.3
"@
                Set-Content -Path $infPath -Value $infText -Encoding ASCII
                $certReqOut = & certreq.exe -new $infPath $reqPath 2>&1
                if ($LASTEXITCODE -ne 0) { throw ('certreq exit={0}：{1}' -f $LASTEXITCODE, ($certReqOut -join ' ')) }
                $cert = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $prePrints -notcontains $_.Thumbprint -and $_.Subject -eq $certSubject }) |
                    Sort-Object NotAfter -Descending | Select-Object -First 1
                if (-not $cert) { throw "certreq 报成功但未在 CurrentUser\My 找到新证书" }
                try { if (-not $cert.HasPrivateKey -or -not $cert.PrivateKey) { $cert = $null } } catch { $cert = $null }
                if (-not $cert) { throw "certreq 证书已入库但私钥未持久化" }
                Write-Host "    已用 certreq 兜底新建证书（5 年有效）：$($cert.Thumbprint)" -ForegroundColor Yellow
            } catch {
                $t4err = $_.Exception.Message
            }
        }
        if (-not $cert) {
            throw ('证书创建四级方式全部失败。① {0} ② {1} ③ {2} ④ {3}。处置建议：先重启电脑再重试（多数 NTE_FAIL 属密钥服务临时故障）；仍失败可尝试管理员 PowerShell 执行 Restart-Service CryptSvc，并检查 %APPDATA%\Microsoft\Crypto\RSA 目录是否可写（可将其重命名后重试）；排查第三方杀软或组策略加密限制；或加 -NoMsix 先跳过 MSIX 打包（zip 与应用内自动更新不受影响）。' -f $t1err, $t2err, $t3err, $t4err)
        }
    }

    $cerPath = Join-Path $RepoRoot "QingShu.Toolkit-$version.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    # 暂存目录 = 发布输出（去 .pdb）+ AppxManifest + 图标
    $stage = Join-Path $env:TEMP "qs-msix-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $stage -Recurse -Force
    Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force

    # 图标：直接复用仓库 Assets（tools/make_assets.py 由设计稿源图生成），不再现场绘制
    $assetsDir = Join-Path $stage "Assets"
    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
    $srcAssets = Join-Path $RepoRoot "src\QingShu.Toolkit\Assets"
    foreach ($logo in @("Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png")) {
        Copy-Item (Join-Path $srcAssets $logo) (Join-Path $assetsDir $logo) -Force
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="QingShuToolkit" Publisher="CN=QingShu.Toolkit" Version="$msixVersion" />
  <Properties>
    <DisplayName>青枢工具箱</DisplayName>
    <PublisherDisplayName>BeiXiangGuMao</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Resources>
    <Resource Language="zh-cn" />
  </Resources>
  <Applications>
    <Application Id="App" Executable="QingShu.Toolkit.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="青枢工具箱" Description="学校专用系统工具箱：网络诊断、磁盘清理、性能优化与功耗管理" BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@
    $manifest | Out-File -FilePath (Join-Path $stage "AppxManifest.xml") -Encoding utf8

    $msixPath = Join-Path $RepoRoot "QingShu.Toolkit-$version.msix"
    if (Test-Path $msixPath) { Remove-Item $msixPath -Force }
    Write-Host "==> MakeAppx 打包 MSIX（版本 $msixVersion）..." -ForegroundColor Cyan
    & $makeappx pack /o /d $stage /p $msixPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx 打包失败（exit=$LASTEXITCODE）" }

    # SignTool 签名三级：① T3 全内存路径已产出的 PFX 直接用；
    # ② 存储证书导 PFX；③ 私钥不可导时按 SHA1 指纹直签存储证书
    # （PFX 仅本地临时使用，不随 Release 上传）
    $signed = $false
    $pfxErr = ""
    $memPfxTried = $false
    if ($certPfxPath -and (Test-Path $certPfxPath)) {
        $memPfxTried = $true
        & $signtool sign /fd SHA256 /f $certPfxPath /p "qs-toolkit-local-pfx" $msixPath | Out-Null
        if ($LASTEXITCODE -eq 0) { $signed = $true } else { $pfxErr = "内存 PFX 签名 exit=$LASTEXITCODE" }
        Remove-Item $certPfxPath -Force -ErrorAction SilentlyContinue
    }
    if (-not $signed -and -not $memPfxTried) {
        $pfxPath = Join-Path $env:TEMP "qs-msix-sign.pfx"
        if (Test-Path $pfxPath) { Remove-Item $pfxPath -Force }
        $pfxPwd = ConvertTo-SecureString -String "qs-toolkit-local-pfx" -Force -AsPlainText
        try {
            Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPwd -ErrorAction Stop | Out-Null
        } catch {
            $pfxErr = $_.Exception.Message
            Write-Host "    私钥不可导出 PFX，改按 SHA1 指纹直签存储证书 ..." -ForegroundColor Yellow
        }
        if (-not $pfxErr) {
            & $signtool sign /fd SHA256 /f $pfxPath /p "qs-toolkit-local-pfx" $msixPath | Out-Null
            if ($LASTEXITCODE -eq 0) { $signed = $true }
            Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $signed) {
        & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msixPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw ('SignTool 签名失败（exit={0}）。PFX 路径错误：{1}。若 certutil -user -store My {2} 显示"找不到储存的密钥集"，表示证书私钥已丢失——新版脚本已会自动清理废证书并重建，拉取最新代码重跑即可。' -f $LASTEXITCODE, $pfxErr, $cert.Thumbprint) }
    }
    Remove-Item $stage -Recurse -Force
    $msixSize = "{0:N1} MB" -f ((Get-Item $msixPath).Length / 1048576)
    Write-Host "    完成（$msixSize）+ 证书 $cerPath" -ForegroundColor Green
}

if ($PackOnly) {
    Write-Host "==> -PackOnly 模式：不创建 Release。产物：" -ForegroundColor Yellow
    Write-Host "    zip ：$zipPath"
    Write-Host "    哈希：$hashSidecar（部署核验用，随 zip 一同分发）"
    if ($msixPath) {
        Write-Host "    msix：$msixPath"
        Write-Host ('    cer ：{0} —— 本机验收：.\scripts\install_msix.ps1 -Msix "{1}" 一键装证书并安装' -f $cerPath, $msixPath)
    }
    return
}

# ---- 5. Token ----
if (-not $Token) {
    $sec = Read-Host "请输入 GitHub PAT（repo 权限，输入不回显）" -AsSecureString
    $Token = [System.Net.NetworkCredential]::new("", $sec).Password
}
$headers = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    'User-Agent'  = "QingShu.Toolkit-release-script"
}

# ---- 6. 校验 Token 与仓库可达性（私库无有效 Token 时 GitHub 同样返 404） ----
try {
    $null = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo"
} catch {
    throw "无法访问仓库 $Repo —— 请确认：① PAT 有效且具 repo（Contents 读写）权限；② 仓库归属/名称正确。$($_.Exception.Message)"
}

# ---- 7. 创建 Release（已存在则复用；404=该 tag 尚无 Release，首次发布正常路径） ----
$rel = $null
$existing = $null
try {
    $existing = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag"
} catch {
    $existing = $null
}
if ($existing -and $existing.id) {
    Write-Host "==> Release $Tag 已存在（id=$($existing.id)），复用并更新资产" -ForegroundColor Yellow
    $rel = $existing
} else {
    $body = @{
        tag_name         = $Tag
        name             = "青枢工具箱 $version"
        prerelease       = [bool]$isPrerelease
        body             = "青枢工具箱 $version 发布。zip：解压替换即可使用，应用内「设置 → 自动更新」可直接在线升级。MSIX（手动安装格式）：先双击 .cer 把证书装到「本地计算机 → 受信任的人」（或管理员 PowerShell 执行 certutil -addstore TrustedPeople <cer文件名>），再双击 .msix 安装；应用内自动更新仅支持 zip 版。"
    } | ConvertTo-Json
    Write-Host "==> 创建 Release $Tag ..." -ForegroundColor Cyan
    $rel = Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" `
        -Uri "https://api.github.com/repos/$Repo/releases" -Body $body
}
Write-Host "    Release id=$($rel.id)  预发布=$($rel.prerelease)" -ForegroundColor Green

# ---- 8. 上传资产（同名先删后传；zip 必传，MSIX/证书存在则附带） ----
$assetFiles = @($zipPath)
if ($msixPath -and (Test-Path $msixPath)) { $assetFiles += $msixPath }
if ($cerPath -and (Test-Path $cerPath)) { $assetFiles += $cerPath }
foreach ($f in $assetFiles) {
    $assetName = Split-Path $f -Leaf
    foreach ($a in @($rel.assets)) {
        if ($a.name -eq $assetName) {
            Write-Host "==> 删除旧资产 $assetName" -ForegroundColor Yellow
            Invoke-RestMethod -Method Delete -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/assets/$($a.id)" | Out-Null
        }
    }
    $ctype = "application/zip"
    if ($assetName -like "*.msix") { $ctype = "application/vnd.ms-appx" }
    if ($assetName -like "*.cer") { $ctype = "application/x-x509-ca-cert" }
    $fsize = "{0:N1} MB" -f ((Get-Item $f).Length / 1048576)
    Write-Host "==> 上传 $assetName（$fsize，大文件耐心等待）..." -ForegroundColor Cyan
    $upload = Invoke-RestMethod -Method Post `
        -Headers @{ Authorization = "Bearer $Token"; 'User-Agent' = "QingShu.Toolkit-release-script" } `
        -ContentType $ctype `
        -Uri "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$assetName" `
        -InFile $f
    Write-Host "    完成：$($upload.browser_download_url)"
}

Write-Host ""
Write-Host "提示：现在可在应用内「设置 → 自动更新」点击【立即检查更新】验证；" -ForegroundColor Cyan
Write-Host "      tag 数字版本需高于用户本机版本（本机读取 csproj 程序集版本）才会提示升级。"
Write-Host "      MSIX 为手动安装格式（先装 .cer 证书再装 .msix）；应用内自动更新仅走 zip。"
