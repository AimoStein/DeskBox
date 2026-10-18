# 启动时间探针：启动 DeskBox.exe，轮询它的盒子窗口首次可见的时间。
# 用法：pwsh -File tools/probe-startup.ps1 -Exe "C:\path\DeskBox.exe"
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string[]]$Titles = @('游戏', '文件夹', '工具', '常用', '工作', '学习'),
    [int]$TimeoutMs = 20000,
    [switch]$KeepRunning,
    [string]$Args = '--startup'
)

$ErrorActionPreference = 'Stop'

Add-Type @"
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public class StartProbe {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    public static List<string> VisibleTitles(uint processId) {
        var result = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != processId || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, 512);
            if (sb.Length > 0) result.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
"@

[StartProbe]::SetProcessDPIAware() | Out-Null

$process = Start-Process -FilePath $Exe -ArgumentList $Args -PassThru
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$seen = [ordered]@{}
$processId = [uint32]$process.Id

while ($watch.ElapsedMilliseconds -lt $TimeoutMs) {
    foreach ($title in [StartProbe]::VisibleTitles($processId)) {
        if ($Titles -contains $title -and -not $seen.Contains($title)) {
            $seen[$title] = $watch.ElapsedMilliseconds
        }
    }

    if ($seen.Count -ge $Titles.Count) { break }
    Start-Sleep -Milliseconds 8
}

$watch.Stop()

Write-Output "pid=$processId 总耗时=$($watch.ElapsedMilliseconds)ms 已出现=$($seen.Count)/$($Titles.Count)"
foreach ($pair in $seen.GetEnumerator()) {
    Write-Output ("  {0} = {1}ms" -f $pair.Key, $pair.Value)
}

if (-not $KeepRunning) {
    Start-Sleep -Milliseconds 800   # 留时间让日志落盘
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
}
