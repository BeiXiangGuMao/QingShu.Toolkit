<#
.SYNOPSIS
    青枢工具箱一键发布脚本：本地构建 → 打 zip + 签名 MSIX → 创建 GitHub Release → 上传资产。
    更新系统（自动检查更新）依赖仓库中存在 Release，首次使用先运行本脚本发布一次。
    zip 供应用内自动更新使用；MSIX（+ .cer 证书）为手动安装格式，可选产出。

.DESCRIPTION
    用法（在仓库根目录的 PowerShell 中执行）：
      # 发布 Beta 版（tag 取 csproj Version，如 1.2.0-beta → v1.2.0-beta，标记为预发布）
      .\scripts\make_release.ps1 -Token <你的GitHub PAT>

      # 发布稳定版（覆盖 tag，非预发布）
      .\scripts\make_release.ps1 -Token <PAT> -Tag v1.3.0 -Stable

      # 只打包不上传（先本地验收 zip / msix / cer）
      .\scripts\make_release.ps1 -PackOnly

      # 本机安装 MSIX（自动提权导入证书 → 安装；解决 0x800B0109 证书不受信任）
      .\scripts\install_msix.ps1 -Msix .\QingShu.Toolkit-<版本>.msix

    Token 权限：repo（Contents: read & write）即可。
    注意：发布的 tag 版本号必须高于用户本机版本号，更新检查才会提示升级。

.PARAMETER Token
    GitHub Personal Access Token（repo 权限）。也可通过环境变量 GITHUB_TOKEN 提供。

.PARAMETER Tag
    Release 标签。缺省自动取 src/QingShu.Toolkit/*.csproj 的 <Version> 并加 v 前缀。

.PARAMETER Stable
    标记为正式发布（非预发布）。缺省：Version 含 "-beta" 等后缀时自动标记预发布。

.PARAMETER SkipBuild
    跳过 dotnet publish，直接使用现有发布目录打包（适合反复上传调试）。

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
    [switch]$PackOnly,
    [switch]$NoMsix,
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
    Write-Host "==> dotnet publish（Release / win-x64 / 自包含）..." -ForegroundColor Cyan
    dotnet publish $csproj.FullName -c Release -r win-x64 `
        --self-contained true -p:WindowsAppSDKSelfContained=true `
        -p:Platform=x64 -p:TargetFramework=net8.0-windows10.0.19041.0 `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit=$LASTEXITCODE）" }
} else {
    Write-Host "==> 跳过构建，使用现有 $publishDir" -ForegroundColor Yellow
}
if (-not (Test-Path (Join-Path $publishDir "QingShu.Toolkit.exe"))) {
    throw "发布目录缺少 QingShu.Toolkit.exe，请先构建（或去掉 -SkipBuild）"
}

# ---- 3. 打包 ----
$zipPath = Join-Path $RepoRoot "QingShu.Toolkit-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "==> 打包 $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
$zipSize = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1048576)
Write-Host "    完成（$zipSize）" -ForegroundColor Green

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
        # NuGet 全局缓存兑底（装过含 Microsoft.Windows.SDK.BuildTools 引用的项目即有）
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
        # 最后兑底：下载微软官方打包工具包（一次性，缓存到本地复用）
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

    # 签名证书按主题复用（各版本签名一致 → 用户只需装一次证书），没有才新建
    $certSubject = "CN=QingShu.Toolkit"
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $certSubject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $certSubject `
            -KeyUsage DigitalSignature -FriendlyName "QingShu.Toolkit MSIX" `
            -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        Write-Host "    已新建签名证书（5 年有效）：$($cert.Thumbprint)" -ForegroundColor Yellow
    }

    $cerPath = Join-Path $RepoRoot "QingShu.Toolkit-$version.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null

    # 暂存目录 = 发布输出（去 .pdb）+ AppxManifest + 图标
    $stage = Join-Path $env:TEMP "qs-msix-stage"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $publishDir "*") -Destination $stage -Recurse -Force
    Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force

    $assetsDir = Join-Path $stage "Assets"
    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
    Add-Type -AssemblyName System.Drawing
    foreach ($pair in @(@("Square44x44Logo.png", 44), @("Square150x150Logo.png", 150), @("StoreLogo.png", 50))) {
        $logoPath = Join-Path $assetsDir $pair[0]
        $size = $pair[1]
        $bmp = New-Object System.Drawing.Bitmap($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 110, 235))
        $g.FillRectangle($bg, 0, 0, $size, $size)
        $font = New-Object System.Drawing.Font("Microsoft YaHei UI", ($size * 0.5), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
        $g.DrawString("青", $font, [System.Drawing.Brushes]::White, $rect, $fmt)
        $g.Dispose()
        $bmp.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
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
      <uap:VisualElements DisplayName="青枢工具箱" Description="学校系统工具箱" BackgroundColor="transparent"
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

    # SignTool 签名（PFX 仅本地临时使用，不随 Release 上传）
    $pfxPath = Join-Path $env:TEMP "qs-msix-sign.pfx"
    if (Test-Path $pfxPath) { Remove-Item $pfxPath -Force }
    $pfxPwd = ConvertTo-SecureString -String "qs-toolkit-local-pfx" -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pfxPwd | Out-Null
    & $signtool sign /fd SHA256 /f $pfxPath /p "qs-toolkit-local-pfx" $msixPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SignTool 签名失败（exit=$LASTEXITCODE）" }
    Remove-Item $pfxPath -Force
    Remove-Item $stage -Recurse -Force
    $msixSize = "{0:N1} MB" -f ((Get-Item $msixPath).Length / 1048576)
    Write-Host "    完成（$msixSize）+ 证书 $cerPath" -ForegroundColor Green
}

if ($PackOnly) {
    Write-Host "==> -PackOnly 模式：不创建 Release。产物：" -ForegroundColor Yellow
    Write-Host "    zip ：$zipPath"
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
        body             = "青枢工具箱 $version 发布。zip：解压替换即可使用，应用内「设置 → 自动更新」可直接在线升级。MSIX（手动安装格式）：先双击 .cer 把证书装到「本地计算机 → 受信任的人」（或管理员 PowerShell 执行 certutil -addstore TrustedPeople <cer文件名>），再双击 .msix 安装；Windows 11 开启「智能应用控制」会拦截自签名 MSIX，需在安全中心关闭后再装；应用内自动更新仅支持 zip 版。"
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
