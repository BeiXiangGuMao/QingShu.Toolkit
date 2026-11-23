# 青枢工具箱 · 崩溃转储采集开关（WER LocalDumps）
# =====================================================================
# 背景：切换插件闪退属原生 stowed-exception 类（0xC000027B，XAML 布局/合成
#       回调期 fail-fast），托管 UnhandledException 捕获不到、应用日志拿不到
#       证据。本脚本一次性注册 Windows 错误报告（WER）本地转储，应用闪退时
#       自动落 .dmp 到指定目录，把最新的 .dmp 交给开发侧即可定位根因。
# 用法：管理员 PowerShell 执行
#       .\enable_crash_dumps.ps1                          # 默认全量 dump
#       .\enable_crash_dumps.ps1 -Mini                    # 小型 dump（体积小）
#       .\enable_crash_dumps.ps1 -DumpFolder "D:\dumps"   # 自定义目录
#       .\enable_crash_dumps.ps1 -Remove                  # 取消注册
# =====================================================================
param(
    [string]$DumpFolder = "$env:LOCALAPPDATA\QingShu.Toolkit\crashdumps",
    [switch]$Mini,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$exeName = 'QingShu.Toolkit.exe'
$key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$exeName"

# LocalDumps 注册在 HKLM，必须管理员
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "需要管理员权限：LocalDumps 注册在 HKLM，请以管理员身份重新运行。"
}

if ($Remove) {
    if (Test-Path $key) { Remove-Item -Path $key -Recurse -Force; Write-Host "已取消 $exeName 的本地转储注册。" }
    else { Write-Host "$exeName 本来就没有注册本地转储。" }
    return
}

New-Item -ItemType Directory -Force -Path $DumpFolder | Out-Null
New-Item -Path $key -Force | Out-Null
Set-ItemProperty -Path $key -Name DumpFolder -Value $DumpFolder -Type ExpandString
Set-ItemProperty -Path $key -Name DumpType -Value $(if ($Mini) { 1 } else { 2 }) -Type DWord
Set-ItemProperty -Path $key -Name DumpCount -Value 5 -Type DWord

Write-Host "已注册 WER 本地转储：$exeName"
Write-Host "  转储目录：$DumpFolder（$(if ($Mini) { '小型' } else { '全量' }) dump，保留最近 5 份）"
Write-Host "复现闪退后，把目录里最新的 .dmp 文件交给开发侧定位根因。"
