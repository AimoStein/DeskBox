using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace DeskBox.Services;

/// <summary>
/// 图标磁盘缓存：%配置目录%\iconcache\SHA1(key).png。
/// 进程重启后图标直接从磁盘解码，不用再等 shell 的冷缓存。
/// 这一层只做「读 PNG / 写 PNG / 按上限清理」，任何失败只记日志，绝不抛出。
/// </summary>
internal static class IconDiskCache
{
    /// <summary>文件数上限，超了就按最旧清理。</summary>
    private const int MaxFiles = 4000;

    /// <summary>总大小上限 256MB。</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    /// <summary>清理时多删一点（留 10% 余量），免得刚删完又超限、每次写都全量扫目录。</summary>
    private const double TrimTarget = 0.9;

    /// <summary>每写这么多次才检查一次上限，别每次写都去列举目录。</summary>
    private const int ScanEveryWrites = 128;

    private static int _writesSinceScan;
    private static int _scanning;

    /// <summary>磁盘缓存目录。</summary>
    public static string Directory => Path.Combine(Models.ConfigStore.Directory, "iconcache");

    /// <summary>key 对应的缓存文件：SHA1（小写 hex）+ .png。</summary>
    public static string PathFor(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Directory, Convert.ToHexString(hash).ToLowerInvariant() + ".png");
    }

    /// <summary>读缓存并解码成冻结位图；没有/坏了都返回 false，绝不抛出。</summary>
    public static bool TryRead(string key, out BitmapSource? image)
    {
        image = null;

        try
        {
            var file = PathFor(key);
            if (!File.Exists(file))
            {
                return false;
            }

            var bytes = File.ReadAllBytes(file);
            if (bytes.Length == 0)
            {
                return false;
            }

            using var stream = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            image = frame;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"图标磁盘缓存读取失败 {key}: {ex.Message}");
            return false;
        }
    }

    /// <summary>写缓存：先写 .tmp 再原子替换；失败只记日志。</summary>
    public static void Write(string key, BitmapSource image)
    {
        string? temp = null;

        try
        {
            var file = PathFor(key);
            System.IO.Directory.CreateDirectory(Directory);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            buffer.Position = 0;

            temp = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                buffer.CopyTo(target);
            }

            File.Move(temp, file, overwrite: true);
            temp = null;

            TrimIfNeeded();
        }
        catch (Exception ex)
        {
            Log.Warn($"图标磁盘缓存写入失败 {key}: {ex.Message}");
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 临时文件删不掉就算了
                }
            }
        }
    }

    /// <summary>
    /// 文件数或总大小超限时，按 LastWriteTime 从旧到新删到限额以内（best-effort）。
    /// 只能在后台线程调用；默认带写入次数去抖，不会每次写都扫目录。
    /// </summary>
    public static void TrimIfNeeded(bool force = false)
    {
        if (!force && Interlocked.Increment(ref _writesSinceScan) < ScanEveryWrites)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _writesSinceScan, 0);

            var directory = Directory;
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            var files = new List<FileInfo>();
            long total = 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.png"))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }

                    files.Add(info);
                    total += info.Length;
                }
                catch
                {
                    // 单个文件读不到就跳过
                }
            }

            if (files.Count <= MaxFiles && total <= MaxBytes)
            {
                return;
            }

            var keepFiles = (int)(MaxFiles * TrimTarget);
            var keepBytes = (long)(MaxBytes * TrimTarget);
            var removed = 0;

            foreach (var info in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (files.Count - removed <= keepFiles && total <= keepBytes)
                {
                    break;
                }

                try
                {
                    total -= info.Length;
                    info.Delete();
                    removed++;
                }
                catch
                {
                    // 删不掉（被占用）就留着，下次再说
                }
            }

            if (removed > 0)
            {
                Log.Info($"[图标] 磁盘缓存超限，清理 {removed} 个最旧文件，剩余 {files.Count - removed} 个 / {total / 1024 / 1024}MB");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("图标磁盘缓存清理失败: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }
}
