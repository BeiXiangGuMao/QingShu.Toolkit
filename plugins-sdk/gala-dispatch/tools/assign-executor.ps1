<#
.SYNOPSIS
    元旦晚会智能分流插件 · assignment.json 本机消费端示例执行器
.DESCRIPTION
    插件客户端把主控下发的当前分配（服务器 / 路由 / 优先级 / 限速标记）落盘到
    assignment.json，供本机其它执行器二次消费。本脚本是最小可运行示例：
      1) 循环读取 assignment.json，展示当前分配；
      2) -Apply 时把「限速标记 throttleKbps」转成 Windows QoS 策略（需管理员）：
         新建/更新名为 QSTK-GalaThrottle 的 NetQosPolicy，把本机出向流量限制到该速率；
         标记为 0（解除）时自动删除策略。
    晚会现场的真实消费端（路由器 API、代理切换、专用限速工具）可参照本脚本
    的「读文件 → 差异化执行」骨架替换执行段。
.NOTES
    仅限城域网内网场景使用；本脚本只改本机 QoS 策略，不改路由。
    需 Windows 8+/Server 2012+ 内置 NetQos 模块；-Apply 需管理员运行。
#>
param(
    # assignment.json 路径；缺省 = 插件数据目录默认位置
    [string]$AssignmentPath = "",
    # 实际应用限速策略（不加 = 仅展示模式，只读不写）
    [switch]$Apply,
    # 轮询间隔（秒）
    [int]$IntervalSeconds = 5
)

$ErrorActionPreference = "Stop"
$policyName = "QSTK-GalaThrottle"

if (-not $AssignmentPath) {
    $AssignmentPath = Join-Path $env:LOCALAPPDATA "QingShu.Toolkit\plugins-data\qingshu.galadispatch\assignment.json"
}

function Show-Assignment([object]$rec) {
    $ts = $rec.updatedAt
    $srv = if ($rec.serverId) { $rec.serverId } else { "-" }
    $ip = if ($rec.serverIp) { $rec.serverIp } else { "-" }
    $rt = if ($rec.routerId) { $rec.routerId } else { "-" }
    $pri = @("高", "中", "低")[[Math]::Max(0, [Math]::Min(2, [int]$rec.priority))]
    $thr = if ([int]$rec.throttleKbps -gt 0) { "$($rec.throttleKbps) kbps" } else { "未限速" }
    Write-Host ("[{0}] 服务器 {1}（{2}）· 经 {3} · 优先级 {4} · 限速 {5}" -f $ts, $srv, $ip, $rt, $pri, $thr)
}

function Set-Throttle([int]$kbps) {
    # 幂等：先删同名旧策略再按需新建
    try {
        Get-NetQosPolicy -Name $policyName -ErrorAction SilentlyContinue |
            Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
    } catch { }
    if ($kbps -gt 0) {
        New-NetQosPolicy -Name $policyName `
            -ThrottleRateActionBitsPerSecond ($kbps * 1000) `
            -NetworkProfile All -IPProtocol Both | Out-Null
        Write-Host ("已应用限速策略 {0}：{1} kbps（本机出向）" -f $policyName, $kbps)
    } else {
        Write-Host "限速标记为 0：已解除限速策略"
    }
}

if (-not (Test-Path $AssignmentPath)) {
    Write-Host "未找到 $AssignmentPath"
    Write-Host "请先在本机运行插件客户端角色并连接主控端（分配落盘后才可消费）。"
    exit 1
}

Write-Host "开始监控 $AssignmentPath（间隔 ${IntervalSeconds}s，模式：$(if ($Apply) { '应用限速' } else { '仅展示' }）"
Write-Host "按 Ctrl+C 退出。"
$lastStamp = ""
while ($true) {
    try {
        $rec = Get-Content $AssignmentPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($rec.updatedAt -ne $lastStamp) {
            $lastStamp = $rec.updatedAt
            Show-Assignment $rec
            if ($Apply) { Set-Throttle ([int]$rec.throttleKbps) }
        }
    } catch {
        Write-Host ("读取/解析失败（{0}），下一轮重试" -f $_.Exception.Message)
    }
    Start-Sleep -Seconds ([Math]::Max(1, $IntervalSeconds))
}
