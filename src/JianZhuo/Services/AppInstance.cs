using System.IO.Pipes;
using System.Text;

namespace JianZhuo.Services;

/// <summary>单实例 + 命令行转发（第二个实例把参数交给已在运行的实例）。</summary>
public sealed class AppInstance : IDisposable
{
    private const string MutexName = @"Local\JianZhuo.SingleInstance";
    private const string PipeName = "JianZhuo.Ipc";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsPrimary { get; private set; }

    public event Action<string[]>? CommandReceived;

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        IsPrimary = created;
        return created;
    }

    public void StartServer()
    {
        if (!IsPrimary)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    if (args.Length > 0)
                    {
                        CommandReceived?.Invoke(args);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("IPC 异常: " + ex.Message);
                    await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, token);
    }

    public static bool SendToPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.Write(string.Join('\n', args));
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (IsPrimary)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
