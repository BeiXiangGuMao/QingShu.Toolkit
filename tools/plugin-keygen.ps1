# plugin-keygen.ps1 —— 生成青枢工具箱官方插件签名密钥对（RSA-2048）
# 用法：powershell -ExecutionPolicy Bypass -File tools\plugin-keygen.ps1 [-OutDir <目录>]
# 私钥 = 官方身份，务必离线保管（U盘保险柜），严禁入仓库 / 严禁分发给任何人。
param(
    [string]$OutDir = "$env:USERPROFILE\Documents\QingShu.PluginSigning"
)
$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$privPath = Join-Path $OutDir "official-private-key.xml"
$pubPath  = Join-Path $OutDir "official-public-key.xml"
if (Test-Path $privPath) { throw "私钥已存在，拒绝覆盖（换钥请先备份旧钥并同步更新宿主公钥常量）：$privPath" }

$rsa = [System.Security.Cryptography.RSA]::Create(2048)
try {
    $privXml = $rsa.ToXmlString($true)   # 含私参
    $pubXml  = $rsa.ToXmlString($false)  # 仅公参
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($privPath, $privXml, $utf8)
    [System.IO.File]::WriteAllText($pubPath, $pubXml, $utf8)

    # 指纹：模数 SHA-256 前 8 字节（与插件管理页展示的官方指纹同算法）
    # 注意 Windows PowerShell 5.1 = .NET Framework：SHA256.HashData（.NET 5+）不可用，走 Create().ComputeHash
    $mod = $rsa.ExportParameters($false).Modulus
    $shaAlg = [System.Security.Cryptography.SHA256]::Create()
    try { $sha = $shaAlg.ComputeHash($mod) } finally { $shaAlg.Dispose() }
    $fpHex = -join ($sha[0..7] | ForEach-Object { $_.ToString("X2") })
    $fp = "{0}:{1}:{2}:{3}" -f $fpHex.Substring(0, 4), $fpHex.Substring(4, 4), $fpHex.Substring(8, 4), $fpHex.Substring(12, 4)

    Write-Host "密钥对已生成："
    Write-Host "  私钥（离线保管）：$privPath"
    Write-Host "  公钥（备份用）  ：$pubPath"
    Write-Host "  公钥指纹        ：$fp"
    Write-Host ""
    Write-Host "换钥提醒：把新公钥 XML 替换进 src/QingShu.Toolkit/Services/Plugins/PluginSigning.cs"
    Write-Host "的 OfficialPublicKeyXml 常量并重新发布宿主；旧签名插件需全部重签。"
} finally {
    $rsa.Dispose()
}
