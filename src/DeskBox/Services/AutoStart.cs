using System.IO;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// 开机自启入口。
///
/// 现在优先注册**任务计划程序的「登录时」任务**，而不是 HKCU Run。
/// 原因（本机实测，Shell-Core/Operational 日志）：
/// Windows 10/11 会把 HKCU Run 里的启动项排队延后执行 —— 桌面已经出现后 14s 才开始枚举 Run，
/// DeskBox 作为第 11 项在登录后约 37s 才被创建，用户在桌面里要等半分钟才看到盒子。
/// 任务计划程序的登录触发器由 Task Scheduler 直接触发，不经过这条队列，盒子能在进入桌面时就位。
///
/// 创建不了任务时（受限账户 / 组策略）自动退回 HKCU Run：功能照旧，只是会被系统延后启动。
/// 关闭自启会同时删掉任务和 Run 值；旧版本（「简桌 / JianZhuo」）留下的 Run 值也会顺手清掉。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskBox";

    /// <summary>改英文名之前的自启项名字；设置时顺手清掉，免得开机启动两次。</summary>
    private const string LegacyValueName = "JianZhuo";

    /// <summary>任务计划程序里的任务名（根目录下）。</summary>
    internal const string TaskName = "DeskBox";

    /// <summary>
    /// 任务描述里带版本号：升级后第一次启动会重新注册任务（顺便更新 exe 路径与重启策略）。
    /// </summary>
    private static string TaskDescription =>
        $"DeskBox {typeof(AutoStart).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}：登录时启动桌面盒子。" +
        "由 DeskBox 自己注册；在设置里关闭「开机自启」会删除本任务。";

    // Task Scheduler COM 常量
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int CreateOrUpdate = 6;
    private const int LogonInteractiveToken = 3;
    private const int RunLevelLeastPrivilege = 0;
    private const int InstancesIgnoreNew = 2;

    /// <summary>当前是否启用开机自启（任务或 Run 值任一存在即算启用）。</summary>
    public static bool IsEnabled => IsTaskRegistered || IsRunKeyEnabled;

    /// <summary>当前是否已经由任务计划程序承载（设置界面提示用）。</summary>
    public static bool IsTaskRegistered
    {
        get
        {
            try
            {
                dynamic service = CreateService()!;
                dynamic root = service.GetFolder("\\");
                dynamic task = root.GetTask(TaskName);
                return task is not null && task.Enabled;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>查询自启方式，写给日志/设置界面：Task / Run / Off。</summary>
    public static string Mode
    {
        get
        {
            if (IsTaskRegistered)
            {
                return "Task";
            }

            return IsRunKeyEnabled ? "Run" : "Off";
        }
    }

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }

    /// <summary>启动时后台同步一次：开关状态没变就不做任何事，变过则迁移/清理。</summary>
    public static void EnsureCurrent(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (IsEnabled)
                {
                    Disable();
                }

                return;
            }

            if (MatchesCurrentExe())
            {
                // 任务已经是指向当前 exe 的登录任务：顺手清掉可能残留的旧 Run 值
                RemoveRunValue();
                return;
            }

            Enable();
        }
        catch (Exception ex)
        {
            Log.Warn("同步开机自启失败: " + ex.Message);
        }
    }

    private static void Enable()
    {
        var exe = ContextMenuRegistrar.ExecutablePath;

        if (TryRegisterLogonTask(exe))
        {
            RemoveRunValue();
            Log.Info($"开机自启：已注册任务计划程序登录任务（{exe}）");
            return;
        }

        SetRunValue(exe);
        Log.Warn("开机自启：任务计划程序注册失败，已退回 HKCU Run（开机时可能被系统延后启动）");
    }

    private static void Disable()
    {
        TryDeleteLogonTask();
        RemoveRunValue();
        Log.Info("开机自启：已关闭（任务计划程序任务与 Run 值均已清理）");
    }

    /// <summary>任务是否存在、指向当前 exe、带 --startup 参数。</summary>
    private static bool MatchesCurrentExe()
    {
        try
        {
            dynamic service = CreateService()!;
            dynamic root = service.GetFolder("\\");
            object? registered = root.GetTask(TaskName);
            if (registered is null)
            {
                return false;
            }

            dynamic task = registered;
            if (!task.Enabled)
            {
                return false;
            }

            dynamic actions = task.Definition.Actions;
            if (actions.Count < 1)
            {
                return false;
            }

            dynamic action = actions.Item(1);
            var path = (string?)action.Path ?? string.Empty;
            var arguments = (string?)action.Arguments ?? string.Empty;
            var description = (string?)task.Definition.RegistrationInfo.Description ?? string.Empty;

            return string.Equals(
                       Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                       Path.GetFullPath(ContextMenuRegistrar.ExecutablePath).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase) &&
                   arguments.Contains("--startup", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(description, TaskDescription, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRegisterLogonTask(string exe)
    {
        try
        {
            dynamic service = CreateService()!;
            dynamic root = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            task.RegistrationInfo.Description = TaskDescription;
            task.RegistrationInfo.Author = "DeskBox";

            task.Settings.Enabled = true;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.RunOnlyIfIdle = false;
            task.Settings.StartWhenAvailable = false;
            task.Settings.Hidden = false;
            task.Settings.MultipleInstances = InstancesIgnoreNew;
            task.Settings.IdleSettings.StopOnIdleEnd = false;

            // 登录任务可能比桌面合成 / 显卡驱动更早启动：万一首启失败，让任务计划程序稍后重试两次。
            task.Settings.RestartInterval = "PT1M";
            task.Settings.RestartCount = 2;

            dynamic trigger = task.Triggers.Create(TriggerLogon);
            trigger.UserId = $"{Environment.UserDomainName}\\{Environment.UserName}";
            trigger.Delay = "PT0S";

            dynamic action = task.Actions.Create(ActionExec);
            action.Path = exe;
            action.Arguments = "--startup";
            action.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;

            dynamic principal = task.Principal;
            principal.LogonType = LogonInteractiveToken;
            principal.RunLevel = RunLevelLeastPrivilege;

            root.RegisterTaskDefinition(TaskName, task, CreateOrUpdate, null, null, LogonInteractiveToken);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("注册登录任务失败: " + ex.Message);
            return false;
        }
    }

    private static void TryDeleteLogonTask()
    {
        try
        {
            dynamic service = CreateService()!;
            dynamic root = service.GetFolder("\\");
            root.DeleteTask(TaskName, 0);
        }
        catch
        {
            // 任务本来就不存在
        }
    }

    private static dynamic CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException("找不到任务计划程序 COM 服务。");

        dynamic service = Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("无法创建任务计划程序服务对象。");
        service.Connect();
        return service;
    }

    private static bool IsRunKeyEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void SetRunValue(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            // 老名字留下的自启项一律清掉
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.SetValue(ValueName, $"\"{exe}\" --startup");
        }
        catch (Exception ex)
        {
            Log.Warn("写入 HKCU Run 自启失败: " + ex.Message);
        }
    }

    private static void RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                return;
            }

            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("清理 HKCU Run 自启失败: " + ex.Message);
        }
    }
}
