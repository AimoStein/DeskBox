using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _loading;
    private string _section = "icons";

    public SettingsWindow(AppConfig config)
    {
        _config = config;

        // XAML 解析期间（Minimum/Maximum 赋值）就会触发 ValueChanged，
        // 这时其它命名控件还没构造出来，必须先把标志立起来。
        _loading = true;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            LoadValues();
            SelectSection(_section);
        };
    }

    /// <summary>任何设置变化后触发，由 App 负责保存与即时生效。</summary>
    public event EventHandler? Applied;

    /// <summary>需要 App 处理的动作，比如打开目录、撤销整理。</summary>
    public event Action<string>? CommandRequested;

    public void RefreshState()
    {
        LoadValues();
    }

    /// <summary>被前台锁挡住时也要能出现在用户面前。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Activate();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Interop.NativeMethods.SetForegroundWindow(handle);
        }
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            ChkAutoStart.IsChecked = AutoStart.IsEnabled;
            ChkAutoCheckUpdates.IsChecked = _config.AutoCheckForUpdates;
            ChkDoubleClick.IsChecked = _config.HideIconsOnDoubleClick;
            ChkHideGroups.IsChecked = _config.HideBoxesWithIcons;
            ChkHideTaskbar.IsChecked = _config.HideTaskbarWithIcons;
            ChkRestoreIcons.IsChecked = _config.RestoreIconsOnExit;
            ChkHidden.IsChecked = _config.ShowHiddenFiles;
            ChkShortcuts.IsChecked = _config.OrganizeShortcuts;
            ChkAlign.IsChecked = _config.AutoAlignBoxes;
            ChkAvoidOverlap.IsChecked = _config.AvoidBoxOverlap;
            ChkGlass.IsChecked = _config.LiquidGlassEnabled;
            SlGlassBlur.Value = _config.GlassBlur;
            SlGlassRefraction.Value = _config.GlassRefraction;
            LblGlassBlur.Text = $"{_config.GlassBlur:0.0}";
            LblGlassRefraction.Text = $"{_config.GlassRefraction:0.0}";

            PillLight.IsChecked = _config.Theme == ThemeMode.Light;
            PillDark.IsChecked = _config.Theme == ThemeMode.Dark;
            PillSystem.IsChecked = _config.Theme == ThemeMode.System;

            SlOpacity.Value = Math.Round(_config.BoxOpacity * 100);
            SlIcon.Value = _config.IconSize;
            SlRadius.Value = _config.CornerRadius;

            LblOpacity.Text = $"{SlOpacity.Value:0}%";
            LblIcon.Text = $"{SlIcon.Value:0} px";
            LblRadius.Text = $"{SlRadius.Value:0} px";

            var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            LblVersion.Text = $"版本 {version.ToString(3)}";
            LblSidebarVersion.Text = $"简桌 · {version.ToString(3)}";

            LblLastOrganize.Text = string.IsNullOrEmpty(_config.LastOrganizeTime)
                ? "还没有整理记录。"
                : $"上次整理：{_config.LastOrganizeTime}，共 {_config.LastOrganize.Count} 项。";

            BtnUndo.IsEnabled = _config.LastOrganize.Count > 0;
            LblHint.Text = _config.HideIconsOnDoubleClick
                ? (_config.HideTaskbarWithIcons
                    ? "提示：双击桌面可隐藏原图标和任务栏，分组是否隐藏由上方开关决定。"
                    : "提示：双击桌面可隐藏原图标，分组是否隐藏由上方开关决定。")
                : "提示：托盘图标右键可以快速新建盒子和显示 / 隐藏桌面图标。";
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>整理 / 撤销之后在窗口里给出结果反馈。</summary>
    public void SetOrganizeStatus(string message)
    {
        LblOrganizeStatus.Text = message;
        LblOrganizeStatus.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>一键整理盒子之后的反馈。</summary>
    public void SetArrangeStatus(string message)
    {
        LblArrangeStatus.Text = message;
        LblArrangeStatus.Visibility = string.IsNullOrEmpty(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _config.HideIconsOnDoubleClick = ChkDoubleClick.IsChecked == true;
        _config.HideBoxesWithIcons = ChkHideGroups.IsChecked == true;
        _config.AutoCheckForUpdates = ChkAutoCheckUpdates.IsChecked == true;
        _config.HideTaskbarWithIcons = ChkHideTaskbar.IsChecked == true;
        _config.RestoreIconsOnExit = ChkRestoreIcons.IsChecked == true;
        _config.ShowHiddenFiles = ChkHidden.IsChecked == true;
        _config.OrganizeShortcuts = ChkShortcuts.IsChecked == true;
        _config.AutoAlignBoxes = ChkAlign.IsChecked == true;
        _config.AvoidBoxOverlap = ChkAvoidOverlap.IsChecked == true;
        _config.LiquidGlassEnabled = ChkGlass.IsChecked == true;

        if (ChkAutoStart.IsChecked == true != AutoStart.IsEnabled)
        {
            AutoStart.Set(ChkAutoStart.IsChecked == true);
            _config.AutoStart = ChkAutoStart.IsChecked == true;
        }

        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag)
        {
            return;
        }

        _config.Theme = tag switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        ThemeManager.Apply(_config.Theme);

        _loading = true;
        PillLight.IsChecked = _config.Theme == ThemeMode.Light;
        PillDark.IsChecked = _config.Theme == ThemeMode.Dark;
        PillSystem.IsChecked = _config.Theme == ThemeMode.System;
        _loading = false;

        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || LblOpacity is null || LblIcon is null || LblRadius is null)
        {
            return;
        }

        LblOpacity.Text = $"{SlOpacity.Value:0}%";
        LblIcon.Text = $"{SlIcon.Value:0} px";
        LblRadius.Text = $"{SlRadius.Value:0} px";

        _config.BoxOpacity = SlOpacity.Value / 100.0;
        _config.IconSize = Math.Round(SlIcon.Value);
        _config.CornerRadius = Math.Round(SlRadius.Value);

        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void GlassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || LblGlassBlur is null || LblGlassRefraction is null) return;
        _config.GlassBlur = SlGlassBlur.Value;
        _config.GlassRefraction = SlGlassRefraction.Value;
        LblGlassBlur.Text = $"{SlGlassBlur.Value:0.0}";
        LblGlassRefraction.Text = $"{SlGlassRefraction.Value:0.0}";
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void Organize_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("organize");

    private void Arrange_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("arrange");

    private void OpenRoot_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("openroot");

    private void Undo_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("undo");

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("openconfig");

    private void OpenLog_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("openlog");

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("checkupdates");
    private void InstallUpdate_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("installupdate");
    private void CancelUpdate_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("cancelupdate");
    private void Releases_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke("releases");

    public void ScrollToUpdates()
    {
        NavUpdates.IsChecked = true;
        SelectSection("updates");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is RadioButton { Tag: string section }) SelectSection(section);
    }

    private void SelectSection(string section)
    {
        _section = section;
        foreach (FrameworkElement page in Pages.Children)
            page.Visibility = Equals(page.Tag, section) ? Visibility.Visible : Visibility.Collapsed;
        (PageTitle.Text, PageSubtitle.Text) = section switch
        {
            "appearance" => ("外观", "调整盒子背景、玻璃效果与主题。"),
            "layout" => ("盒子排布", "让桌面上的盒子对齐、有序，保持恰好的间距。"),
            "organize" => ("整理", "按类型归类桌面文件，也可以随时撤销上次整理。"),
            "general" => ("常规", "管理启动方式与本地数据。"),
            "updates" => ("版本更新", "获取最新功能与改进，保留熟悉的桌面布局。"),
            _ => ("图标", "调整盒内图标大小与桌面图标的显示方式。"),
        };
        Scroller.ScrollToTop();
    }

    public void SetUpdateState(UpdateService updates)
    {
        LblUpdateStatus.Text = updates.Status;
        BtnCheckUpdates.IsEnabled = !updates.IsBusy;
        BtnInstallUpdate.IsEnabled = !updates.IsBusy;
        BtnInstallUpdate.Visibility = updates.AvailableRelease is null ? Visibility.Collapsed : Visibility.Visible;
        BtnCancelUpdate.Visibility = updates.IsBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开失败 {path}: {ex.Message}");
        }
    }
}
