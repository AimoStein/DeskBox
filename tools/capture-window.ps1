# 抓取指定窗口标题的屏幕区域，用于验证渲染效果。
param(
    [string]$Title = '新盒子',
    [string]$Out = 'shot.png',
    [int]$Margin = 40,
    [switch]$Activate,
    # 直接截屏幕区域，形如 "3320,250,520,520"
    [string]$Region = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Cap {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

[Cap]::SetProcessDPIAware() | Out-Null

if ($Region) {
    $parts = $Region.Split(',') | ForEach-Object { [int]$_ }
    $x = $parts[0]; $y = $parts[1]; $w = $parts[2]; $h = $parts[3]

    $screenDc = [Cap]::GetDC([IntPtr]::Zero)
    $memDc = [Cap]::CreateCompatibleDC($screenDc)
    $bitmap = [Cap]::CreateCompatibleBitmap($screenDc, $w, $h)
    $old = [Cap]::SelectObject($memDc, $bitmap)
    [Cap]::BitBlt($memDc, 0, 0, $w, $h, $screenDc, $x, $y, 0x00CC0020) | Out-Null

    $image = [System.Drawing.Image]::FromHbitmap($bitmap)
    $image.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $image.Dispose()
    [Cap]::SelectObject($memDc, $old) | Out-Null
    [Cap]::DeleteObject($bitmap) | Out-Null
    [Cap]::DeleteDC($memDc) | Out-Null
    [Cap]::ReleaseDC([IntPtr]::Zero, $screenDc) | Out-Null

    Write-Output "已保存 $Out（区域 $x,$y ${w}x${h}）"
    return
}

$script:found = [IntPtr]::Zero
$cb = [Cap+EnumProc]{
    param($h, $l)
    if (-not [Cap]::IsWindowVisible($h)) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [Cap]::GetWindowText($h, $sb, 256) | Out-Null
    if ($sb.ToString() -eq $Title) { $script:found = $h; return $false }
    return $true
}
[Cap]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if ($script:found -eq [IntPtr]::Zero) { throw "没找到可见窗口：$Title" }

if ($Activate) {
    [Cap]::ShowWindow($script:found, 9) | Out-Null   # SW_RESTORE
    [Cap]::BringWindowToTop($script:found) | Out-Null
    [Cap]::SetForegroundWindow($script:found) | Out-Null
    Start-Sleep -Milliseconds 600
}

$rect = New-Object Cap+RECT
[Cap]::GetWindowRect($script:found, [ref]$rect) | Out-Null

$x = $rect.L - $Margin
$y = $rect.T - $Margin
$w = ($rect.R - $rect.L) + $Margin * 2
$h = ($rect.B - $rect.T) + $Margin * 2

$screenDc = [Cap]::GetDC([IntPtr]::Zero)
$memDc = [Cap]::CreateCompatibleDC($screenDc)
$bitmap = [Cap]::CreateCompatibleBitmap($screenDc, $w, $h)
$old = [Cap]::SelectObject($memDc, $bitmap)

[Cap]::BitBlt($memDc, 0, 0, $w, $h, $screenDc, $x, $y, 0x00CC0020) | Out-Null

$image = [System.Drawing.Image]::FromHbitmap($bitmap)
$image.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$image.Dispose()

[Cap]::SelectObject($memDc, $old) | Out-Null
[Cap]::DeleteObject($bitmap) | Out-Null
[Cap]::DeleteDC($memDc) | Out-Null
[Cap]::ReleaseDC([IntPtr]::Zero, $screenDc) | Out-Null

Write-Output "已保存 $Out（窗口 $($rect.L),$($rect.T) $($rect.R-$rect.L)x$($rect.B-$rect.T)，含边距 $w x $h）"
