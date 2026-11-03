param(
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Sha256,
    [Parameter(Mandatory=$true)][int]$ParentId,
    [Parameter(Mandatory=$true)][string]$Ready
)
$ErrorActionPreference = 'Stop'
$updateDirectory = [IO.Path]::GetDirectoryName($Source)
$targetDirectory = [IO.Path]::GetDirectoryName($Target)
$backup = $Target + '.previous'
$logPath = Join-Path $targetDirectory 'deskbox-update.log'
$replaced = $false
$restart = New-Object Diagnostics.ProcessStartInfo
$restart.FileName = $Target
$restart.WorkingDirectory = $targetDirectory
$restart.UseShellExecute = $false
function Get-Sha256([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
try {
    # 先获取进程句柄，避免等待期间 PID 被重用。
    $parent = [Diagnostics.Process]::GetProcessById($ParentId)
    $null = $parent.Handle
    if ((Get-Sha256 $Source) -ne $Sha256) {
        throw '更新包校验失败。'
    }
    [IO.File]::WriteAllText($Ready, 'ready')
    if (-not $parent.WaitForExit(60000)) { throw '等待 DeskBox 退出超时，未替换程序。' }
    # 再次核对下载文件；File.Replace 同时保留旧版，仅原子替换主程序。
    if ((Get-Sha256 $Source) -ne $Sha256) {
        throw '更新包在等待期间发生变化。'
    }
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            [IO.File]::Replace($Source, $Target, $backup, $true)
            $replaced = $true
            break
        } catch {
            if ($attempt -eq 59) { throw }
            [Threading.Thread]::Sleep(500)
        }
    }
    $restarted = [Diagnostics.Process]::Start($restart)
    $restarted.Dispose()
    try { [IO.File]::AppendAllText($logPath, "$(Get-Date -Format s) 更新成功，旧版备份：$backup`r`n") } catch {}
} catch {
    $failure = $_.Exception.Message
    if ($replaced -and [IO.File]::Exists($backup)) {
        try { [IO.File]::Replace($backup, $Target, (Join-Path $updateDirectory 'failed.exe'), $true) } catch { $failure += '；回退失败：' + $_.Exception.Message }
    }
    try { [IO.File]::AppendAllText($logPath, "$(Get-Date -Format s) 更新失败：$failure`r`n") } catch {}
    # 只有原进程已退出时才重新启动；超时绝不终止原进程。
    if ($null -ne $parent -and $parent.HasExited) {
        try { $restarted = [Diagnostics.Process]::Start($restart); $restarted.Dispose() } catch {}
    }
} finally {
    if ($null -ne $parent) { $parent.Dispose() }
    # 暂存目录只允许位于目标目录内，且必须是本程序生成的专用目录。
    $resolvedUpdate = [IO.Path]::GetFullPath($updateDirectory)
    $resolvedParent = [IO.Path]::GetDirectoryName($resolvedUpdate)
    if ($resolvedParent -eq [IO.Path]::GetFullPath($targetDirectory) -and
        [IO.Path]::GetFileName($resolvedUpdate) -match '^\.deskbox-update-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedUpdate -Recurse -Force -ErrorAction SilentlyContinue
    }
}
