# 在隔离目录测试 Windows PowerShell 助手，绝不使用正在运行的 DeskBox。
$ErrorActionPreference = 'Stop'
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('DeskBox-Updater-' + [Guid]::NewGuid().ToString('N'))
$helperSource = Join-Path $PSScriptRoot '../src/DeskBox/Services/InstallUpdate.ps1'
$powershell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
New-Item -ItemType Directory -Path $sandbox | Out-Null
function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    Write-Output "PASS $message"
}
function Start-Hidden($arguments, $inputPipe = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new($powershell)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardInput = $inputPipe
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}
try {
    foreach ($label in @('old', 'new')) {
        $sourceFile = Join-Path $sandbox "$label.cs"
        $code = 'using System; using System.IO; class Entry { static void Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "started.txt"), "' + $label + '"); } }'
        [IO.File]::WriteAllText($sourceFile, $code)
        & $compiler /nologo /target:winexe "/out:$sandbox/$label.exe" $sourceFile
        if ($LASTEXITCODE -ne 0) { throw 'Probe compilation failed' }
    }
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $sandbox 'old.exe')).Hash
    $newHash = (Get-FileHash -LiteralPath (Join-Path $sandbox 'new.exe')).Hash
    foreach ($scenario in @('success', 'bad-hash', 'rollback', 'locked-target')) {
        # 空格、中文及 shell 特殊字符验证所有路径均按字面值处理。
        $caseDirectory = Join-Path $sandbox ($scenario + ' 中文 $() quote''')
        $stage = Join-Path $caseDirectory ('.deskbox-update-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        $target = Join-Path $caseDirectory 'DeskBox.exe'
        $source = Join-Path $stage 'DeskBox.exe'
        Copy-Item -LiteralPath (Join-Path $sandbox 'old.exe') -Destination $target
        Copy-Item -LiteralPath (Join-Path $sandbox 'new.exe') -Destination $source
        $config = Join-Path $caseDirectory 'config.json'
        [IO.File]::WriteAllText($config, '{"sentinel":true}')
        if ($scenario -eq 'rollback') { [IO.File]::WriteAllText($source, 'not an executable') }
        $hash = (Get-FileHash -LiteralPath $source).Hash
        if ($scenario -eq 'bad-hash') { $hash = '0' * 64 }
        $script = Join-Path $stage 'install.ps1'
        [IO.File]::WriteAllText($script, [IO.File]::ReadAllText($helperSource), [Text.UTF8Encoding]::new($true))
        $ready = Join-Path $stage 'ready'
        $parent = Start-Hidden @('-NoProfile', '-NonInteractive', '-Command', '[Console]::ReadLine() | Out-Null') $true
        $helper = $null
        $lockStream = $null
        try {
            $helper = Start-Hidden @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $script,
                '-Target', $target, '-Source', $source, '-Sha256', $hash, '-ParentId', $parent.Id.ToString(), '-Ready', $ready)
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (-not [IO.File]::Exists($ready) -and -not $helper.HasExited -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 50
            }
            Assert ((Get-FileHash -LiteralPath $target).Hash -eq $oldHash) "$scenario leaves running target intact"
            if ($scenario -eq 'bad-hash') {
                Assert ($helper.WaitForExit(5000) -and -not [IO.File]::Exists($ready)) 'bad hash rejected before application exit'
            } else {
                Assert ([IO.File]::Exists($ready)) "$scenario helper readiness handshake"
                if ($scenario -eq 'locked-target') { $lockStream = [IO.File]::Open($target, 'Open', 'Read', 'None') }
                $parent.StandardInput.WriteLine('exit')
                Assert ($parent.WaitForExit(5000)) "$scenario parent exits normally"
                if ($lockStream) {
                    Start-Sleep -Milliseconds 800
                    Assert (-not $helper.HasExited) 'locked target is retried'
                    $lockStream.Dispose()
                    $lockStream = $null
                }
                Assert ($helper.WaitForExit(15000)) "$scenario helper completes"
                $expectedHash = if ($scenario -eq 'rollback') { $oldHash } else { $newHash }
                Assert ((Get-FileHash -LiteralPath $target).Hash -eq $expectedHash) "$scenario final executable verified"
                $marker = Join-Path $caseDirectory 'started.txt'
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                while (-not [IO.File]::Exists($marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
                $expectedMarker = if ($scenario -eq 'rollback') { 'old' } else { 'new' }
                Assert ([IO.File]::ReadAllText($marker) -eq $expectedMarker) "$scenario restarts expected executable"
                if ($scenario -ne 'rollback') {
                    Assert ((Get-FileHash -LiteralPath ($target + '.previous')).Hash -eq $oldHash) "$scenario preserves previous version"
                }
            }
            Assert ([IO.File]::ReadAllText($config) -eq '{"sentinel":true}') "$scenario preserves configuration"
            Assert (-not [IO.Directory]::Exists($stage)) "$scenario cleans staging directory"
        } finally {
            if ($lockStream) { $lockStream.Dispose() }
            if (-not $parent.HasExited) { $parent.StandardInput.WriteLine('exit'); $null = $parent.WaitForExit(5000) }
            $parent.Dispose()
            if ($helper) { if (-not $helper.HasExited) { $helper.Kill(); $helper.WaitForExit() }; $helper.Dispose() }
        }
    }
} catch {
    Get-ChildItem -LiteralPath $sandbox -Filter '*.log' -Recurse | ForEach-Object { Get-Content -LiteralPath $_.FullName }
    throw
} finally {
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    if ([IO.Path]::GetDirectoryName($resolvedSandbox) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($resolvedSandbox) -match '^DeskBox-Updater-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force
    }
}
