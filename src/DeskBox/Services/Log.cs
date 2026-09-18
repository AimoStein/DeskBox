using System.IO;

namespace DeskBox;

/// <summary>极简日志，便于自用排查。放在根命名空间，各层都能直接用。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Models.ConfigStore.Directory, "deskbox.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Models.ConfigStore.Directory);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);

                var info = new FileInfo(FilePath);
                if (info.Length > 512 * 1024)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
            }
        }
        catch
        {
            // 日志失败绝不打扰主流程
        }
    }
}
