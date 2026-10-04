using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using ColorfulLedKeyboard.Core;

namespace ColorfulLedKeyboard.Simulator;

/// <summary>
/// 外接控制命名管道服务端：接收 Zones 服务经 <see cref="SimulatorPipeTransport"/> 转发的
/// DCHU 命令，让本模拟器作为"虚拟键盘"被主项目软件驱动。
/// 同一时刻至多一个客户端（服务进程唯一）；顺序 accept，客户端断开后回到等待。
/// 协议（UTF-8 按行）见 SimulatorPipeTransport。全部回调经 Dispatcher 编排回 UI 线程。
/// </summary>
internal sealed class ExternalControlServer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<int, int> _onCommand;              // (command, args) → 渲染
    private readonly Func<int, int> _onQuery;                  // command → 读命令结果
    private readonly Action<bool, long> _onStatus;             // (clientConnected, totalCommands)
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeServerStream? _serving;
    private Task? _acceptLoop;
    private long _totalCommands;
    private bool _disposed;

    public ExternalControlServer(Action<int, int> onCommand, Func<int, int> onQuery, Action<bool, long> onStatus)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onCommand = onCommand;
        _onQuery = onQuery;
        _onStatus = onStatus;
    }

    public static string PipeName => SimulatorPipeTransport.DefaultPipeName;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _acceptLoop ??= Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _serving = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (IOException)
            {
                // 客户端在等待期间断开：回到顶部重建服务端实例
                pipe.Dispose();
                continue;
            }

            Notify(connected: true);
            try
            {
                await ServeClientAsync(pipe, ct);
            }
            catch
            {
                // 客户端断开/进程退出导致的 IO 异常：统一走下方清理
            }
            finally
            {
                try { if (pipe.IsConnected) { pipe.Disconnect(); } } catch { }
                pipe.Dispose();
            }

            Notify(connected: false);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: 256, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        while (!ct.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                return; // 客户端关闭连接
            }

            if (SimulatorPipeTransport.TryParseRequestLine(line, out var isWrite, out var command, out var args))
            {
                if (isWrite)
                {
                    var total = Interlocked.Increment(ref _totalCommands);
                    _dispatcher.BeginInvoke(_onCommand, command, args);
                    Notify(connected: true, total);
                    await writer.WriteLineAsync(SimulatorPipeTransport.FormatOkReply());
                }
                else
                {
                    // 读命令同步求值：只读视图开关等 volatile 状态，无 UI 依赖
                    var result = unchecked((int)_onQuery(command));
                    await writer.WriteLineAsync(SimulatorPipeTransport.FormatValueReply(result));
                }
            }
            else
            {
                await writer.WriteLineAsync(SimulatorPipeTransport.FormatErrorReply("bad request"));
            }
        }
    }

    private void Notify(bool connected) => Notify(connected, Interlocked.Read(ref _totalCommands));

    private void Notify(bool connected, long total) =>
        _dispatcher.BeginInvoke(_onStatus, connected, total);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _serving?.Dispose(); } catch { } // 打断等待连接/读取中的异步调用
        _stop.Cancel();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
    }
}
