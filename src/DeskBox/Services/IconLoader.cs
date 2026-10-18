using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskBox.Interop;

namespace DeskBox.Services;

/// <summary>
/// 图标加载器：常驻 STA 工作线程 + 队列，取代「一个图标一个线程」的做法。
/// 取图顺序：内存 → 磁盘缓存 → shell 提取（E_PENDING 有界重试，总等待 ≤1.2s）→ 回写磁盘缓存。
/// 同一 key 的并发请求只排一次队；交给调用方的一律是冻结过的位图，可以直接给 UI。
/// </summary>
internal static class IconLoader
{
    /// <summary>常驻 STA 工作线程数：有界并发，开机时不会再炸出几十个线程。</summary>
    private const int WorkerCount = 2;

    /// <summary>shell 提取的总等待上限（含 E_PENDING 退避），超过就放弃并回落到占位图。</summary>
    private const int ShellBudgetMs = 1200;

    /// <summary>E_PENDING 的退避节奏；累计 900ms，给 shell 调用本身留约 300ms。</summary>
    private static readonly int[] PendingBackoffMs = { 150, 300, 450 };

    /// <summary>一批加载完成后再安静这么久就结算日志（去抖，避免刷屏）。</summary>
    private const int BatchQuietMs = 400;

    /// <summary>工作线程空转时的轮询间隔，用来及时捡起到期的 E_PENDING 重试。</summary>
    private const int IdlePollMs = 25;

    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>内存缓存：key = path|size|ticks，值一律是冻结后的位图。</summary>
    private static readonly ConcurrentDictionary<string, BitmapSource> Memory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>排队中的请求（同 key 去重）。预热请求单独记一个 key，免得把正式请求带进沟里。</summary>
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<BitmapSource?>> Inflight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>待处理队列。并发上限由工作线程数决定，队列本身不设硬上限（key 数量天然有限）。</summary>
    private static readonly BlockingCollection<IconRequest> Queue = new();

    /// <summary>还在等 E_PENDING 的请求：放在这里不占工作线程。</summary>
    private static readonly List<IconRequest> Delayed = new();

    private static readonly object DelayGate = new();

    // ── 统计与批量日志 ─────────────────────────────────────────────────
    private static long _memoryHits;
    private static long _diskHits;
    private static long _shellHits;

    private static readonly object BatchGate = new();
    private static readonly Stopwatch BatchWatch = new();
    private static readonly Timer BatchTimer;

    private static bool _batchActive;
    private static int _batchCount;
    private static int _batchMemory;
    private static int _batchDisk;
    private static int _batchShell;
    private static long _batchElapsedMs;

    /// <summary>还没结束的请求数：归零说明这一批干完了。</summary>
    private static int _pending;

    /// <summary>启动后的磁盘缓存超限检查只强制做一次。</summary>
    private static int _startupTrimDone;

    /// <summary>首个图标就绪只在启动阶段记一次日志。</summary>
    private static int _firstDeliveryLogged;

    static IconLoader()
    {
        BatchTimer = new Timer(_ => FlushBatch(), null, Timeout.Infinite, Timeout.Infinite);

        for (var i = 0; i < WorkerCount; i++)
        {
            var worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"DeskBox.Icon.{i}",
            };

            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
    }

    /// <summary>key = 路径 + 尺寸 + 文件时间戳：文件改了就是新 key，自然绕过旧缓存。</summary>
    public static string BuildKey(string path, int size)
    {
        long ticks = 0;
        try
        {
            if (File.Exists(path))
            {
                ticks = File.GetLastWriteTimeUtc(path).Ticks;
            }
            else if (System.IO.Directory.Exists(path))
            {
                ticks = System.IO.Directory.GetLastWriteTimeUtc(path).Ticks;
            }
        }
        catch
        {
            // 拿不到时间戳就用 0
        }

        return $"{path}|{size}|{ticks}";
    }

    /// <summary>只查内存缓存，立即返回，绝不碰 shell 或磁盘。</summary>
    public static BitmapSource? TryGet(string path, int size)
    {
        try
        {
            return Memory.TryGetValue(BuildKey(path, size), out var image) ? image : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>异步取图；任何失败都返回 null，不抛异常。</summary>
    public static Task<BitmapSource?> LoadAsync(string path, int size, bool allowShell)
    {
        try
        {
            var key = BuildKey(path, size);

            // 内存命中：直接给，不排队
            if (Memory.TryGetValue(key, out var cached))
            {
                NoteRequest();
                NoteSource(HitSource.Memory);
                NoteDrained();
                return Task.FromResult<BitmapSource?>(cached);
            }

            var modeKey = ModeKey(key, allowShell);
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var actual = Inflight.GetOrAdd(modeKey, completion);

            if (!ReferenceEquals(actual, completion))
            {
                // 同一个 key 已经在排队/提取中，共享结果，不再占用工作线程
                return actual.Task;
            }

            NoteRequest();
            Interlocked.Increment(ref _pending);
            Queue.Add(new IconRequest
            {
                Key = key,
                Path = path,
                Size = size,
                AllowShell = allowShell,
                StartedAt = Environment.TickCount64,
                Completion = completion,
            });

            return completion.Task;
        }
        catch (Exception ex)
        {
            Log.Warn($"图标排队失败 {path}: {ex.Message}");
            return Task.FromResult<BitmapSource?>(null);
        }
    }

    /// <summary>同步取图（兼容旧 API）：真正干活的是工作线程，这里只是等结果。</summary>
    public static BitmapSource? LoadBlocking(string path, int size)
    {
        try
        {
            return LoadAsync(path, size, allowShell: true).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"同步取图标失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>预热：后台把磁盘缓存解码进内存，不碰 shell，也不阻塞调用方。</summary>
    public static void Prewarm(IEnumerable<string> paths, int size)
    {
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (Memory.ContainsKey(BuildKey(path, size)))
                {
                    continue;
                }

                _ = LoadAsync(path, size, allowShell: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"图标预热跳过 {path}: {ex.Message}");
            }
        }

        // 预热完顺手看一眼磁盘缓存有没有超限（在工作线程上做，不占 UI；每个进程只强制扫一次）
        if (Interlocked.CompareExchange(ref _startupTrimDone, 1, 0) == 0)
        {
            _ = Task.Run(() => IconDiskCache.TrimIfNeeded(force: true));
        }
    }

    public static void ClearMemory() => Memory.Clear();

    /// <summary>删除所有以 prefix（形如 path+"|"）开头的内存缓存项。</summary>
    public static void InvalidatePrefix(string prefix)
    {
        foreach (var key in Memory.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Memory.TryRemove(key, out _);
            }
        }
    }

    public static string StatsSummary() =>
        $"内存命中 {Interlocked.Read(ref _memoryHits)} / 磁盘命中 {Interlocked.Read(ref _diskHits)} / shell 提取 {Interlocked.Read(ref _shellHits)}";

    // ── 工作线程 ───────────────────────────────────────────────────────

    private static void WorkerLoop()
    {
        while (true)
        {
            try
            {
                // 到期的 E_PENDING 重试优先，免得一直被新请求插队饿死
                var request = TakeDue();
                if (request is null)
                {
                    Queue.TryTake(out var taken, IdlePollMs);
                    request = taken;
                }

                if (request is null)
                {
                    continue;
                }

                Process(request);
            }
            catch (Exception ex)
            {
                // 工作线程绝不能因为某个图标挂掉
                Log.Warn("图标工作线程异常: " + ex.Message);
            }
        }
    }

    private static void Process(IconRequest request)
    {
        BitmapSource? image = null;

        try
        {
            if (Memory.TryGetValue(request.Key, out var cached))
            {
                NoteSource(HitSource.Memory);
                image = cached;
            }
            else if (IconDiskCache.TryRead(request.Key, out var fromDisk) && fromDisk is not null)
            {
                // 磁盘解码 + Freeze 都在工作线程上做
                Memory[request.Key] = fromDisk;
                NoteSource(HitSource.Disk);
                image = fromDisk;
            }
            else if (request.AllowShell)
            {
                image = TryExtractOnce(request.Path, request.Size, out var pending);

                if (image is not null)
                {
                    Memory[request.Key] = image;
                    NoteSource(HitSource.Shell);
                    IconDiskCache.Write(request.Key, image);
                }
                else if (pending && ScheduleRetry(request))
                {
                    // 还是 E_PENDING：先让位给别的图标，等退避时间到了再试
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"取图标失败 {request.Path}: {ex.Message}");
            image = null;
        }

        Inflight.TryRemove(ModeKey(request.Key, request.AllowShell), out _);
        request.Completion.TrySetResult(image);

        if (image is not null && StartupTrace.IsStartup &&
            Interlocked.CompareExchange(ref _firstDeliveryLogged, 1, 0) == 0)
        {
            Log.Info($"[启动] 首个图标就绪，自进程启动 {StartupTrace.SinceProcessStartMs}ms");
        }

        if (Interlocked.Decrement(ref _pending) == 0)
        {
            NoteDrained();
        }
    }

    /// <summary>等 E_PENDING 的请求重新排进延迟表；超过总预算就返回 false（放弃）。</summary>
    private static bool ScheduleRetry(IconRequest request)
    {
        var spent = Environment.TickCount64 - request.StartedAt;
        if (spent >= ShellBudgetMs)
        {
            Log.Warn($"图标始终未就绪（E_PENDING 等待 {spent}ms 超过 {ShellBudgetMs}ms）{request.Path}");
            return false;
        }

        var index = Math.Min(request.Attempts, PendingBackoffMs.Length - 1);
        var wait = (int)Math.Min(PendingBackoffMs[index], ShellBudgetMs - spent);

        request.Attempts++;
        request.DueAt = Environment.TickCount64 + Math.Max(wait, 1);

        lock (DelayGate)
        {
            Delayed.Add(request);
        }

        return true;
    }

    /// <summary>取出一个已经到期的重试请求；没有就返回 null。</summary>
    private static IconRequest? TakeDue()
    {
        lock (DelayGate)
        {
            if (Delayed.Count == 0)
            {
                return null;
            }

            var now = Environment.TickCount64;
            var bestIndex = -1;
            var bestDue = long.MaxValue;

            for (var i = 0; i < Delayed.Count; i++)
            {
                if (Delayed[i].DueAt < bestDue)
                {
                    bestDue = Delayed[i].DueAt;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0 || bestDue > now)
            {
                return null;
            }

            var request = Delayed[bestIndex];
            Delayed.RemoveAt(bestIndex);
            return request;
        }
    }

    /// <summary>取一次图标。pending 为 true 表示 shell 返回 E_PENDING，可以稍后重试。</summary>
    private static BitmapSource? TryExtractOnce(string path, int size, out bool pending)
    {
        pending = false;

        try
        {
            if (!FsUtil.PathExists(path))
            {
                return null;
            }

            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, IidShellItemImageFactory, out var factory);
            try
            {
                var hr = factory.GetImage(
                    new SIZE(size, size),
                    SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK,
                    out var hBitmap);

                // 0x8000000A = E_PENDING：Shell 还在准备图形资源
                pending = hr == unchecked((int)0x8000000A);

                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"取图标失败 {path}: {ex.Message}");
            return null;
        }
    }

    private static string ModeKey(string key, bool allowShell) => allowShell ? key : key + "|warm";

    // ── 统计与批量日志 ─────────────────────────────────────────────────

    private static void NoteRequest()
    {
        lock (BatchGate)
        {
            if (!_batchActive)
            {
                _batchActive = true;
                _batchCount = 0;
                _batchMemory = 0;
                _batchDisk = 0;
                _batchShell = 0;
                _batchElapsedMs = 0;
                BatchWatch.Restart();
            }

            _batchCount++;

            // 这一批还没结算，先撤掉待发的日志
            BatchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private static void NoteSource(HitSource source)
    {
        lock (BatchGate)
        {
            switch (source)
            {
                case HitSource.Memory:
                    Interlocked.Increment(ref _memoryHits);
                    if (_batchActive)
                    {
                        _batchMemory++;
                    }

                    break;

                case HitSource.Disk:
                    Interlocked.Increment(ref _diskHits);
                    if (_batchActive)
                    {
                        _batchDisk++;
                    }

                    break;

                case HitSource.Shell:
                    Interlocked.Increment(ref _shellHits);
                    if (_batchActive)
                    {
                        _batchShell++;
                    }

                    break;
            }
        }
    }

    /// <summary>没有在排队的请求了就推迟结算：再等一小会儿，看还有没有新请求并进来。</summary>
    private static void NoteDrained()
    {
        lock (BatchGate)
        {
            if (!_batchActive)
            {
                return;
            }

            _batchElapsedMs = BatchWatch.ElapsedMilliseconds;

            if (Volatile.Read(ref _pending) == 0)
            {
                BatchTimer.Change(BatchQuietMs, Timeout.Infinite);
            }
        }
    }

    private static void FlushBatch()
    {
        int count, memory, disk, shell;
        long elapsed;

        lock (BatchGate)
        {
            if (!_batchActive)
            {
                return;
            }

            count = _batchCount;
            memory = _batchMemory;
            disk = _batchDisk;
            shell = _batchShell;
            elapsed = _batchElapsedMs;

            _batchActive = false;
            _batchCount = 0;
            _batchMemory = 0;
            _batchDisk = 0;
            _batchShell = 0;
        }

        // 零星单个图标不记，避免刷屏；批量加载或明显卡顿的才留一条
        if (count >= 2 || elapsed >= 600)
        {
            var sinceStart = StartupTrace.IsStartup
                ? $"，自进程启动 {StartupTrace.SinceProcessStartMs}ms"
                : string.Empty;
            Log.Info($"[图标] {count} 项 内存命中 {memory} / 磁盘命中 {disk} / shell {shell} / 耗时 {elapsed}ms{sinceStart}");
        }
    }

    private enum HitSource
    {
        Memory,
        Disk,
        Shell,
    }

    private sealed class IconRequest
    {
        public required string Key { get; init; }

        public required string Path { get; init; }

        public int Size { get; init; }

        public bool AllowShell { get; init; }

        public long StartedAt { get; init; }

        public TaskCompletionSource<BitmapSource?> Completion { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>已经试过几次。</summary>
        public int Attempts { get; set; }

        /// <summary>早于这个时刻（TickCount64）不重试。</summary>
        public long DueAt { get; set; }
    }
}
