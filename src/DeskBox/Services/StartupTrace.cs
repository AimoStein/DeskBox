using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace DeskBox.Services;

/// <summary>
/// 启动耗时埋点：模块初始化、App 构造、盒子构造分项、首帧、玻璃引擎、图标、桌面就绪检查。
///
/// 统一 `[启动]` 前缀，每条给出「本阶段 / 累计 / 自进程启动」三个数，
/// 这样既能看流程内部占比，也能对齐到"用户看到桌面之后过了多久盒子才出来"。
/// 只在启动阶段写入；运行期调用点用 <see cref="IsStartup"/> 保护。
/// </summary>
internal static class StartupTrace
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static readonly DateTime ProcessStart = GetProcessStartTime();
    private static long _lastMarkMs;
    private static long _logWriteTicks;

    /// <summary>启动阶段标记：App 启动完成后置 false，避免运行期产生启动日志。</summary>
    internal static bool IsStartup { get; set; } = true;

    /// <summary>自进程启动以来的毫秒数；取不到进程启动时间时返回 -1。</summary>
    internal static long SinceProcessStartMs =>
        ProcessStart == DateTime.MinValue ? -1 : (long)(DateTime.Now - ProcessStart).TotalMilliseconds;

    /// <summary>
    /// 模块初始化器：在 Main 之前执行，用来量出 .NET 宿主 / 单文件自解压 / 框架加载花了多久。
    /// </summary>
    [ModuleInitializer]
    internal static void OnInit()
    {
        Mark("模块初始化（托管入口）");
    }

    /// <summary>记一条启动阶段日志。</summary>
    internal static void Mark(string stage)
    {
        var elapsed = Watch.ElapsedMilliseconds;
        Log.Info($"[启动] {stage}：本阶段 {elapsed - _lastMarkMs}ms，累计 {elapsed}ms，自进程启动 {SinceProcessStartMs}ms");
        _lastMarkMs = elapsed;
    }

    /// <summary>累计日志文件写入耗时（启动阶段由 Log 调用），用来判断同步写日志是不是启动瓶颈。</summary>
    internal static void NoteLogWrite(long elapsedTicks) => Interlocked.Add(ref _logWriteTicks, elapsedTicks);

    internal static long LogWriteMs => (long)(Interlocked.Read(ref _logWriteTicks) * 1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// 启动主体跑完后继续观察一段时间：首帧、首批图标、开机时桌面是否后出现，都要等 Dispatcher 转起来才看得到。
    /// 观测期结束后 <see cref="IsStartup"/> 归 false，运行期不再写启动日志。
    /// </summary>
    internal static void EndStartupAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log.Info($"[启动] 启动观测结束（自进程启动 {SinceProcessStartMs}ms）");
            IsStartup = false;
        };
        timer.Start();
    }

    private static DateTime GetProcessStartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
