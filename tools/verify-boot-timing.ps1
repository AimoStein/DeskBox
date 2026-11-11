# 重启/注销重新登录后运行；只读取现场，不启动或重启 DeskBox。
# 可用 -LogPath 指定日志。旧日志时间戳只有秒精度，不能反推精确绝对首帧时间。
param([string]$LogPath)
$ErrorActionPreference = 'Stop'

function Format-Time([datetime]$time) { $time.ToString('yyyy-MM-dd HH:mm:ss.fff') }

$sessionId = (Get-Process -Id $PID).SessionId
$processes = @(Get-Process explorer, DeskBox -ErrorAction SilentlyContinue | Where-Object SessionId -eq $sessionId)
Write-Output '===== 1. 当前会话进程（绝对时间来自系统） ====='
$processes | Select-Object ProcessName, Id, @{ n = 'StartTime'; e = { Format-Time $_.StartTime } }, Path | Format-Table -AutoSize

Write-Output '===== 2. 登录自启现场 ====='
& (Join-Path $PSScriptRoot 'diagnose-autostart.ps1')

if (-not $LogPath) {
    $candidates = @(
        $processes | Where-Object { $_.ProcessName -eq 'DeskBox' -and $_.Path } | ForEach-Object { Join-Path (Split-Path $_.Path -Parent) 'deskbox.log' }
        Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\deskbox.log'
        Join-Path $env:APPDATA 'DeskBox\deskbox.log'
    )
    $LogPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
Write-Output '===== 3. 最近一次有盒子首帧的启动 ====='
if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath)) {
    throw '找不到 deskbox.log，请用 -LogPath 指定实际配置目录中的日志。'
}
Write-Output "日志: $LogPath"
$lines = @(Get-Content -LiteralPath $LogPath -Encoding UTF8)
# 不用最新模块入口直接定界：后续的单实例转发/自检进程也会留下入口日志。
$frameIndex = -1
for ($i = $lines.Count - 1; $i -ge 0; $i--) {
    if ($lines[$i] -match '盒子「.+?」首帧（ContentRendered）') { $frameIndex = $i; break }
}
$start = -1
for ($i = $frameIndex; $i -ge 0; $i--) {
    if ($lines[$i] -match '\[启动\] 模块初始化') { $start = $i; break }
}
if ($start -lt 0) { throw '日志中没有包含盒子首帧的完整启动段。' }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '\[启动\] 模块初始化') { $end = $i; break }
    if ($lines[$i] -match '\[启动\] 启动观测结束') { $end = $i + 1; break }
}
$block = @($lines[$start..($end - 1)])
$block | Where-Object { $_ -match '\[启动\]|\[图标\]|图标批次|注册登录任务失败|开机自启：' } | Write-Output

$frames = @($block | ForEach-Object {
    if ($_ -match '盒子「(.+?)」首帧（ContentRendered）自进程启动 (\d+)ms') {
        [pscustomobject]@{ Title = $matches[1]; Milliseconds = [long]$matches[2] }
    }
})
$expected = $null
foreach ($line in $block) {
    if ($line -match '配置读取（盒子 (\d+) 个）') { $expected = [int]$matches[1]; break }
}
Write-Output '===== 4. 本进程显示耗时（不能单独视为登录或冷启动耗时） ====='
foreach ($line in $block) {
    if ($line -match '快照图标首帧自进程启动 (\d+)ms') { Write-Output "可点击快照首帧: $($matches[1])ms" }
    if ($line -match '桌面与盒子完成显示交接，自进程启动 (\d+)ms') { Write-Output "完整界面接管: $($matches[1])ms" }
}
if ($frames.Count -gt 0) {
    $range = $frames | Measure-Object Milliseconds -Minimum -Maximum
    Write-Output "首个盒子首帧: $($range.Minimum)ms；最后一个已记录盒子首帧: $($range.Maximum)ms"
    if ($null -ne $expected) {
        Write-Output "首帧记录: $($frames.Count)/$expected"
        if ($frames.Count -ne $expected) { Write-Output '首帧记录不完整，不能据此认定全部盒子显示完成。' }
    }
}
Write-Output 'ContentRendered 表示盒子首帧；完整图标需同时核对图标批次/预热日志。'
Write-Output '日志时间戳按秒截断；本脚本不据此反推毫秒精度的进程或首帧绝对时间。'

Write-Output '===== 5. 当前 Explorer 启动后的 Shell-Core 事件 ====='
$explorer = $processes | Where-Object ProcessName -eq 'explorer' | Sort-Object StartTime | Select-Object -First 1
if (-not $explorer) { Write-Output '当前会话没有 Explorer，无法限定事件范围。'; return }
try {
    # 避免取“最近六小时第一条”误用其他会话；最多读取 10000 条，并明确截断。
    $events = @(Get-WinEvent -FilterHashtable @{
        LogName = 'Microsoft-Windows-Shell-Core/Operational'; StartTime = $explorer.StartTime
    } -MaxEvents 10000 -ErrorAction Stop)
    if ($events.Count -eq 10000) { Write-Output '事件达到读取上限，早期记录可能缺失。' }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $relevant = @($events | Where-Object {
        $_.UserId -and $_.UserId.Value -eq $userSid -and
        ($_.Id -in 9705, 9707, 9708 -or $_.Message -match 'DesktopSwitch')
    } | Sort-Object TimeCreated)
    $relevant | ForEach-Object {
        $message = $_.Message -replace '\r?\n', ' '
        Write-Output ('{0} [{1}] {2}' -f (Format-Time $_.TimeCreated), $_.Id, $message)
    }
    if ($relevant.Count -eq 0) { Write-Output '没有匹配的桌面切换/Run 事件，登录到首帧耗时未知。' }
    Write-Output '9705 是 Run/RunOnce 枚举，不代表桌面切换；Explorer 启动也不等同于登录。'
    Write-Output '需要把同次登录的 DeskBox 创建事件与对应首帧日志对齐后，再判断登录等待时间。'
} catch {
    Write-Output ('事件读取失败，登录到首帧耗时未知: ' + $_.Exception.Message)
}
