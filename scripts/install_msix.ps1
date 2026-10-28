<#
.SYNOPSIS
    青枢工具箱 MSIX 一键安装助手：自动导入签名证书 → Add-AppxPackage 安装。

.DESCRIPTION
    解决 0x800B0109（已处理证书链，但是在不受信任提供程序信任的根证书中终止）：
    该错误表示 .msix 的签名证书尚未被本机信任，自签名 MSIX 必须先装 .cer 再安装。
    本脚本自动完成：导入 .cer → 本地计算机\受信任的人 → 安装 MSIX；
    若仍不受信任，自动回退导入「受信任的根证书颁发机构」后重试一次。
    导入机器存储需要管理员权限：非管理员运行时自动弹出 UAC 提权窗口。
    证书已被本机信任时（如重装新版本）不再请求提权，直接安装。

    用法（在仓库根目录或任意位置执行）：
      .\scripts\install_msix.ps1                              # 自动找仓库根目录最新 .msix
      .\scripts\install_msix.ps1 -Msix .\QingShu.Toolkit-1.2.0-beta.msix
      .\scripts\install_msix.ps1 -CertOnly                    # 只导入证书，不安装

.PARAMETER Msix
    .msix 文件路径。缺省自动在仓库根目录找最近生成的 .msix。

.PARAMETER Cer
    .cer 证书路径。缺省取与 .msix 同名的 .cer。

.PARAMETER CertOnly
    只导入证书，不安装 MSIX（供终端用户安装 Release 附带的 .cer 后自行装 .msix）。

.PARAMETER Pause
    内部参数：提权子窗口执行完后等待回车，避免窗口一闪而过看不到结果。
#>
[CmdletBinding()]
param(
    [string]$Msix = "",
    [string]$Cer = "",
    [switch]$CertOnly,
    [switch]$Pause
)

$ErrorActionPreference = 'Stop'

# ---- 1. 定位 .msix ----
if (-not $Msix) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $latest = Get-ChildItem $repoRoot -Filter *.msix -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { $Msix = $latest.FullName }
}
if (-not $Msix -or -not (Test-Path $Msix)) {
    throw "未找到 .msix：请先运行 make_release.ps1 打包，或用 -Msix 指定路径（如 -Msix .\QingShu.Toolkit-1.2.0-beta.msix）"
}
$Msix = (Resolve-Path $Msix).Path

# ---- 2. 定位 .cer（缺省取同名文件）----
if (-not $Cer) {
    $side = [System.IO.Path]::ChangeExtension($Msix, ".cer")
    if (Test-Path $side) { $Cer = $side }
}
if (-not $Cer -or -not (Test-Path $Cer)) {
    throw "未找到配套证书 .cer（$Cer）。请先运行 make_release.ps1 生成，或用 -Cer 指定路径。"
}
$Cer = (Resolve-Path $Cer).Path

$cerCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Cer)
Write-Host "MSIX ：$Msix"
Write-Host "证书 ：$Cer"
Write-Host "指纹 ：$($cerCert.Thumbprint)"

# ---- 3. 证书是否已被本机信任（已信任则重装新版无需再提权）----
$trusted = $false
foreach ($storePath in @("Cert:\LocalMachine\TrustedPeople", "Cert:\LocalMachine\Root")) {
    $found = Get-ChildItem $storePath -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cerCert.Thumbprint }
    if ($found) {
        $trusted = $true
        Write-Host "证书已在受信任存储：$storePath"
    }
}

# ---- 4. 需要导入证书且当前非管理员 → 弹 UAC 提权重跑本脚本 ----
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $trusted -and -not $isAdmin) {
    Write-Host "==> 导入证书需要管理员权限，正在弹出 UAC 提权窗口 ..." -ForegroundColor Cyan
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Msix `"$Msix`" -Cer `"$Cer`" -Pause"
    if ($CertOnly) { $argLine += " -CertOnly" }
    try {
        Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argLine
        return
    } catch {
        throw "已取消 UAC，未导入证书。可改用手动方式：管理员 PowerShell 执行 certutil -addstore TrustedPeople `"$Cer`" 后再装 .msix。"
    }
}

# ---- 5. 导入证书：本地计算机 → 受信任的人 ----
if (-not $trusted) {
    Write-Host "==> 导入证书 → 本地计算机\受信任的人 ..." -ForegroundColor Cyan
    $imported = Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    Write-Host "    已导入：$($imported.Thumbprint)"
}

# ---- 6. 安装 ----
if (-not $CertOnly) {
    # 智能应用控制（SAC，Windows 11）开启时会按云端信誉拦截自签名包，且无"仍要运行"按钮
    try {
        $sac = (Get-CimInstance -Namespace root\Microsoft\Windows\Defender -ClassName MSFT_MpComputerStatus -ErrorAction Stop).SmartAppControlState
    } catch { $sac = "" }
    if ($sac -eq "ON" -or $sac -eq "EVAL") {
        Write-Host "==> 注意：智能应用控制处于 $sac 状态，可能拦截自签名 MSIX（提示「智能应用控制已阻止」）" -ForegroundColor Yellow
        Write-Host "    关闭路径：Windows 安全中心 → 应用和浏览器控制 → 智能应用控制设置 → 关（关闭不可逆）。"
    }
    Write-Host "==> Add-AppxPackage 安装中（约十几秒）..." -ForegroundColor Cyan
    try {
        Add-AppxPackage -Path $Msix
        Write-Host "    安装成功！开始菜单搜索「青枢工具箱」即可启动。" -ForegroundColor Green
    } catch {
        $msg = $_.Exception.Message
        if ($msg -match "0x800B0109") {
            Write-Host "    「受信任的人」未通过校验，回退导入「受信任的根证书颁发机构」后重试 ..." -ForegroundColor Yellow
            Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
            try {
                Add-AppxPackage -Path $Msix
                Write-Host "    安装成功！开始菜单搜索「青枢工具箱」即可启动。" -ForegroundColor Green
            } catch {
                Write-Host "    重试仍失败：$($_.Exception.Message)" -ForegroundColor Red
                Write-Host "    若刚才弹出「智能应用控制已阻止」：这是 Windows 11 SAC 拦截自签名包，"
                Write-Host "    需在安全中心 → 应用和浏览器控制 → 智能应用控制设置关闭后再重试。"
                Write-Host "    部署日志：Get-AppPackageLog -Latest | Select -Last 20"
                throw
            }
        } elseif ($msg -match "0x80073D06") {
            throw "本机已安装更高版本（0x80073D06）。请先在「设置 → 应用 → 安装的应用」卸载旧版，或调高 csproj 的 Version 后重新打包。"
        } else {
            throw
        }
    }
}

Write-Host "完成。" -ForegroundColor Green
if ($Pause) {
    Read-Host "按 Enter 关闭窗口" | Out-Null
}
