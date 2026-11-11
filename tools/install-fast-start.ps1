# Run once with administrator rights for accounts that cannot register DeskBox's
# normal interactive logon task. The scheduled helper lives under Program Files.
[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../dist/DeskBox.exe'),
    [string]$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$current = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this setup in an elevated PowerShell session.'
}
if ($UserSid -notmatch '^S-1-5-21-(\d+-){3}\d+$') { throw 'Invalid interactive user SID.' }

$taskName = "DeskBox Fast Start $UserSid"
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$folder = $service.GetFolder('\')
$userHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($UserSid, $true)
if ($null -eq $userHive) { throw 'The interactive user must be signed in.' }
try {
    if ($Uninstall) {
        $key = $userHive.OpenSubKey('Software\DeskBox\FastStart', $true)
        if ($null -ne $key) {
            try { $key.DeleteValue('Executable', $false) } finally { $key.Dispose() }
        }
        try { $folder.DeleteTask($taskName, 0) }
        catch { if ($_.Exception.HResult -ne -2147024894) { throw } }
        return
    }

    $target = (Resolve-Path -LiteralPath $Executable).ProviderPath
    if ($target -notmatch '^[A-Za-z]:\\' -or [IO.Path]::GetExtension($target) -ine '.exe') {
        throw 'Use the local path to DeskBox.exe.'
    }
    $root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'DeskBox Fast Start'
    $directory = Join-Path $root $UserSid
    foreach ($path in @($root, $directory)) {
        [IO.Directory]::CreateDirectory($path) | Out-Null
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The installation directory must be a normal directory. $path"
        }
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetSecurityDescriptorSddlForm('D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)')
        Set-Acl -LiteralPath $path -AclObject $acl
    }

    $launcher = Join-Path $directory 'DeskBoxFastStart.exe'
    $stage = Join-Path $directory ([Guid]::NewGuid().ToString('N') + '.exe')
    $backup = $stage + '.previous'
    try {
        $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
        & $compiler /nologo /target:winexe /optimize+ "/out:$stage" (Join-Path $PSScriptRoot 'FastStartLauncher.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Fast-start launcher compilation failed.' }
        if ([IO.File]::Exists($launcher)) {
            if (([IO.File]::GetAttributes($launcher) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The launcher must be a regular file.'
            }
            [IO.File]::Replace($stage, $launcher, $backup)
        } else {
            [IO.File]::Move($stage, $launcher)
        }
        $acl = [Security.AccessControl.FileSecurity]::new()
        $acl.SetSecurityDescriptorSddlForm('D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)')
        Set-Acl -LiteralPath $launcher -AclObject $acl
    } finally {
        if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }

    $task = $service.NewTask(0)
    $task.RegistrationInfo.Description = 'Starts DeskBox at logon using the interactive desktop token.'
    $task.Principal.UserId = $UserSid
    $task.Principal.LogonType = 3
    $task.Principal.RunLevel = 1
    $task.Settings.MultipleInstances = 2
    $task.Settings.DisallowStartIfOnBatteries = $false
    $task.Settings.StopIfGoingOnBatteries = $false
    $task.Settings.ExecutionTimeLimit = 'PT1M'
    $trigger = $task.Triggers.Create(9)
    $trigger.UserId = $UserSid
    $trigger.Delay = 'PT0S'
    $action = $task.Actions.Create(0)
    $action.Path = $launcher
    $action.WorkingDirectory = $directory
    $sddl = "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$UserSid)"
    $registered = $folder.RegisterTaskDefinition($taskName, $task, 6, $UserSid, $null, 3, $sddl)

    $key = $userHive.CreateSubKey('Software\DeskBox\FastStart')
    try { $key.SetValue('Executable', $target, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
    Write-Output "Installed $($registered.Name)"
} finally {
    $userHive.Dispose()
}
