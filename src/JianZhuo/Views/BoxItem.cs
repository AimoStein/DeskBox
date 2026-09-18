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
    private bool _insertMark;

    public BoxItem(string path, int iconSize)
    {
        Path = path;
        Name = DisplayName(path);

        IsDirectory = Directory.Exists(path);
        IconSize = iconSize;
        ItemWidth = iconSize + 26;
        ItemHeight = iconSize + 48;
        Icon = IconCacheHolder.Placeholder(iconSize);
    }

    /// <summary>
    /// 显示名：快捷方式（.lnk）和网址快捷方式（.url）隐藏扩展名，和资源管理器一致。
    /// 其它文件保留扩展名，免得「合同.docx」变成「合同」后看不出类型。
    /// </summary>
    private static string DisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            return path;
        }

        var extension = System.IO.Path.GetExtension(name);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var stem = name[..^extension.Length];
        return stem.Length > 0 ? stem : name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public int IconSize { get; }

    public double ItemWidth { get; }

    /// <summary>固定高度：所有条目一样高，缩小 / 放大才能按格子顿挫。</summary>
    public double ItemHeight { get; }

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

    /// <summary>拖动其它条目经过时，在这一条前面显示插入标记。</summary>
    public bool InsertMark
    {
        get => _insertMark;
        set
        {
            if (_insertMark != value)
            {
                _insertMark = value;
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
