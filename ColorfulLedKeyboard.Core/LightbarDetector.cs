using Microsoft.Win32;

namespace ColorfulLedKeyboard.Core;

/// <summary>
/// 灯带（第 4 区，0xF3）机型探测：按 SystemProductName 前缀匹配。
/// 机型表来源：clevo-xsm-wmi DMI 表的 <c>kb_full_color_with_extra_ops</c> 条目
/// （docs/reverse-engineering/dchu-protocol-findings.md 第九节 9.4；
/// 归档源码 third-party-reference/module_clevo-xsm-wmi.c），后续按用户反馈追加。
/// 读取失败一律按"无灯带"处理——与三区门控同方向的安全默认。
/// </summary>
public static class LightbarDetector
{
    /// <summary>
    /// 带灯带机型的 SystemProductName 前缀（大小写不敏感）。
    /// 覆盖归档 DMI 表全部 7 条：P870DM / P7xxDM(-G)（含 Deimos/Phobos 1x15S）/
    /// P750ZM（含 P5 Pro SE 与 ECT 定制板）/ P17SM-A。
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

    /// <summary>读取本机 SystemProductName 并做灯带机型匹配；任何异常按 false。</summary>
    public static bool HasLightbar()
    {
        try
        {
            var productName = (string?)Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")
                ?.GetValue("SystemProductName");
            return MatchesLightbar(productName);
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
