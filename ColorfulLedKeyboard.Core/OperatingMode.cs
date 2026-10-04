namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 顶层运行模式：表示用户当前希望键盘走灯效路径还是音乐响应路径。
/// 与 <see cref="EffectType"/> 是不同维度——前者是"模式"，后者是"灯效"。
/// 当 <see cref="Music"/> 时 Worker 走 RunMusicAsync；当 <see cref="Lighting"/> 时走 RunEffectAsync。
/// </summary>
public enum OperatingMode
{
    /// <summary>灯效模式（默认）：键盘按 EffectType 显示静态色/呼吸/RGB 循环等动画。</summary>
    Lighting = 0,

    /// <summary>音乐模式：键盘根据系统音频电平动态变化，参数由 EffectSettings.Music 配置。</summary>
    Music = 1,

    /// <summary>
    /// 多分区模式（实验）：左/中/右/灯带各自按 MultiZone.Zones 的灯效配置独立渲染，
    /// 经能力位门控（未命中回退灯效管线）；灯带不做机型检测，由 IncludeLightbar 手动开关。
    /// 注意：主仓库（单区版）没有此枚举值——旧服务读到会按未知值回退灯效模式，灯效参数
    /// 保持不变，因此与主仓库共享 settings.json 是安全的。
    /// </summary>
    MultiZone = 2
}
