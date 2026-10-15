using System.Runtime.InteropServices;
using System.Windows;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 一次拖放里解析出来的一项：可能是磁盘真实路径，也可能是 shell 命名空间项
/// （回收站 / 此电脑 / 控制面板 / 网络这类系统图标）。
/// FileSystemPath 非空 = 真实路径，按原来的移动语义处理；
/// FileSystemPath 为空 = 命名空间项，只能按 ParsingName 建 .lnk。
/// </summary>
public readonly record struct ShellDropItem(string? FileSystemPath, string ParsingName, string DisplayName);

/// <summary>
/// 解析拖放数据。资源管理器拖系统图标时给的是 CFSTR_SHELLIDLIST（"Shell IDList Array"，CIDA 结构），
/// 里面没有文件路径，只有 PIDL。这里把它拆成 ShellDropItem，交给上层决定是搬运还是建快捷方式。
/// 所有失败都只记日志，绝不向外抛异常。
/// </summary>
public static class ShellDrop
{
    /// <summary>FileDrop 或 "Shell IDList Array" 任一存在即可接受。</summary>
    public static bool CanAccept(IDataObject? data)
    {
        if (data is null)
        {
            return false;
        }

        try
        {
            return HasFormat(data, DataFormats.FileDrop) || HasFormat(data, ShellItemInterop.ShellIdListArrayFormat);
        }
        catch (Exception ex)
        {
            // 剪贴板/拖放源可能已经消失，这里不能往外抛
            Log.Warn($"判断拖放内容失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>解析拖放内容；解析不了的项跳过并 Log.Warn，绝不向外抛异常。</summary>
    public static IReadOnlyList<ShellDropItem> Read(IDataObject? data)
    {
        var items = new List<ShellDropItem>();
        if (data is null)
        {
            return items;
        }

        try
        {
            // FileDrop 里先出现的项记下来，后面 IDList 里重复的同一项不再重复添加
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in ReadFileDrop(data))
            {
                if (ShellItemInterop.LooksLikeParsingName(raw))
                {
                    // 有些来源会把 ::{GUID} 混在 FileDrop 里，按虚拟项处理
                    var parsingName = raw.Trim();
                    if (seen.Add(parsingName))
                    {
                        items.Add(new ShellDropItem(null, parsingName, DisplayNameOf(parsingName) ?? parsingName));
                    }

                    continue;
                }

                var path = raw.Trim();
                if (path.Length == 0 || !seen.Add(path))
                {
                    continue;
                }

                items.Add(new ShellDropItem(path, path, System.IO.Path.GetFileName(path.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))));
            }

            var shellItems = ReadShellItems(data);
            foreach (var item in shellItems)
            {
                if (item.FileSystemPath is not { Length: > 0 } realPath)
                {
                    // 系统图标按解析名去重
                    if (!seen.Add(item.ParsingName))
                    {
                        continue;
                    }

                    items.Add(item);
                    continue;
                }

                if (!seen.Add(realPath))
                {
                    continue;
                }

                items.Add(item);
            }

            // 系统解析器没给出东西时才自己拆 CIDA（兜底）
            if (shellItems.Count == 0)
            {
                foreach (var item in ReadIdList(data))
                {
                    // 资源管理器一般两种格式都给：真实文件按路径去重，系统图标按解析名去重
                    var key = item.FileSystemPath is { Length: > 0 } path ? path : item.ParsingName;
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    items.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析拖放内容失败：{ex.Message}");
        }

        return items;
    }

    // ---------------------------------------------------------------- 系统解析器（首选）

    /// <summary>
    /// 用系统自己的 SHCreateShellItemArrayFromDataObject 解析拖放数据。
    /// 这比手工拆 CIDA 可靠：真实资源管理器给的 IDList 布局和手工拼的并不一样
    /// （例如末项 PIDL 可能没有结尾的 2 字节 0）。取不到就返回空，交给 CIDA 兜底。
    /// </summary>
    private static List<ShellDropItem> ReadShellItems(IDataObject data)
    {
        var items = new List<ShellDropItem>();

        var comDataObject = ShellItemInterop.TryGetComDataObject(data);
        if (comDataObject is null)
        {
            return items;
        }

        var array = ShellItemInterop.CreateShellItemArray(comDataObject);
        if (array is null)
        {
            return items;
        }

        try
        {
            if (array.GetCount(out var count) != ShellItemInterop.S_OK)
            {
                return items;
            }

            for (uint i = 0; i < count; i++)
            {
                if (array.GetItemAt(i, out var shellItem) != ShellItemInterop.S_OK || shellItem is null)
                {
                    Log.Warn($"Shell 项 {i + 1} 取不到，跳过。");
                    continue;
                }

                try
                {
                    var item = Describe(shellItem);
                    if (item is null)
                    {
                        Log.Warn($"Shell 项 {i + 1} 解析不出路径也解析不出名字，跳过。");
                    }
                    else
                    {
                        items.Add(item.Value);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(shellItem);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 Shell 项失败：{ex.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(array);
        }

        return items;
    }

    /// <summary>IShellItem → ShellDropItem：先真实路径，再解析名 + 显示名。</summary>
    private static ShellDropItem? Describe(ShellItemInterop.IShellItem shellItem)
    {
        var path = ShellItemInterop.GetItemName(shellItem, ShellItemInterop.SIGDN.FILESYSPATH);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return new ShellDropItem(path, path, System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)));
        }

        var parsingName = ShellItemInterop.GetItemName(shellItem, ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING);
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        var displayName = ShellItemInterop.GetItemName(shellItem, ShellItemInterop.SIGDN.NORMALDISPLAY);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = parsingName;
        }

        return new ShellDropItem(null, parsingName, displayName);
    }

    // ---------------------------------------------------------------- FileDrop

    private static IEnumerable<string> ReadFileDrop(IDataObject data)
    {
        object? value = null;
        try
        {
            if (HasFormat(data, DataFormats.FileDrop))
            {
                value = data.GetData(DataFormats.FileDrop);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 FileDrop 失败：{ex.Message}");
        }

        if (value is string[] paths)
        {
            return paths.Where(p => !string.IsNullOrWhiteSpace(p));
        }

        if (value is string single && !string.IsNullOrWhiteSpace(single))
        {
            return new[] { single };
        }

        return Array.Empty<string>();
    }

    // ---------------------------------------------------------------- CIDA

    private static IEnumerable<ShellDropItem> ReadIdList(IDataObject data)
    {
        var buffer = GetIdListBytes(data);
        if (buffer is null || buffer.Length < 12)
        {
            yield break;
        }

        // CIDA：DWORD cidl; DWORD aoffset[cidl + 1];
        // aoffset[0] = 父文件夹 PIDL 相对 CIDA 起点的偏移，aoffset[1..cidl] = 每个条目 PIDL 的偏移
        var count = BitConverter.ToUInt32(buffer, 0);
        var headerSize = 4 + ((count + 1) * 4);

        if (count == 0 || headerSize > buffer.Length)
        {
            Log.Warn($"Shell IDList 结构不完整：cidl={count} 长度={buffer.Length}");
            yield break;
        }

        var parentOffset = BitConverter.ToUInt32(buffer, 4);

        for (var i = 0; i < count; i++)
        {
            var itemOffset = BitConverter.ToUInt32(buffer, 8 + (i * 4));
            if (itemOffset < headerSize || itemOffset >= buffer.Length)
            {
                Log.Warn($"Shell IDList 第 {i + 1} 项偏移越界：{itemOffset}/{buffer.Length}");
                continue;
            }

            var relativePidl = PidlAt(buffer, (int)itemOffset);
            if (relativePidl == IntPtr.Zero)
            {
                Log.Warn($"Shell IDList 第 {i + 1} 项不是完整 PIDL，跳过。");
                continue;
            }

            // 拖放里的条目 PIDL 一般是相对父文件夹的，拼上父 PIDL 才是绝对 PIDL。
            // aoffset[0] 为 0 表示没有父 PIDL（有些来源会这样给），此时条目 PIDL 本身就是绝对 PIDL。
            var parentPidl = parentOffset >= headerSize && parentOffset < buffer.Length
                ? PidlAt(buffer, (int)parentOffset)
                : IntPtr.Zero;
            var absolutePidl = Combine(parentPidl, relativePidl);

            try
            {
                var item = Describe(absolutePidl);
                if (item is null)
                {
                    Log.Warn($"Shell IDList 第 {i + 1} 项解析不出路径也解析不出名字，跳过。");
                }
                else
                {
                    yield return item.Value;
                }
            }
            finally
            {
                ShellItemInterop.FreePidl(absolutePidl);
                ShellItemInterop.FreePidl(parentPidl);
                ShellItemInterop.FreePidl(relativePidl);
            }
        }
    }

    /// <summary>把 CIDA 里的一个条目变成 ShellDropItem；两种情况都失败时返回 null。</summary>
    private static ShellDropItem? Describe(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        var path = ShellItemInterop.GetFileSystemPath(pidl);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return new ShellDropItem(path, path, System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)));
        }

        // 系统图标：没有文件系统路径，只有解析名（::{645FF040-…}）和显示名（"回收站"）
        var parsingName = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.DESKTOPABSOLUTEPARSING)
            ?? ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.PARENTRELATIVEPARSING)
            ?? ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.URL);

        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        var displayName = ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.NORMALDISPLAY)
            ?? System.IO.Path.GetFileName(parsingName.TrimEnd('\\'));

        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = parsingName;
        }

        return new ShellDropItem(null, parsingName, displayName);
    }

    private static string? DisplayNameOf(string parsingName)
    {
        var pidl = ShellItemInterop.ParseDisplayName(parsingName);
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ShellItemInterop.GetName(pidl, ShellItemInterop.SIGDN.NORMALDISPLAY);
        }
        finally
        {
            ShellItemInterop.FreePidl(pidl);
        }
    }

    /// <summary>父 PIDL + 相对 PIDL 拼成一个绝对 PIDL（都要自己释放）。</summary>
    private static IntPtr Combine(IntPtr parent, IntPtr relative)
    {
        var parentSize = ShellItemInterop.PidlSize(parent);
        var relativeSize = ShellItemInterop.PidlSize(relative);

        if (relativeSize <= 0)
        {
            return IntPtr.Zero;
        }

        var total = parentSize + relativeSize;
        var buffer = Marshal.AllocCoTaskMem(total);
        if (buffer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (parentSize > 0)
        {
            // 父 PIDL 结尾有 2 字节的 0；拷贝时可省掉，但留着也无害，这里按 SH 的习惯去掉
            var copy = parentSize - 2;
            for (var i = 0; i < copy; i++)
            {
                Marshal.WriteByte(buffer, i, Marshal.ReadByte(parent, i));
            }

            for (var i = 0; i < relativeSize; i++)
            {
                Marshal.WriteByte(buffer, copy + i, Marshal.ReadByte(relative, i));
            }
        }
        else
        {
            for (var i = 0; i < relativeSize; i++)
            {
                Marshal.WriteByte(buffer, i, Marshal.ReadByte(relative, i));
            }
        }

        return buffer;
    }

    /// <summary>从 CIDA 字节流里读一个 PIDL 并复制出来（调用方负责 FreePidl）。</summary>
    private static IntPtr PidlAt(byte[] buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return IntPtr.Zero;
        }

        var size = PidlLengthAt(buffer, offset, out var terminated);
        if (size <= 0)
        {
            return IntPtr.Zero;
        }

        // 真实 Explorer 的 CIDA 末项可能没有结尾的 2 字节 0，这里补一个，
        // 否则 shell API 会顺着内存往后读。
        var total = terminated ? size : size + 2;

        var pidl = Marshal.AllocCoTaskMem(total);
        if (pidl == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(buffer, offset, pidl, size);
        if (!terminated)
        {
            Marshal.WriteInt16(pidl, size, 0);
        }

        return pidl;
    }

    /// <summary>
    /// 量一个 PIDL 在缓冲区里占多少字节；terminated = 缓冲区里自带结尾的 2 字节 0。
    /// 关键：SHITEMID 的 cb 是「这一个 item id 的总字节数，含 cb 自己」，
    /// 下一个 item id 在 pidl + cb 处（shell 的 _ILNext 就是这么走的），不是 pidl + 2 + cb。
    /// 之前按 2 + cb 走，真实资源管理器给的 CIDA 里父 PIDL 会被读成 cb=17199，整个解析就废了。
    /// </summary>
    private static int PidlLengthAt(byte[] buffer, int offset, out bool terminated)
    {
        terminated = false;

        var cursor = offset;
        while (cursor + 2 <= buffer.Length)
        {
            var cb = BitConverter.ToUInt16(buffer, cursor);

            if (cb == 0)
            {
                terminated = true;
                return cursor + 2 - offset;
            }

            cursor += cb;

            if (cursor > buffer.Length)
            {
                return 0;
            }
        }

        // 兜底：最后一个 PIDL 正好顶到缓冲区末尾、没带结尾 0（正常数据不会这样）
        return cursor == buffer.Length ? cursor - offset : 0;
    }

    // ---------------------------------------------------------------- HGLOBAL / Stream

    /// <summary>
    /// 取 "Shell IDList Array" 的原始字节。WPF 里通常是 Stream（HGLOBAL 包成 MemoryStream），
    /// 也可能是 byte[]，两种都兼容。
    /// </summary>
    private static byte[]? GetIdListBytes(IDataObject data)
    {
        try
        {
            if (!HasFormat(data, ShellItemInterop.ShellIdListArrayFormat))
            {
                return null;
            }

            var value = data.GetData(ShellItemInterop.ShellIdListArrayFormat);
            switch (value)
            {
                case null:
                    return null;

                case byte[] bytes:
                    return bytes.Length == 0 ? null : bytes;

                case MemoryStream memory:
                    return memory.Length == 0 ? null : memory.ToArray();

                case System.IO.Stream stream:
                    if (stream.Length == 0)
                    {
                        return null;
                    }

                    stream.Position = 0;
                    using (var copy = new MemoryStream())
                    {
                        stream.CopyTo(copy);
                        return copy.Length == 0 ? null : copy.ToArray();
                    }

                default:
                    Log.Warn($"Shell IDList Array 是没见过的类型：{value.GetType().FullName}");
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 Shell IDList Array 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>格式是否存在；用字符串名和注册过的 Format 都问一遍。</summary>
    private static bool HasFormat(IDataObject data, string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        if (data.GetDataPresent(format))
        {
            return true;
        }

        try
        {
            // 注册过的格式名可能和字面量不完全一致，用注册表里的规范名再问一次
            var registered = DataFormats.GetDataFormat(format);
            return registered is not null &&
                !string.IsNullOrEmpty(registered.Name) &&
                data.GetDataPresent(registered.Name);
        }
        catch
        {
            return false;
        }
    }
}
