namespace ColorfulLedKeyboard.Core;

public enum EffectType
{
    Static = 0,
    Rainbow = 1,
    Breathing = 2,
    Sequence = 3,
    Off = 4,
    // 5 = 已废弃的 Music 灯效（以前 EffectType.Music）。
    // 现在音乐由 OperatingMode.Music 表示，不再属于 EffectType。迁移逻辑见 KeyboardSettings.Normalize。
    Pulse = 6,
    Heartbeat = 7,

    // ---- 多分区协同效果（仅 MultiZone 渲染消费；普通灯效管线的 LightingFrameGenerator
    //      不认识这些值——KeyboardSettings.Normalize 会把它们收敛为 Static，因此不会泄漏进
    //      灯效模式。区域语义：该效果配在哪个区都一样，四区由 Worker 的协同生成器统一驱动）----

    /// <summary>接力流动：同一条色相时间轴，左→中→右依次相位偏移，灯带取补色（单分区硬件上表现为色相摆动）。</summary>
    RelayFlow = 100,

    /// <summary>氛围渐变：左暖右冷（可自定义两端色），中间线性插值，灯带取中间色，缓慢呼吸。</summary>
    AmbientGradient = 101
}
