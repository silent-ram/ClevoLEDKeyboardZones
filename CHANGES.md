# Changes

本文件记录本 fork 相对上游 [xuha233/ClevoRGBControl](https://github.com/xuha233/ClevoRGBControl) 的修改，遵循 GPL-3.0 第 5(a) 条要求。

## 2026-10-05 — v1.0.0：多分区支持

本 fork 首个版本（基于 fork 前主仓库 v3.5.0 代码），新增多分区键盘支持与配套基础设施：

- **多分区模式**：灯效设置页新增模式；左/中/右/灯带四区独立配置灯效（固定颜色/单色呼吸/RGB 循环/循环呼吸/脉冲/心跳/关闭）与颜色，整套配置支持命名预设（最多 8 个）。
- **分区协同效果**（多分区专属）：接力流动（颜色列表随时间轴循环流过四区）、氛围渐变（颜色列表映射键盘横向渐变 + 缓慢呼吸）；纯函数生成器，四区统一驱动。归一化守卫：协同值只存于 MultiZone，灯效管线零泄漏。
- **渲染布局**：三区+灯带（分区机型）/ 单区合并（单分区机型，走与灯效模式相同的单区路径）。
- **能力门控**：服务端按三区能力位（GET_BIOS_FEATURES_1 0x00400000）门控，未命中自动回退普通灯效管线；灯带（0xF3）不做机型检测，由用户开关决定。
- **独立托盘/服务**：多分区页（拾色器/颜色列表/参数滑条/预设栏/服务端状态行）、服务单实例互斥、模式变更日志。
- **基础设施**：模拟器外接控制与音乐模式（TCP 环回传输与分支 IPC 通道，规避本机 .NET 8 命名管道数据流拦截）；设置/状态文件可经 CLEVO_LED_SETTINGS_PATH 隔离；版本号启用独立 1.x 线（正式版保持 3.x 不动）。
- 移除灯带机型检测（DMI 前缀匹配）——灯带是否下发由调用方决定（docs/reverse-engineering 9.4 设计决策）。

已知限制见 README；单分区硬件上三区写入同址（表现为单色）为硬件限制。

## 2026-06-10 — Fork 起点

- 基线提交：`90d2438`（"Clean repository metadata"）。
- 重命名产品为 `ClevoLEDKeyboardControl`，涉及：
  - `ColorfulLedKeyboard.Core/AppPaths.cs`（服务名、显示名、ProgramData 目录名）。
  - `ColorfulLedKeyboard.Installer/Program.cs`（产品名、安装目录、注册表键、安装包文件名、legacy 服务名兼容）。
  - `ColorfulLedKeyboard.Tray/*`（关于框、设置框标题、托盘文本、消息框标题、Restart-Service 命令）。
  - `ColorfulLedKeyboard.Tray/UpdateChecker.cs`（GitHub Releases URL、User-Agent）。
  - `ColorfulLedKeyboard.Installer/app.manifest`、安装器 `AssemblyName`。
  - `scripts/install-service.ps1`、`scripts/restart-service.ps1`、`scripts/uninstall-service.ps1`、`scripts/publish.ps1`。
  - `README.md`、`RELEASE_NOTES.txt`。
- 安装器同时识别旧服务名 `ClevoRGBControlService` 与 `ColorfulLedKeyboardService`，保证老用户升级时自动清理。
- 移除随仓库分发的厂商驱动 `assets/driver/InsydeDCHU.dll`，改为在 README 与安装提示中说明从 OEM Control Center 获取。`.gitignore` 屏蔽 `assets/driver/*.dll`，构建脚本仍可在本地放置 DLL 后打包。
- 新增 `NOTICE`、`CHANGES.md`，明确 fork 来源、修改日期与第三方组件授权状态。
- .NET 命名空间和项目目录暂时保留 `ColorfulLedKeyboard` 前缀，避免一次性大规模重命名带来的构建风险，后续重构再统一调整。