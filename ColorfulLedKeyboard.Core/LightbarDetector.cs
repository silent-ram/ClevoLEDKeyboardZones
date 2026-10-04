using Microsoft.Win32;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 灯带（第 4 区，0xF3）机型探测：按 SystemProductName / BaseBoardProduct 前缀匹配。
/// 机型表来源：clevo-xsm-wmi DMI 表的 <c>kb_full_color_with_extra_ops</c> 条目
/// （docs/reverse-engineering/dchu-protocol-findings.md 第九节 9.4；
/// 归档源码 third-party-reference/module_clevo-xsm-wmi.c），后续按用户反馈追加。
/// 覆盖说明：6 条按产品名匹配；第 7 条（ECT 定制板）驱动按 DMI_BOARD_NAME 匹配，
/// 故本实现同时读取 BaseBoardProduct。读取失败一律按"无灯带"处理——与三区门控同方向的安全默认。
/// </summary>
public static class LightbarDetector
{
    /// <summary>
    /// 带灯带机型的名称前缀（大小写不敏感），同时匹配 SystemProductName 与 BaseBoardProduct。
    /// </summary>
    private static readonly string[] LightbarProductPrefixes =
    [
        "P870DM",
        "P7xxDM",                  // P7xxDM(-G) 及其变体
        "P750ZM",
        "P17SM-A",
        "Deimos/Phobos 1x15S",     // P7xxDM(-G) 非标准命名
        "P5 Pro SE",               // P750ZM 非标准命名
    ];

    private const string BiosKeyPath = @"HARDWARE\DESCRIPTION\System\BIOS";

    /// <summary>读取本机 SystemProductName / BaseBoardProduct 并做灯带机型匹配；任何异常按 false。</summary>
    public static bool HasLightbar()
    {
        try
        {
            using var biosKey = Registry.LocalMachine.OpenSubKey(BiosKeyPath);
            var productName = biosKey?.GetValue("SystemProductName") as string;
            if (MatchesLightbar(productName))
            {
                return true;
            }

            // ECT 定制机型：驱动按 DMI_BOARD_NAME 匹配，产品名不可依赖
            var boardProduct = biosKey?.GetValue("BaseBoardProduct") as string;
            return MatchesLightbar(boardProduct);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>纯匹配函数（测试用）：productName 为 null/空白按 false。</summary>
    public static bool MatchesLightbar(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return false;
        }

        foreach (var prefix in LightbarProductPrefixes)
        {
            if (productName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
