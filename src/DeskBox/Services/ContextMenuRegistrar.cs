using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// 注册资源管理器右键菜单。全部写在 HKCU 下，不需要管理员权限，卸载时可直接删除。
///
/// 注意：Windows 11 的新版右键菜单**不显示级联子菜单**（SubCommands），
/// 所以桌面入口必须是普通扁平项；「简桌」点开的是程序自己的菜单，等价于原来的子菜单。
/// </summary>
public static class ContextMenuRegistrar
{
    // 这几个键名是「简桌 / JianZhuo」时期写下的历史注册项，保持原样才能在启动时把它们清掉
    private const string DesktopMenuKey = @"Software\Classes\DesktopBackground\Shell\JianZhuo";
    private const string FileMenuKey = @"Software\Classes\*\shell\JianZhuoAddTo";
    private const string FolderMenuKey = @"Software\Classes\Directory\shell\JianZhuoAddTo";

    /// <summary>注册结构版本，改动菜单形式时递增，旧安装会自动重写。</summary>
    private const int MenuVersion = 2;

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopMenuKey);
        return key is not null;
    }

    /// <summary>注册项是否指向当前这份可执行文件（程序被挪走时要重新注册）。</summary>
    public static bool IsRegisteredForCurrentPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopMenuKey);
            if (key?.GetValue("JianZhuoPath") is string stored)
            {
                var samePath = string.Equals(stored, ExecutablePath, StringComparison.OrdinalIgnoreCase);
                var sameVersion = key.GetValue("JianZhuoMenuVersion") is int version && version == MenuVersion;
                return samePath && sameVersion;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static void Register()
    {
        try
        {
            var exe = ExecutablePath;
            var icon = exe + ",0";

            // 先清掉旧结构（早期版本用的是 SubCommands 级联菜单，Win11 顶层看不到）
            TryDeleteKey(DesktopMenuKey);

            using (var root = Registry.CurrentUser.CreateSubKey(DesktopMenuKey))
            {
                root.SetValue("MUIVerb", "DeskBox");
                root.SetValue("Icon", icon);
                root.SetValue("JianZhuoPath", exe);
                root.SetValue("JianZhuoMenuVersion", MenuVersion);

                using var command = root.CreateSubKey("command");
                command.SetValue(string.Empty, $"\"{exe}\" --action=menu");
            }

            RegisterAddToMenu(FileMenuKey, exe, icon);
            RegisterAddToMenu(FolderMenuKey, exe, icon);

            Log.Info("右键菜单已注册。");
        }
        catch (Exception ex)
        {
            Log.Warn("注册右键菜单失败: " + ex.Message);
        }
    }

    public static void Unregister()
    {
        TryDeleteKey(DesktopMenuKey);
        TryDeleteKey(FileMenuKey);
        TryDeleteKey(FolderMenuKey);
        Log.Info("右键菜单已移除。");
    }

    private static void RegisterAddToMenu(string rootPath, string exe, string icon)
    {
        using var key = Registry.CurrentUser.CreateSubKey(rootPath);
        key.SetValue("MUIVerb", "整理到盒子");
        key.SetValue("Icon", icon);

        using var command = key.CreateSubKey("command");
        command.SetValue(string.Empty, $"\"{exe}\" --action=addto --paths=\"%1\"");
    }

    private static void TryDeleteKey(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除注册表项失败 {path}: {ex.Message}");
        }
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskBox";

    /// <summary>改英文名之前的自启项名字；设置时顺手清掉，免得开机启动两次。</summary>
    private const string LegacyValueName = "JianZhuo";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);

            // 老名字留下的自启项一律清掉
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ContextMenuRegistrar.ExecutablePath}\" --startup");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置开机自启失败: " + ex.Message);
        }
    }
}
