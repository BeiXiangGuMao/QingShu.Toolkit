<#
.SYNOPSIS
    将新的官方插件签名公钥安全写入宿主源码。

.DESCRIPTION
    校验公钥 XML 后备份宿主源文件，再替换 OfficialPublicKeyXml 常量并打印指纹。
    运行前先使用 plugin-keygen.ps1 生成密钥；私钥必须离线保管且不得入库。
    换钥后必须重新发布宿主，并使用新私钥重签所有插件。

.PARAMETER PublicKeyXml
    plugin-keygen.ps1 生成的公钥 XML 路径。

.PARAMETER HostSource
    宿主 PluginSigning.cs 路径；缺省指向当前仓库源码。

.EXAMPLE
    .\tools\plugin-rotate-publickey.ps1

.EXAMPLE
    .\tools\plugin-rotate-publickey.ps1 -PublicKeyXml 'D:\Secure\official-public-key.xml'
#>
param(
    [string]$PublicKeyXml = "$env:USERPROFILE\Documents\QingShu.PluginSigning\official-public-key.xml",
    [string]$HostSource = "$PSScriptRoot\..\src\QingShu.Toolkit\Services\Plugins\PluginSigning.cs"
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $PublicKeyXml)) { throw "公钥文件不存在：$PublicKeyXml" }
$HostSource = (Resolve-Path $HostSource).Path
if (-not (Test-Path $HostSource)) { throw "宿主源文件不存在：$HostSource" }

$pubXml = [System.IO.File]::ReadAllText($PublicKeyXml).Trim()
if (-not $pubXml.StartsWith("<RSAKeyValue>")) {
    throw "公钥文件不是 RSA XML 格式（应为 plugin-keygen.ps1 生成的 official-public-key.xml）"
}

# 写入前先验证公钥可被 .NET 解析（防手改坏文件）
$probe = [System.Security.Cryptography.RSA]::Create()
try { $probe.FromXmlString($pubXml) | Out-Null } finally { $probe.Dispose() }

# 指纹：模数 SHA-256 前 8 字节（与 plugin-keygen.ps1 / 插件管理页同算法）
$doc = New-Object System.Xml.XmlDocument
$doc.LoadXml($pubXml)
$mod = [Convert]::FromBase64String($doc["RSAKeyValue"]["Modulus"].InnerText)
$shaAlg = [System.Security.Cryptography.SHA256]::Create()
try { $sha = $shaAlg.ComputeHash($mod) } finally { $shaAlg.Dispose() }
$fpHex = -join ($sha[0..7] | ForEach-Object { $_.ToString("X2") })
$fp = "{0}:{1}:{2}:{3}" -f $fpHex.Substring(0, 4), $fpHex.Substring(4, 4), $fpHex.Substring(8, 4), $fpHex.Substring(12, 4)

$text = [System.IO.File]::ReadAllText($HostSource)
$rx = [regex]'(private const string OfficialPublicKeyXml\s*=\s*")([^"]*)(")'
if (-not $rx.IsMatch($text)) {
    throw "未在宿主源文件中找到 OfficialPublicKeyXml 常量（宿主代码结构可能已变更，请手工替换该常量）"
}

$backup = "$HostSource.bak"
Copy-Item -Force $HostSource $backup

# RSA XML 仅含 base64 字符与固定标签，不含正则替换特殊字符 $，可安全拼接
$newText = $rx.Replace($text, { param($m) $m.Groups[1].Value + $pubXml + $m.Groups[3].Value }, 1)
$utf8 = New-Object System.Text.UTF8Encoding($false)   # 与仓库 .cs 编码一致（UTF-8 无 BOM）
[System.IO.File]::WriteAllText($HostSource, $newText, $utf8)

Write-Host "官方公钥已替换进 $HostSource"
Write-Host "  新公钥指纹：$fp（与 plugin-keygen.ps1 输出一致即成功）"
Write-Host "  原文件备份：$backup"
Write-Host ""
Write-Host "后续动作（缺一不可）："
Write-Host "  1) 重新发布宿主：dotnet publish src/QingShu.Toolkit -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true"
Write-Host "  2) 用配套私钥对全部插件重新签名：powershell -ExecutionPolicy Bypass -File tools\plugin-sign.ps1 -PluginFolder <插件目录>"
Write-Host "  3) 旧公钥签名的旧插件全部失效，需用新私钥重签后才能加载"
