using ColorfulLedKeyboard.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ColorfulLedKeyboard.Service;

/// <summary>
/// IPC over TCP loopback（分支专用通道）。已安装的生产服务持有标准命名管道
/// （ClevoLEDKeyboardControl.v2）；本服务在 127.0.0.1:<see cref="ServiceIpc.ForkIpcPort"/>
/// 托管同一套 JSON 信封协议（长度前缀 + JSON，与管道版一致）。原因：本机环境会拦截
/// "新建 .NET 8 命名管道"的数据流（连接成功但数据滞留不出），TCP 不受影响。
/// 仅接受环回连接。客户端顺序：TCP 分支通道（本服务）→ 标准命名管道（生产服务），
/// 见 ServiceIpc.TryRequest。
/// </summary>
public sealed class ServiceIpcServer : IDisposable
{
    private readonly ILogger<ServiceIpcServer> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private Task? _loop;

    public ServiceIpcServer(ILogger<ServiceIpcServer> logger) => _logger = logger;

    public void Start() => _loop ??= Task.Run(() => AcceptLoopAsync(_stop.Token));

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, ServiceIpc.ForkIpcPort);
        listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPC accept failed");
                    continue;
                }

                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                if (!IsLoopback(client))
                {
                    using var unauthorized = client.GetStream();
                    await ReplyAsync(unauthorized, false, "Unauthorized client", false, cancellationToken);
                    return;
                }

                using var stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                var length = reader.ReadInt32();
                if (length <= 0 || length > ServiceIpc.MaximumMessageBytes)
                {
                    await ReplyAsync(stream, false, "Invalid message length", false, cancellationToken);
                    return;
                }
                var bytes = reader.ReadBytes(length);
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (!root.TryGetProperty("Version", out var version) || version.GetInt32() != ServiceIpc.ProtocolVersion ||
                    !root.TryGetProperty("Kind", out var kindElement) || !root.TryGetProperty("Payload", out var payload))
                {
                    await ReplyAsync(stream, false, "Invalid envelope", false, cancellationToken);
                    return;
                }
                var kind = kindElement.GetString() ?? "";
                switch (kind)
                {
                    case "Ping": await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    case "GetSettings":
                    {
                        await _settingsGate.WaitAsync(cancellationToken);
                        try { await ReplyAsync(stream, true, "", new SettingsStore().LoadLocal(), cancellationToken); }
                        finally { _settingsGate.Release(); }
                        break;
                    }
                    case "GetAutomationStatus":
                        await ReplyAsync(stream, true, "", ReadJson<AutomationStatus>(AppPaths.AutomationStatusPath), cancellationToken);
                        break;
                    case "GetMultiZoneStatus":
                        await ReplyAsync(stream, true, "", ReadJson<MultiZoneStatus>(AppPaths.MultiZoneStatusPath), cancellationToken);
                        break;
                    case "GetAudioApplications":
                        await ReplyAsync(stream, true, "", ReadJson<AudioApplicationsState>(AppPaths.AudioApplicationsStatePath), cancellationToken);
                        break;
                    case "GetMediaPlayback":
                        await ReplyAsync(stream, true, "", ReadJson<MediaPlaybackState>(AppPaths.MediaPlaybackStatePath), cancellationToken);
                        break;
                    case "GetForegroundState":
                        await ReplyAsync(stream, true, "", ReadJson<ForegroundAppState>(AppPaths.ForegroundAppStatePath), cancellationToken);
                        break;
                    case "GetAudioSourceStatus":
                        await ReplyAsync(stream, true, "", AudioSourceStatusFile.ReadFrom(AppPaths.AudioSourceStatusPath), cancellationToken);
                        break;
                    case "GetSettingsRecovery":
                        await ReplyAsync(stream, true, "", ReadJson<SettingsRecoveryState>(AppPaths.SettingsRecoveryStatePath), cancellationToken);
                        break;
                    case "RestoreLastGoodSettings":
                        await _settingsGate.WaitAsync(cancellationToken);
                        try
                        {
                            await ReplyAsync(stream, true, "", new SettingsStore().RestoreLastGoodLocal(), cancellationToken);
                        }
                        finally { _settingsGate.Release(); }
                        break;
                    case "SaveSettings":
                    {
                        var settings = payload.Deserialize<KeyboardSettings>();
                        if (settings is null) await ReplyAsync(stream, false, "Invalid settings", false, cancellationToken);
                        else
                        {
                            await _settingsGate.WaitAsync(cancellationToken);
                            try
                            {
                                new SettingsStore().SaveLocal(settings);
                                await ReplyAsync(stream, true, "", true, cancellationToken);
                            }
                            finally { _settingsGate.Release(); }
                        }
                        break;
                    }
                    case "ForegroundState": SaveJson(AppPaths.ForegroundAppStatePath, payload); await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    case "TypingPulse": SaveJson(AppPaths.TypingPulseStatePath, payload); await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    case "NotificationFlash": SaveJson(AppPaths.NotificationFlashStatePath, payload); await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    case "AudioApplications": SaveJson(AppPaths.AudioApplicationsStatePath, payload); await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    case "MediaPlayback": SaveJson(AppPaths.MediaPlaybackStatePath, payload); await ReplyAsync(stream, true, "", true, cancellationToken); break;
                    default: await ReplyAsync(stream, false, "Unsupported message kind", false, cancellationToken); break;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 客户端断开：正常
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC request failed");
            }
        }
    }

    private static bool IsLoopback(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static void SaveJson(string destination, JsonElement payload)
    {
        Directory.CreateDirectory(AppPaths.ProgramDataDirectory);
        var temp = $"{destination}.{Guid.NewGuid():N}.ipc.tmp";
        try
        {
            File.WriteAllText(temp, payload.GetRawText());
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static T? ReadJson<T>(string source)
    {
        try
        {
            return File.Exists(source) ? JsonSerializer.Deserialize<T>(File.ReadAllText(source)) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static async Task ReplyAsync<T>(Stream stream, bool success, string error, T payload, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new IpcReply<T>(success, error, payload));
        var prefix = BitConverter.GetBytes(bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _stop.Dispose();
    }
}
