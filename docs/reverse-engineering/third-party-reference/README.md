# 第三方参考实现归档（third-party reference archive）

本目录存放用于**交叉验证 DCHU 三区 / 灯带协议**的第三方 Linux 驱动源码副本。
仅作逆向工程参考资料归档：协议事实（编码常量、命令布局、时序）从这些实现核对；
C 代码一律**不复制**进本仓库的 C# 实现（`ColorfulLedKeyboard.Core` 的实现按
`dchu-protocol-findings.md` 第九节的规格独立编写）。

许可证：以各文件头部声明为准，登记如下。

## tuxedo-keyboard（TUXEDO Computers）

来源：<https://github.com/tuxedocomputers/tuxedo-keyboard>（`master` 分支 `src/` 目录）

| 文件 | 头部声明 | 版权 |
| --- | --- | --- |
| `clevo_leds.h` | GPL-3.0-or-later | (c) 2018-2020 TUXEDO Computers GmbH |
| `clevo_interfaces.h` | GPL-3.0-or-later | (c) 2020-2021 TUXEDO Computers GmbH |
| `clevo_keyboard.h` | GPL-3.0-or-later | (c) 2018-2020 TUXEDO Computers GmbH |
| `clevo_acpi.c` | GPL-3.0-or-later | (c) 2020 TUXEDO Computers GmbH |
| `clevo_wmi.c` | GPL-3.0-or-later | (c) 2020 TUXEDO Computers GmbH |

核对要点：`SUB_RGB_ZONE_0/1/2` 三区参数编码、CUSTOM 模式表、亮度范围（0..255 连续）。
常量定义位置：`clevo_interfaces.h` 64~69 行（`CLEVO_CMD_SET_KB_RGB_LEDS = 0x67`、
`SUB_RGB_ZONE_0..3 = 0xF0000000..0xF3000000`、`SUB_RGB_BRIGHTNESS = 0xF4000000`）。

## clevo-xsm-wmi（社区驱动）

来源：GitHub 保留分支 <https://github.com/rafaelgieschke/clevo-xsm-wmi>（`master` 分支 `module/` 目录）；
原版托管于 Bitbucket `lynthium/clevo-xsm-wmi`。

| 文件 | 头部声明 | 版权 |
| --- | --- | --- |
| `module_clevo-xsm-wmi.c` | GPL-2.0-or-later | (C) 2014-2016 Arnoud Willemsen、(C) 2013-2015 TUXEDO Computers GmbH 等 |

核对要点：`kb_full_color_with_extra_ops` 的静态应用时序（先模式 0x10000000 → 各分区颜色 → 亮度）、
亮度 4 档（63/126/189/252）、DMI 机型表中 `kb_full_color_with_extra_ops` 条目
（第 4 区 / 灯带存在的机型依据）、键盘总开关参数。

## 下载与校验（可复现）

```bash
BASE="https://raw.githubusercontent.com/tuxedocomputers/tuxedo-keyboard/master/src"
for f in clevo_acpi.c clevo_interfaces.h clevo_keyboard.h clevo_leds.h clevo_wmi.c; do
  curl -L -o "$f" "$BASE/$f"
done
curl -L -o module_clevo-xsm-wmi.c \
  "https://raw.githubusercontent.com/rafaelgieschke/clevo-xsm-wmi/master/module/clevo-xsm-wmi.c"
```

校验：每个文件非空（>1KB）；`grep -c "kb_full_color_with_extra_ops" module_clevo-xsm-wmi.c`
应 ≥ 1（当前 8 处）。2026-10-04 已按此流程重新下载并逐字节比对，与初版归档一致。

## 与本仓库的许可关系

上述文件按其头部声明原样归档，版权归属原作者。本仓库主体为 GPL-3.0，与 GPL-2.0-or-later /
GPL-3.0-or-later 的参考资料相互兼容；归档行为本身不改变本仓库许可证范围。
