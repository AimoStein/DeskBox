using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace JianZhuo.Views;

/// <summary>盒子里的一条内容（对应真实文件系统里的一项）。</summary>
public sealed class BoxItem : INotifyPropertyChanged
{
    private BitmapSource? _icon;
    private bool _selected;

    public BoxItem(string path, int iconSize)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(Name))
        {
            Name = path;
        }

        IsDirectory = Directory.Exists(path);
        IconSize = iconSize;
        ItemWidth = iconSize + 26;
        Icon = IconCacheHolder.Placeholder(iconSize);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public int IconSize { get; }

    public double ItemWidth { get; }

    public string Meta => IsDirectory ? "文件夹" : BuildMeta();

    public BitmapSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Raise();
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Raise();
            }
        }
    }

    private string BuildMeta()
    {
        try
        {
            var info = new FileInfo(Path);
            var size = info.Length switch
            {
                < 1024 => $"{info.Length} B",
                < 1024 * 1024 => $"{info.Length / 1024.0:0.#} KB",
                < 1024L * 1024 * 1024 => $"{info.Length / 1024.0 / 1024:0.#} MB",
                _ => $"{info.Length / 1024.0 / 1024 / 1024:0.##} GB",
            };

            return $"{size} · {info.LastWriteTime:yyyy-MM-dd}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>避免 Views 层到处 using Services。</summary>
internal static class IconCacheHolder
{
    public static BitmapSource Placeholder(int size) => Services.IconCache.Placeholder(size);

    public static Task<BitmapSource?> LoadAsync(string path, int size) => Services.IconCache.GetAsync(path, size);
}
