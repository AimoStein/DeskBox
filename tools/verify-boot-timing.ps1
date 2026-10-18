# 验证「登录时自启 + 盒子首帧」时间线：重启/注销重新登录后运行。
# 用法：pwsh -File tools/verify-boot-timing.ps1
# 输出：登录/桌面切换时间、DeskBox 与 Explorer 启动先后、盒子首帧时刻、登录任务状态。
$ErrorActionPreference = 'Continue'

function Format-Time([datetime]$t) { if ($t -eq [datetime]::MinValue) { '(未知)' } else { $t.ToString('HH:mm:ss.fff') } }

Write-Host '===== 1. 相关进程 ====='
Get-Process explorer, DeskBox -ErrorAction SilentlyContinue |
    Select-Object ProcessName, Id, @{ n = 'StartTime'; e = { Format-Time $_.StartTime } }, Path |
    Format-Table -AutoSize

Write-Host '===== 2. 登录时任务状态 ====='
schtasks /query /tn DeskBox /v /fo LIST 2>&1 |
    Select-String -Pattern 'TaskName|Status|Last Run Time|Last Result|Task To Run|Logon Mode|状态|上次运行时间|上次结果|要运行的任务|登录模式' |
    ForEach-Object { '  ' + $_.Line.Trim() }

Write-Host '===== 3. 旧式 Run 键（应为空） ====='
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).DeskBox
if ($null -eq $run) { Write-Host '  (无 DeskBox Run 值，符合预期)' } else { Write-Host "  $run" }

Write-Host '===== 4. 本次登录的 Shell-Core 事件（桌面切换 / Run 枚举） ====='
$desktopSwitch = [datetime]::MinValue
try {
    $events = Get-WinEvent -LogName 'Microsoft-Windows-Shell-Core/Operational' -MaxEvents 600 -ErrorAction Stop |
        Where-Object { $_.TimeCreated -gt (Get-Date).AddHours(-6) } |
        Where-Object { $_.Id -in 9705, 9707 -or $_.Message -match 'DesktopSwitch' }
    foreach ($ev in ($events | Sort-Object TimeCreated | Select-Object -First 40)) {
        $msg = ($ev.Message -replace "`r?`n", ' ')
        if ($msg.Length -gt 110) { $msg = $msg.Substring(0, 110) }
        Write-Output ('  {0} [{1}] {2}' -f (Format-Time $ev.TimeCreated), $ev.Id, $msg)
        if ($desktopSwitch -eq [datetime]::MinValue -and $ev.Id -ne 9705 -and $ev.Id -ne 9707) {
            $desktopSwitch = $ev.TimeCreated
        }
        if ($desktopSwitch -eq [datetime]::MinValue -and $ev.Id -eq 9705 -and $msg -match 'Run') {
            $desktopSwitch = $ev.TimeCreated
        }
    }
} catch {
    Write-Host ('  读取失败: ' + $_.Exception.Message)
}

Write-Host '===== 5. deskbox.log 最近一次启动时间线 ====='
$logPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\deskbox.log'
if (-not (Test-Path $logPath)) { $logPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'deskbox.log' }

if (-not (Test-Path $logPath)) {
    Write-Host "  找不到日志：$logPath"
} else {
    $lines = Get-Content $logPath
    $start = -1
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}).*\[启动\] 模块初始化') { $start = $i; break }
    }

    if ($start -lt 0) {
        Write-Host '  日志里没有 [启动] 段。'
    } else {
        $block = $lines[$start..([Math]::Min($start + 40, $lines.Count - 1))]

        foreach ($line in $block) {
            if ($line -match '\[启动\] (模块初始化|进程入口|开机上下文|盒子显示|启动完成|桌面现场)|盒子「.+?」首帧') {
                Write-Host ('  ' + $line)
            }
        }

        $firstLine = $block | Where-Object { $_ -match '\[启动\] 模块初始化' } | Select-Object -First 1
        $frameLine = $block | Where-Object { $_ -match '首帧（ContentRendered）' } | Select-Object -First 1
        if ($firstLine -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}).*自进程启动 (\d+)ms') {
            $logStamp = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
            $sinceProcess = [int]$matches[2]
            $deskBoxStart = $logStamp.AddMilliseconds(-$sinceProcess)

            Write-Host ''
            Write-Host '===== 6. 关键结论 ====='
            if ($desktopSwitch -ne [datetime]::MinValue) {
                Write-Host ('  桌面切换:            {0}' -f (Format-Time $desktopSwitch))
            }
            Write-Host ('  DeskBox 进程启动:    {0}（由日志反推）' -f (Format-Time $deskBoxStart))

            $ex = Get-Process explorer -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($ex) {
                Write-Host ('  Explorer 启动:       {0}' -f (Format-Time $ex.StartTime))
                Write-Host ('  DeskBox 比 Explorer: {0} ms（负值=更早）' -f [int]($deskBoxStart - $ex.StartTime).TotalMilliseconds)
            }

            if ($frameLine -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                $frameStamp = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss', $null)
                if ($desktopSwitch -ne [datetime]::MinValue) {
                    Write-Host ('  盒子首帧比桌面切换:  {0} ms' -f [int]($frameStamp - $desktopSwitch).TotalMilliseconds)
                }
            }
        }
    }
}
