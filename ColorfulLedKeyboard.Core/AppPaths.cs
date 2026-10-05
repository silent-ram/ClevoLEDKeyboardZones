namespace ColorfulLedKeyboard.Core;

public static class AppPaths
{
    public const string ServiceName = "ClevoLEDKeyboardControlService";
    public const string DisplayName = "ClevoLEDKeyboardControl Service";
    public const string ProgramDataFolderName = "ClevoLEDKeyboardControl";
    public const string SettingsFileName = "settings.json";
    public const string UpdateStateFileName = "update-check.json";
    public const string ForegroundAppStateFileName = "foreground-app.json";
    public const string TypingPulseStateFileName = "typing-pulse.json";
    public const string NotificationFlashStateFileName = "notification-flash.json";
    public const string DriverComponentStateFileName = "driver-component.json";
    public const string AudioSourceStatusFileName = "audio-source-status.json";
    public const string AutomationStatusFileName = "automation-status.json";
    public const string MultiZoneStatusFileName = "multizone-status.json";
    public const string MediaPlaybackStateFileName = "media-playback.json";
    public const string AudioApplicationsStateFileName = "audio-applications.json";
    public const string SettingsRecoveryStateFileName = "settings-recovery.json";
    public const string UsageTelemetryStateFileName = "usage-telemetry.json";

    public static string ProgramDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProgramDataFolderName);

    public static string UserDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProgramDataFolderName);

    /// <summary>开发/演示隔离：CLEVO_LED_SETTINGS_PATH 指向自定义设置文件时，实验栈
    /// （本仓库托盘+服务）整体改用该文件，与已安装生产服务的 settings.json 彻底隔离——
    /// 生产服务的旧解析器会把 MultiZone 当损坏配置还原，共享同一路径必然互相覆盖。</summary>
    public const string SettingsPathEnvironmentVariable = "CLEVO_LED_SETTINGS_PATH";

    public static string SettingsPath =>
        Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(ProgramDataDirectory, SettingsFileName);

    /// <summary>不受 CLEVO_LED_SETTINGS_PATH 重定向影响的生产默认路径：用于判定"是否默认存储"
    /// （决定交互式保存走 IPC 还是本地写）。重定向激活时二者不等，保存走本地文件，
    /// 绝不回退生产 IPC 管道——堵住 MultiZone 设置泄漏进生产 settings.json 的路径。</summary>
    public static string DefaultSettingsPath => Path.Combine(ProgramDataDirectory, SettingsFileName);

    public static string UpdateStatePath => Path.Combine(UserDataDirectory, UpdateStateFileName);

    public static string ForegroundAppStatePath => Path.Combine(ProgramDataDirectory, ForegroundAppStateFileName);

    public static string TypingPulseStatePath => Path.Combine(ProgramDataDirectory, TypingPulseStateFileName);

    public static string NotificationFlashStatePath => Path.Combine(ProgramDataDirectory, NotificationFlashStateFileName);


    public static string DriverComponentStatePath => Path.Combine(ProgramDataDirectory, DriverComponentStateFileName);

    public static string AudioSourceStatusPath => Path.Combine(ProgramDataDirectory, AudioSourceStatusFileName);

    public static string AutomationStatusPath => Path.Combine(ProgramDataDirectory, AutomationStatusFileName);

    public static string MultiZoneStatusPath =>
        Environment.GetEnvironmentVariable(SettingsPathEnvironmentVariable) is { Length: > 0 } custom
            ? Path.Combine(Path.GetDirectoryName(custom)!, MultiZoneStatusFileName)
            : Path.Combine(ProgramDataDirectory, MultiZoneStatusFileName);


    public static string MediaPlaybackStatePath => Path.Combine(ProgramDataDirectory, MediaPlaybackStateFileName);

    public static string AudioApplicationsStatePath => Path.Combine(ProgramDataDirectory, AudioApplicationsStateFileName);
    public static string SettingsRecoveryStatePath => Path.Combine(ProgramDataDirectory, SettingsRecoveryStateFileName);

    public static string UsageTelemetryStatePath => Path.Combine(UserDataDirectory, UsageTelemetryStateFileName);
}
