# 打包手工分发测试版：3.6.0-zone.1
# 产出 dist\ClevoLEDKeyboardZones-3.6.0-zone.1-test.zip（不入库）
# 包内：Tray + Service（框架依赖发布）+ 守门人启动脚本（适配包内路径 + 驱动复制）+ 测试说明
# 不含厂商驱动 InsydeDCHU.dll（授权范围外）：启动脚本从测试者已装的正式版复制

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "3.6.0-zone.1"
$pkgName = "ClevoLEDKeyboardZones-$version-test"
$dist = Join-Path $root "dist"
$pkg = Join-Path $dist $pkgName

Write-Host "[1/5] stopping demo processes (file locks)..."
Get-Process ColorfulLedKeyboard.Service, ColorfulLedKeyboard.Tray, ColorfulLedKeyboard.Simulator -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "[2/5] publishing (framework-dependent, Release)..."
dotnet publish "$root\ColorfulLedKeyboard.Service\ColorfulLedKeyboard.Service.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "service publish failed" }
dotnet publish "$root\ColorfulLedKeyboard.Tray.Wpf\ColorfulLedKeyboard.Tray.Wpf.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "tray publish failed" }

Write-Host "[3/5] assembling package..."
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$pkg\Service" | Out-Null
New-Item -ItemType Directory -Force -Path "$pkg\Tray" | Out-Null
Copy-Item "$root\ColorfulLedKeyboard.Service\bin\Release\net8.0-windows\publish\*" "$pkg\Service\" -Recurse -Force
# 厂商驱动随包分发（仅测试用途；版权归厂商）。构建时已由 csproj 自动从本机复制到发布输出。
if (-not (Test-Path "$pkg\Service\InsydeDCHU.dll")) { throw "InsydeDCHU.dll missing from service publish output" }
Copy-Item "$root\ColorfulLedKeyboard.Tray.Wpf\bin\Release\net8.0-windows10.0.22621.0\publish\*" "$pkg\Tray\" -Recurse -Force

Write-Host "[4/5] writing launcher + readme..."
$launcher = @"
@echo off
rem ============================================================
rem  ClevoLEDKeyboardZones $version - multi-zone test build
rem  Manual test distribution. NOT a production release.
rem
rem  Requires: the official ClevoLEDKeyboardControl v3.5.0 installed
rem  (for the vendor driver InsydeDCHU.dll and the .NET 8 runtime).
rem
rem  This script: stops the installed production service, runs the
rem  ZONES service (multi-zone experimental) + zones tray, and on
rem  exit restarts the production service. Your production config
rem  is untouched - zones uses its own isolated settings file.
rem ============================================================
setlocal EnableExtensions
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Requesting administrator rights - please accept the UAC prompt...
  powershell -NoProfile -Command "try { Start-Process -FilePath '%~f0' -Verb RunAs } catch { Write-Host ('ELEVATION DECLINED: ' + $_.Exception.Message) }"
  exit /b
)
cd /d "%~dp0"
set SVC=%~dp0Service\ColorfulLedKeyboard.Service.exe
set TRAY=%~dp0Tray\ColorfulLedKeyboard.Tray.exe
set ISOLATED_DIR=%LOCALAPPDATA%\ClevoLEDKeyboardControlZones
set CLEVO_LED_SETTINGS_PATH=%ISOLATED_DIR%\settings.json

echo [1/5] stopping the installed production service...
sc stop ClevoLEDKeyboardControlService >nul 2>&1
ping -n 4 127.0.0.1 >nul

echo [2/5] checking vendor driver...
if not exist "%~dp0Service\InsydeDCHU.dll" (
  echo [FAIL] InsydeDCHU.dll missing from the Service folder - vendor driver is
  echo        required for real keyboard control. Obtain it from your OEM
  echo        Control Center and place it next to ColorfulLedKeyboard.Service.exe.
  pause
  exit /b 1
) else (
  echo       present.
)

echo [3/5] starting tray...
start "" "%TRAY%" --settings

echo [4/5] starting zones service in this console...
"%SVC%"

echo.
echo === test session ended - restoring production ===
taskkill /f /im ColorfulLedKeyboard.Service.exe >nul 2>&1
taskkill /f /im ColorfulLedKeyboard.Tray.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul
sc start ClevoLEDKeyboardControlService >nul
echo Production service restarted. Done.
pause
exit /b 0
"@
$launcher | Out-File "$pkg\multizone-test.cmd" -Encoding ascii

$readme = @"
ClevoLEDKeyboardZones $version - 多分区测试版（手工分发）
==========================================================

这是什么
--------
ClevoLEDKeyboardControl 的实验性多分区版本：三区+灯带独立灯效、
协同效果（接力流动/氛围渐变）、每区颜色列表与参数、整套配置预设。
基于正式版 v3.5.0 代码，仅用于向作者反馈多分区效果的测试。

重要前提
--------
- 仅限 Windows 10/11 x64 + .NET 8 Desktop Runtime
- 包内已含厂商驱动 InsydeDCHU.dll（仅供本次测试使用，版权归厂商）
- 本包不修改正式版程序与配置：Zones 使用独立的设置文件
  （%LOCALAPPDATA%\ClevoLEDKeyboardControlZones\settings.json），
  你的正式版配置全程不受影响

使用步骤
--------
1. 双击 multizone-test.cmd，UAC 弹窗点"是"
   （会临时停止已安装的正式版服务——如有——退出时自动恢复）
2. 设置窗口 → 灯效设置 → 模式选"多分区" → 多分区页配置各分区
   （布局选"单区合并"=单分区机型；"三区+灯带"=分区机型）
3. 保存并应用，键盘立即按配置渲染
4. 测试完关闭服务控制台窗口、退出托盘，正式版服务自动恢复；
   需要正式版托盘再运行：
   C:\Program Files\ClevoLEDKeyboardControl\ColorfulLedKeyboard.Tray.exe

多分区页功能速览
----------------
- 渲染布局：三区+灯带（分区机型）/ 单区合并（单分区机型）
- 每区效果：固定颜色/单色呼吸/RGB 循环/循环呼吸/脉冲/心跳/关闭
- 协同效果（多分区专属）：接力流动、氛围渐变——颜色列表随时间轴
  流过四区（配在任意区效果一致）
- 每区颜色：色板 + 自定义拾色器 + 颜色列表（循环类与渐变）
- 整套配置预设：保存/应用/删除命名预设（最多 8 个）
- 音乐模式与灯效模式与正式版一致

已知限制
--------
- 单分区硬件上"三区"写入同址（表现为单色），分区异色需三区机型
- 灯带（0xF3）不检测机型：无灯带机型请保持关闭
- 能力位只是必要条件：个别单分区机型也会置位（表现同上）
- 测试版请勿日常使用；数据与正式版隔离，随时可退回

反馈
----
把现象 + 服务控制台日志（PID 与 Active mode 行）发给作者即可。
"@
$readme | Out-File "$pkg\测试说明.txt" -Encoding utf8

Write-Host "[5/5] zipping..."
$zip = Join-Path $dist "$pkgName.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "DONE: $zip ($size MB)"
