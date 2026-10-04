using System.IO;
using System.IO.Pipes;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 实验用"虚拟驱动"传输：把 DCHU 命令经命名管道转发给模拟器进程（外接控制模式），
/// 模拟器作为一块虚拟键盘实时渲染，实现"主项目软件控制模拟器键盘"。
/// 仅当环境变量 <see cref="EnableEnvironmentVariable"/> 启用时由
/// <see cref="DchuKeyboardDevice.CreateDefault"/> 选用；生产路径（P/Invoke）完全不受影响。
///
/// <para>可靠性语义：模拟器未启动时写入静默丢弃（计数，不抛异常——服务效果循环的
/// DllNotFoundException 重试路径只针对真实驱动缺失，管道模式不该触发）；读取返回
/// 0x80000002（命令不支持）这一非三区安全默认，与测试假件默认值同源。断连后下次调用
/// 自动重连；每条写命令等待服务端 ACK（localhost 往返，微秒级，天然限速并暴露协议错位）。</para>
///
/// <para>线路协议（UTF-8，按行）：客户端→服务端 <c>W cmdHex argsHex8</c>（写）/
/// <c>Q cmdHex</c>（读）；服务端→客户端 <c>OK</c> / <c>V valueHex8</c> / <c>ERR msg</c>。
/// 服务端实现在模拟器项目 ExternalControlServer。</para>
/// </summary>
public sealed class SimulatorPipeTransport : IDchuTransport, IDisposable
{
    /// <summary>与模拟器 ExternalControlServer 约定的管道名。</summary>
    public const string DefaultPipeName = "ColorfulLedKeyboardZones.Simulator";

    /// <summary>启用外接模式的环境变量；值为 1 / true（不分大小写）时生效。</summary>
    public const string EnableEnvironmentVariable = "CLEVO_LED_SIMULATOR_PIPE";

    /// <summary>读取失败/不支持时的安全默认：0x80000002（非三区，门控回退单区路径）。</summary>
    private const int UnsupportedFeaturesResult = unchecked((int)0x80000002u);

    private const int ConnectTimeoutMs = 200;
    private const int MaxAttemptsPerCall = 2;

    private readonly string _pipeName;
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _droppedWrites;
    private bool _disposed;

    public SimulatorPipeTransport(string? pipeName = null)
    {
        _pipeName = pipeName ?? DefaultPipeName;
    }

    /// <summary>外接模式是否已通过环境变量启用（供工厂与文档诊断使用）。</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnableEnvironmentVariable) is { } value
        && (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>因管道不通而丢弃的写命令计数（诊断用）。</summary>
    public long DroppedWrites => Interlocked.Read(ref _droppedWrites);

    // ---- 线路协议纯函数（客户端与服务端共用，单测覆盖；不涉及任何 I/O）----

    /// <summary>写命令行：W cmdHex argsHex8（args 固定 8 位十六进制，便于肉眼对齐协议文档）。</summary>
    public static string FormatWriteLine(int command, int args) => $"W {command:X} {(uint)args:X8}";

    /// <summary>读命令行：Q cmdHex。</summary>
    public static string FormatQueryLine(int command) => $"Q {command:X}";

    /// <summary>应答行：读成功 V valueHex8；写成功 OK；失败 ERR message。</summary>
    public static string FormatValueReply(int value) => $"V {(uint)value:X8}";
    public static string FormatOkReply() => "OK";
    public static string FormatErrorReply(string message) => $"ERR {message}";

    /// <summary>解析请求行 → (是写命令, command, args)。写：W cmd args；读：Q cmd；其余返回 false。</summary>
    public static bool TryParseRequestLine(string? line, out bool isWrite, out int command, out int args)
    {
        isWrite = false;
        command = 0;
        args = 0;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var parts = line.Split(' ');
        if (parts.Length == 3 && parts[0] == "W"
            && TryParseHex(parts[1], out var cmd) && TryParseHex(parts[2], out var arg))
        {
            isWrite = true;
            command = unchecked((int)cmd);
            args = unchecked((int)arg);
            return true;
        }

        if (parts.Length == 2 && parts[0] == "Q" && TryParseHex(parts[1], out var query))
        {
            isWrite = false;
            command = unchecked((int)query);
            return true;
        }

        return false;
    }

    /// <summary>解析读应答行："V valueHex8" → value；其余（OK/ERR/断连 null）返回 false。</summary>
    public static bool TryParseValueReply(string? line, out int value)
    {
        value = 0;
        if (line is null || !line.StartsWith("V ", StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryParseHex(line.Substring(2), out var raw))
        {
            return false;
        }

        value = unchecked((int)raw);
        return true;
    }

    // 严格十六进制：HexNumber 组合样式默认容忍前后空白，会放过协议错位，这里只留 AllowHexSpecifier
    private static bool TryParseHex(string text, out uint value) =>
        uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, null, out value);

    public void SetData(int command, int args)
    {
        if (!TryRoundTrip(FormatWriteLine(command, args), out var reply))
        {
            Interlocked.Increment(ref _droppedWrites);
        }
        else if (reply is null || reply.StartsWith("ERR", StringComparison.Ordinal))
        {
            // 断连重建后仍未送达（reply=null）或服务端拒绝（ERR）按丢弃处理：不重试，交由下一帧自然恢复
            Interlocked.Increment(ref _droppedWrites);
        }
    }

    public int GetInteger(int command)
    {
        return TryRoundTrip(FormatQueryLine(command), out var reply)
            && TryParseValueReply(reply, out var value)
            ? value
            : UnsupportedFeaturesResult;
    }

    /// <summary>发送一行并等待应答；断连时当次调用内重连重试一次（服务端重启场景）。</summary>
    private bool TryRoundTrip(string line, out string? reply)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            for (var attempt = 0; attempt < MaxAttemptsPerCall; attempt++)
            {
                if (!EnsureConnected())
                {
                    break;
                }

                try
                {
                    _writer!.WriteLine(line);
                    _writer.Flush();
                    reply = _reader!.ReadLine();
                    if (reply is null)
                    {
                        Disconnect(); // 对端已关闭：重建后再试
                        continue;
                    }

                    return true;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    Disconnect(); // 管道失效（服务端重启/断开）：重建后再试
                }
            }

            reply = null;
            return false;
        }
    }

    private bool EnsureConnected()
    {
        if (_pipe is { IsConnected: true })
        {
            return true;
        }

        Disconnect();
        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(ConnectTimeoutMs);
            _pipe = pipe;
            _reader = new StreamReader(pipe, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);
            _writer = new StreamWriter(pipe, System.Text.Encoding.UTF8, bufferSize: 256, leaveOpen: true)
            {
                AutoFlush = false,
                NewLine = "\n",
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 模拟器未启动：快速失败，命令丢弃，效果循环节奏不受影响
            return false;
        }
    }

    private void Disconnect()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Disconnect();
        }
    }
}
