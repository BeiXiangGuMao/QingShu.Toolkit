# plugin-sign.ps1 —— 给插件目录做官方签名（RSA-2048 + SHA-256 + PKCS#1 v1.5）
# 用法：powershell -ExecutionPolicy Bypass -File tools\plugin-sign.ps1 -PluginFolder <插件目录> [-KeyPath <私钥.xml>]
# 流程：校验目录名=id → 计算主程序集 SHA-256 写入清单 → 重写清单（UTF-8 无 BOM）→ 对清单原始字节签名 → 写 plugin.sig
param(
    [Parameter(Mandatory = $true)][string]$PluginFolder,
    [string]$KeyPath = "$env:USERPROFILE\Documents\QingShu.PluginSigning\official-private-key.xml"
)
$ErrorActionPreference = "Stop"

$manifestPath = Join-Path $PluginFolder "plugin.json"
$sigPath      = Join-Path $PluginFolder "plugin.sig"
if (-not (Test-Path $manifestPath)) { throw "缺少清单：$manifestPath" }
if (-not (Test-Path $KeyPath))      { throw "签名私钥不存在：$KeyPath（先用 tools\plugin-keygen.ps1 生成）" }

# 1) 读清单
$raw = [System.IO.File]::ReadAllText($manifestPath)
$manifest = $raw | ConvertFrom-Json
if (-not $manifest.id)           { throw "清单缺少 id" }
if (-not $manifest.mainAssembly) { throw "清单缺少 mainAssembly" }

# 2) 目录名必须与 id 一致（宿主加载时同样强制，防冒名）
$folderName = Split-Path -Leaf $PluginFolder
if ($folderName -ne $manifest.id) { throw "目录名 '$folderName' 与清单 id '$($manifest.id)' 不一致（宿主要求二者一致）" }

# 3) 主程序集 SHA-256 写入清单（签名一并锁定程序集内容，改 dll 即验签失败）
$dllPath = Join-Path $PluginFolder $manifest.mainAssembly
if (-not (Test-Path $dllPath)) { throw "主程序集缺失：$dllPath" }
$sha = (Get-FileHash -Algorithm SHA256 $dllPath).Hash.ToLowerInvariant()
$manifest | Add-Member -NotePropertyName mainAssemblySha256 -NotePropertyValue $sha -Force

# 4) 重写清单（UTF-8 无 BOM）并对原始字节签名
$json = $manifest | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)
$bytes = [System.IO.File]::ReadAllBytes($manifestPath)

$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.FromXmlString([System.IO.File]::ReadAllText($KeyPath))
    $sig = $null
    try {
        $sig = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                             [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    } catch {
        # 兼容极老 .NET Framework：字符串哈希名重载（同为 PKCS#1 v1.5）
        $sig = $rsa.SignData($bytes, "SHA256")
    }
    [System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig), $utf8NoBom)
} finally {
    $rsa.Dispose()
}

Write-Host "签名完成：$sigPath"
Write-Host "  插件            ：$($manifest.id) v$($manifest.version)"
Write-Host "  主程序集 SHA-256：$sha"
Write-Host "安装：把整个目录复制到工具箱 plugins\ 目录（目录名 = id），在「插件管理」页点「重新扫描」。"
Write-Host "注意：改任何清单字段或更换 dll 后必须重新签名，否则宿主验签失败。"
