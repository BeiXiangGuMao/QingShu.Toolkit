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
    跳过 MSIX 打包（需 Windows SDK 的 MakeAppx/SignTool；缺省会打包签名 MSIX 并附 .cer）。
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Tag,
    [switch]$Stable,
    [switch]$SkipBuild,
    [switch]$PackOnly,
    [switch]$NoMsix
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
# MSIX 为手动安装格式：先双击 .cer 装证书（本地计算机 → 受信任的人），再双击 .msix 安装。
# 应用内自动更新始终使用 zip 资产，与 MSIX 互不影响（MSIX 安装目录只读，勿在其内测自动更新）。
$msixPath = ""
$cerPath = ""
if (-not $NoMsix) {
    if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)') {
        throw "无法从版本号 '$version' 解析数字段；MSIX 要求 x.y.z.w 四段版本号"
    }
    $msixVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

    $kitRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kitRoot)) { throw "未找到 Windows SDK（$kitRoot），无法打包 MSIX。可安装 VS2022「Windows 应用程序开发」工作负载，或加 -NoMsix 跳过。" }
    $sdkBin = Get-ChildItem $kitRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.' } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $sdkBin) { throw "Windows SDK 目录下没有版本号子目录，无法定位 MakeAppx。可加 -NoMsix 跳过。" }
    $makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"
    $signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"
    foreach ($tool in @($makeappx, $signtool)) {
        if (-not (Test-Path $tool)) { throw "缺少工具：$tool（Windows SDK 未装全）。可加 -NoMsix 跳过 MSIX。" }
    }

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
        Write-Host "    cer ：$cerPath —— 本机验收：双击 cer 装到「本地计算机 → 受信任的人」→ 双击 msix 安装"
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
        body             = "青枢工具箱 $version 发布。zip：解压替换即可使用，应用内「设置 → 自动更新」可直接在线升级。MSIX（手动安装格式）：先双击 .cer 把证书装到「本地计算机 → 受信任的人」，再双击 .msix 安装；应用内自动更新仅支持 zip 版。"
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
