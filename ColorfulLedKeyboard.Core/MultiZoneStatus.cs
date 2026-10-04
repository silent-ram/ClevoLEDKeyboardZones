using System.Text.Json;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 多分区模式的服务端状态（服务写、托盘读）：能力位是否命中、当前是否在多分区渲染。
/// 进入/退出多分区循环与门控回退时写入；托盘多分区页加载与定时刷新时读取。
/// 读取优先走 IPC（服务在运行时文件可能尚未落盘），与 AutomationStatus 同模式。
/// </summary>
public sealed class MultiZoneStatus
{
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>能力位（GET_BIOS_FEATURES_1 的 0x00400000）是否命中。未命中即回退普通灯效。</summary>
    public bool CapabilityDetected { get; set; }

    /// <summary>当前是否正在多分区渲染循环中。</summary>
    public bool Active { get; set; }

    public void Save()
    {
        UpdatedUtc = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(AppPaths.ProgramDataDirectory);
            var temporary = AppPaths.MultiZoneStatusPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, AppPaths.MultiZoneStatusPath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static MultiZoneStatus? Load()
    {
        if (Environment.UserInteractive &&
            ServiceIpc.TryRequest<object, MultiZoneStatus>("GetMultiZoneStatus", new { }, out var remote, 350))
        {
            return remote;
        }

        try
        {
            return File.Exists(AppPaths.MultiZoneStatusPath)
                ? JsonSerializer.Deserialize<MultiZoneStatus>(File.ReadAllText(AppPaths.MultiZoneStatusPath))
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
