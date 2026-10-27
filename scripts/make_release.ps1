<#
.SYNOPSIS
    青枢工具箱一键发布脚本：本地构建 → 打 zip → 创建 GitHub Release → 上传资产。
    更新系统（自动检查更新）依赖仓库中存在 Release，首次使用先运行本脚本发布一次。

.DESCRIPTION
    用法（在仓库根目录的 PowerShell 中执行）：
      # 发布 Beta 版（tag 取 csproj Version，如 1.2.0-beta → v1.2.0-beta，标记为预发布）
      .\scripts\make_release.ps1 -Token <你的GitHub PAT>

      # 发布稳定版（覆盖 tag，非预发布）
      .\scripts\make_release.ps1 -Token <PAT> -Tag v1.3.0 -Stable

      # 只打包不上传（先本地检查 zip 内容）
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
    只构建并打包 zip，不创建 Release（上传前本地验收）。
#>
[CmdletBinding()]
param(
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$Tag,
    [switch]$Stable,
    [switch]$SkipBuild,
    [switch]$PackOnly
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
if ($PackOnly) { Write-Host "==> -PackOnly 模式：不创建 Release。zip 位于 $zipPath" -ForegroundColor Yellow; return }

# ---- 4. Token ----
if (-not $Token) {
    $sec = Read-Host "请输入 GitHub PAT（repo 权限，输入不回显）" -AsSecureString
    $Token = [System.Net.NetworkCredential]::new("", $sec).Password
}
$headers = @{
    Authorization = "Bearer $Token"
    Accept        = "application/vnd.github+json"
    'User-Agent'  = "QingShu.Toolkit-release-script"
}

# ---- 5. 创建 Release（已存在则复用） ----
$rel = $null
$existing = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" `
    -ErrorAction SilentlyContinue
if ($existing -and $existing.id) {
    Write-Host "==> Release $Tag 已存在（id=$($existing.id)），复用并更新资产" -ForegroundColor Yellow
    $rel = $existing
} else {
    $body = @{
        tag_name         = $Tag
        name             = "青枢工具箱 $version"
        prerelease       = [bool]$isPrerelease
        body             = "青枢工具箱 $version 发布。下载 QingShu.Toolkit-$version.zip 解压替换即可使用；应用内「设置 → 自动更新」可直接在线升级。"
    } | ConvertTo-Json
    Write-Host "==> 创建 Release $Tag ..." -ForegroundColor Cyan
    $rel = Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" `
        -Uri "https://api.github.com/repos/$Repo/releases" -Body $body
}
Write-Host "    Release id=$($rel.id)  预发布=$($rel.prerelease)" -ForegroundColor Green

# ---- 6. 上传资产（同名先删后传） ----
$assetName = Split-Path $zipPath -Leaf
foreach ($a in @($rel.assets)) {
    if ($a.name -eq $assetName) {
        Write-Host "==> 删除旧资产 $assetName" -ForegroundColor Yellow
        Invoke-RestMethod -Method Delete -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/assets/$($a.id)" | Out-Null
    }
}
Write-Host "==> 上传 $assetName（$zipSize，大文件耐心等待）..." -ForegroundColor Cyan
$upload = Invoke-RestMethod -Method Post `
    -Headers @{ Authorization = "Bearer $Token"; 'User-Agent' = "QingShu.Toolkit-release-script" } `
    -ContentType "application/zip" `
    -Uri "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$assetName" `
    -InFile $zipPath
Write-Host "==> 发布完成：" -ForegroundColor Green
Write-Host "    $($upload.browser_download_url)"

Write-Host ""
Write-Host "提示：现在可在应用内「设置 → 自动更新」点击【立即检查更新】验证；" -ForegroundColor Cyan
Write-Host "      tag 数字版本需高于用户本机版本（本机读取 csproj 程序集版本）才会提示升级。"
