# 默认只读；-ProbeRegistration 用禁用的临时任务复现注册权限，结束后删除。
# 不修改 DeskBox 自启项，不启动程序，不请求提权。
param([switch]$ProbeRegistration)
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$builtinAdministrator = ($identity.User.Value -match '^S-1-5-21-\d+-\d+-\d+-500$') -and
    $identity.Name.StartsWith($env:COMPUTERNAME + '\', [StringComparison]::OrdinalIgnoreCase)
Write-Output "用户: $($identity.Name)"
Write-Output "SID: $($identity.User.Value)"
Write-Output "已提升管理员权限: $elevated；内置 Administrator: $builtinAdministrator"
if ($builtinAdministrator -and -not $elevated) {
    Write-Output '权限限制：当前未提权进程不能为内置 Administrator 注册任务；RunLevel=0 也不能解除此限制。'
    Write-Output '参考：https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks'
}

$runKey = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
Write-Output "HKCU Run DeskBox: $($runKey.DeskBox)"
Write-Output "HKCU Run JianZhuo: $($runKey.JianZhuo)"
Write-Output "Schedule 服务: $((Get-Service Schedule).Status)"

$stage = '连接任务服务'
try {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    $stage = '打开根目录'
    $folder = $service.GetFolder('\')
    Write-Output '任务服务连接/根目录访问: 成功'
    try {
        $fastTask = $folder.GetTask("DeskBox Fast Start $($identity.User.Value)")
        $fastKey = Get-ItemProperty 'HKCU:\Software\DeskBox\FastStart' -ErrorAction SilentlyContinue
        Write-Output "DeskBox 快速登录任务: Enabled=$($fastTask.Enabled)，LastRunTime=$($fastTask.LastRunTime)，LastTaskResult=$($fastTask.LastTaskResult)"
        Write-Output "DeskBox 快速启动目标: $($fastKey.Executable)"
    } catch {
        Write-Output ('DeskBox 快速登录任务查询: ' + $_.Exception.Message)
    }
    try {
        $task = $folder.GetTask('DeskBox')
        Write-Output "DeskBox 任务: Enabled=$($task.Enabled)，LastTaskResult=$($task.LastTaskResult)"
        Write-Output $task.Xml
    } catch {
        Write-Output ('DeskBox 任务查询: ' + $_.Exception.Message)
    }
} catch {
    Write-Output ("${stage}: " + $_.Exception.Message)
    exit 1
}

if (-not $ProbeRegistration) { return }

foreach ($variant in @('original', 'sid', 'explicit-principal', 'no-trigger', 'compatibility-1')) {
    # TASK_CREATE 防止覆盖已有任务；每个变体都使用独立名字。
    $probeName = 'DeskBox-Diagnostic-' + [guid]::NewGuid().ToString('N')
    $created = $false
    $stage = '构建任务'
    try {
        $definition = $service.NewTask(0)
        $definition.RegistrationInfo.Description = 'DeskBox 注册权限诊断；禁用且不执行，诊断结束即删除。'
        $definition.Settings.Enabled = $false
        $definition.Principal.LogonType = 3
        $definition.Principal.RunLevel = 0
        if ($variant -eq 'compatibility-1') { $definition.Settings.Compatibility = 1 }
        if ($variant -notin @('no-trigger', 'compatibility-1')) {
            $trigger = $definition.Triggers.Create(9)
            $trigger.UserId = if ($variant -eq 'original') { $identity.Name } else { $identity.User.Value }
        }
        $userId = $null
        if ($variant -eq 'explicit-principal') {
            $userId = $identity.User.Value
            $definition.Principal.UserId = $userId
        }
        $action = $definition.Actions.Create(0)
        $action.Path = Join-Path $env:WINDIR 'System32\cmd.exe'
        $action.Arguments = '/c exit 0'
        $stage = 'RegisterTaskDefinition'
        $registered = $folder.RegisterTaskDefinition($probeName, $definition, 2, $userId, $null, 3)
        $created = $true
        if ($registered.Enabled) { throw '诊断任务应保持禁用。' }
        Write-Output "${variant}: 注册成功（任务禁用，未执行）"
    } catch {
        Write-Output ("${variant}: ${stage}: HRESULT=0x{0:X8} {1}" -f $_.Exception.HResult, $_.Exception.Message)
    } finally {
        if ($created) {
            # 只删除本轮成功创建的 GUID 任务，绝不触碰正式 DeskBox 任务。
            $folder.DeleteTask($probeName, 0)
            Write-Output "${variant}: 临时任务已删除"
        }
    }
}
