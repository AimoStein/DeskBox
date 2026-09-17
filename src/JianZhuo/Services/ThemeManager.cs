using System.Windows;
using Microsoft.Win32;
using JianZhuo.Models;

namespace JianZhuo.Services;

public static class ThemeManager
{
    private static ResourceDictionary? _current;
    private static bool? _appliedDark;

    public static bool IsDark { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };

        // 已经是这个配色就直接返回：设置面板拖动滑块时会频繁调用
        if (_current is not null && _appliedDark == dark)
        {
            return;
        }

        var uri = new Uri(
            dark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml",
            UriKind.Absolute);

        var dictionary = new ResourceDictionary { Source = uri };

        if (_current is not null)
        {
            app.Resources.MergedDictionaries.Remove(_current);
        }

        app.Resources.MergedDictionaries.Insert(0, dictionary);
        _current = dictionary;
        _appliedDark = dark;
        IsDark = dark;
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static System.Drawing.Color TrayBackColor =>
        IsDark ? System.Drawing.Color.FromArgb(38, 38, 43) : System.Drawing.Color.White;

    public static System.Drawing.Color TrayTextColor =>
        IsDark ? System.Drawing.Color.FromArgb(242, 242, 245) : System.Drawing.Color.FromArgb(27, 27, 31);

    public static System.Drawing.Color TrayAccentColor =>
        IsDark ? System.Drawing.Color.FromArgb(76, 147, 255) : System.Drawing.Color.FromArgb(43, 124, 255);
}
