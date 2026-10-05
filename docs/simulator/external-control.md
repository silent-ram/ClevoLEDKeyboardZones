# 外接控制：主项目软件 → 虚拟键盘模拟器

> 实验功能（experimental fork 限定）。让模拟器充当一块"虚拟键盘"，由本仓库的服务
> （即主项目的效果管线）经 TCP 环回（127.0.0.1:47820）驱动渲染，全程**零真实 EC 写入**。
> 版本约定 3.6.0-zone.1，与主仓库错开。

## 用法

```powershell
# 1. 构建并启动模拟器（默认勾选"外接控制"）
dotnet build ColorfulLedKeyboard.Simulator -c Release
ColorfulLedKeyboard.Simulator\bin\Release\net8.0-windows\ColorfulLedKeyboard.Simulator.exe

# 2. 以外接模式启动服务（控制台即可，无需管理员；不要与已安装服务同名注册）
$env:CLEVO_LED_SIMULATOR_PIPE = "1"
dotnet run --project ColorfulLedKeyboard.Service
```

服务渲染的每一帧会实时出现在模拟器键盘上（三区列 + 灯带同色）；用主项目托盘/设置
切换效果、亮度，模拟器同步跟随。模拟器侧状态：

| 状态 | 显示 | 行为 |
| --- | --- | --- |
| 等待连接 | "外接：等待服务连接" | 内置演示可正常启停 |
| 服务已连接 | "外接：服务已连接，正在接管渲染" | 内置演示自动暂停，播放/步进按钮锁定 |
| 断开 | 回到等待 | 内置演示恢复（若此前在运行），按钮解锁 |

## 行为与门控

- **服务侧**（`CLEVO_LED_SIMULATOR_PIPE=1` 时）：
  - `DchuKeyboardDevice.CreateDefault()` 选择 `SimulatorPipeTransport`（TCP 环回客户端）替代 P/Invoke；
  - `Worker.RenderFrame` 改走分区路径直写：三区（0xF0/0xF1/0xF2）+ 灯带（0xF3）同色。
    分区 API 的能力位探测门（`Has3ZoneKeyboard`）在此分支**不生效**——模拟器按当前视图
    如实应答 0x52 探测（单区视图答"不支持"），探测门会把演示挡死；环境变量本身就是门控。
  - 生产路径（未设环境变量）逐字节保持既有行为：`SetColor` 三槽位写，绝不出现分区命令。
    真实三区机型的效果管线属后续工作（见 dchu-protocol-findings.md 9.9：能力位仅必要条件，
    不得作为运行时门控）。
- **服务侧 IPC 托管在 TCP 环回分支通道** 127.0.0.1:47821（标准命名管道被已安装主服务
  占用，且本机环境会拦截新建 .NET 8 命名管道的数据流，TCP 不受影响）。客户端按
  "TCP 分支通道 → 标准命名管道"顺序尝试：分支通道无监听时 connect 立即失败，主仓库
  托盘/服务不受影响。托盘的设置保存经 IPC 由本服务落盘（管理员权限不受 ProgramData
  目录 ACL 限制）并即时生效。
- **模拟器侧**：TCP 服务端随窗口创建启动；断连自动回到等待，服务端重启自动重连。

> ⚠ 不要把隔离目录的 settings.json 手动拷贝到生产 ProgramData：多分区/协同效果的
> 枚举字符串是本仓库扩展，旧版解析器无法识别，会判损坏并整体重置配置。

## 线路协议（UTF-8 按行，TCP 环回 127.0.0.1:47820）

| 方向 | 行 | 含义 |
| --- | --- | --- |
| 客户端 → 服务端 | `W cmdHex argsHex8` | 写一条 DCHU 命令（如 `W 67 F2FFFFFF`） |
| 客户端 → 服务端 | `Q cmdHex` | 读一条 DCHU 命令（如 `Q 52`） |
| 服务端 → 客户端 | `OK` | 写入成功（ACK） |
| 服务端 → 客户端 | `V valueHex8` | 读命令返回值（如 `V 00400000`） |
| 服务端 → 客户端 | `ERR message` | 协议错误 |

编解码纯函数在 `ColorfulLedKeyboard.Core/SimulatorPipeTransport.cs`（`Format*`/`TryParse*`），
客户端与服务端共用，单测覆盖（`SimulatorPipeTransportTests`）。

可靠性语义：模拟器未启动时写入**静默丢弃**（`DroppedWrites` 计数，不抛异常——服务效果
循环的 DllNotFoundException 重试路径只针对真实驱动缺失）；读取返回 0x80000002（不支持，
非三区安全默认）；断连后下次调用自动重连重试一次；每条写命令等 ACK（本地往返，天然限速）。

## 已知环境限制（开发机排查记录，2026-10-04）

本机环境（2026-10-05 实测，用户自启进程同样命中）会对**新建 .NET 8 命名管道**的数据流
进行拦截：连接能建立但数据写入在内核层永久停滞（`WriteFile` 不返回）；PowerShell 5.1
进程之间、TCP 环回、以及与启动较早的进程之间不受影响。这是环境级过滤行为，与本项目
代码无关——同一份代码的协议编解码、连接状态机在单测与
UIA 功能测试（T8）中全部通过，且与生产 IPC（`ClevoLEDKeyboardControl.v2`）使用完全相同的
`StreamReader/StreamWriter` 原语。因此：

- 单测刻意不建真实管道回环（同 `ServiceIpcTests` 只测纯解析的先例）；
- `functional-test.ps1` 的 T8 只验证连接状态机（连接/接管锁定/断开回退，零数据传输）；
- 传输层已改为 TCP 环回后，数据流不再依赖命名管道。
