# 虚拟键盘模拟器功能测试（UIA + 像素采样）
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File functional-test.ps1
# 前置：Release 已构建；测试全程经 UIA 驱动真实窗口，被测应用本身零真实 EC 写入（FakeDchuTransport）。
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\ColorfulLedKeyboard.Simulator.exe"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ("W32Cap" -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W32Cap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
}

$script:failures = 0
function Report($name, $ok, $detail) {
  $tag = if ($ok) { "PASS" } else { $script:failures++; "FAIL" }
  Write-Host ("[{0}] {1} - {2}" -f $tag, $name, $detail)
}

function Start-Sim([string[]]$appArgs = @()) {
  Get-Process ColorfulLedKeyboard.Simulator -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 400
  if ($appArgs.Count -gt 0) { $p = Start-Process -FilePath $exe -ArgumentList $appArgs -PassThru }
  else { $p = Start-Process -FilePath $exe -PassThru }
  Start-Sleep -Seconds 3
  if ($p.HasExited) { throw "simulator exited early" }
  return $p
}

function Find-ById($root, $id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke-Button($root, $id) {
  ($root | Where-Object { $false })
  $e = Find-ById $root $id
  if (-not $e) { throw "element not found: $id" }
  $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Check($root, $id, [bool]$on) {
  $e = Find-ById $root $id
  if (-not $e) { throw "element not found: $id" }
  $tp = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  $want = if ($on) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
  if ($tp.Current.ToggleState -ne $want) { $tp.Toggle(); Start-Sleep -Milliseconds 200 }
}
function Select-Effect($root, $itemName) {
  $combo = Find-ById $root "EffectBox"
  if (-not $combo) { throw "EffectBox not found" }
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 500
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $itemName)
  $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if (-not $item) { throw "combo item not found: $itemName" }
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 300
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
function Get-TextById($root, $id) {
  $e = Find-ById $root $id
  if ($e) { return $e.Current.Name }
  return ""
}
function Get-WindowBitmap($p) {
  [W32Cap]::SetProcessDPIAware() | Out-Null
  $r = New-Object W32Cap+RECT
  [W32Cap]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
  $w = [int]($r.Right - $r.Left); $h = [int]($r.Bottom - $r.Top)
  $bmp = New-Object System.Drawing.Bitmap($w, $h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
  $g.Dispose()
  return $bmp
}
# 三个分区列的采样点（占窗口比例）：列区 x 5%~40%、y 17%~49%；灯带 y ~56.5%
function Get-Pixels($bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  function Px($fx, $fy) { return $bmp.GetPixel([int]($w*$fx), [int]($h*$fy)) }
  return @{
    zone0 = Px 0.115 0.33
    zone1 = Px 0.230 0.33
    zone2 = Px 0.345 0.33
    lightbar = Px 0.42 0.565
  }
}
function IsDark($px) { return ($px.R -le 20 -and $px.G -le 24 -and $px.B -le 40) }
function Near($a, $b, $tol) { return ([math]::Abs($a.R-$b.R) -le $tol -and [math]::Abs($a.G-$b.G) -le $tol -and [math]::Abs($a.B-$b.B) -le $tol) }

$results = @()

# ============ T1 启动自检 ============
$p = Start-Sim
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$mode = Get-TextById $root "ModeText"
$btn = Get-TextById $root "StartButton"
$bmp = Get-WindowBitmap $p
$px = Get-Pixels $bmp
$colsDark = (IsDark $px.zone0) -and (IsDark $px.zone1) -and (IsDark $px.zone2)
Report "T1 启动自检" (($mode -eq "—") -and ($btn -like "*开始*") -and $colsDark) "模式='$mode' 按钮='$btn' 三列未点亮=$colsDark"
$bmp.Dispose()
Get-Process ColorfulLedKeyboard.Simulator -ErrorAction SilentlyContinue | Stop-Process -Force

# ============ T2 单色呼吸运行 ============
$p = Start-Sim
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Invoke-Button $root "StartButton"
Start-Sleep -Seconds 2
$mode = Get-TextById $root "ModeText"
$last = Get-TextById $root "LastCommandText"
$bmp = Get-WindowBitmap $p
$px = Get-Pixels $bmp
$colsLit = (-not (IsDark $px.zone0))
$same = Near $px.zone0 $px.zone1 3 -and (Near $px.zone1 $px.zone2 3)
Report "T2 单色呼吸运行" (($mode -like "*静态色*") -and ($last -like "*0x67 0xF2*") -and $colsLit -and $same) "模式='$mode' 命令='$last' 三列同色=$same"
$bmp.Dispose()

# ============ T3 固定颜色（端到端：UI 选择 → 命令字节 → 渲染像素） ============
Select-Effect $root "固定颜色"
Start-Sleep -Seconds 1
$mode = Get-TextById $root "ModeText"
$last = Get-TextById $root "LastCommandText"
$bmp = Get-WindowBitmap $p
$px = Get-Pixels $bmp
# 期望：#0080FF 经 70% 亮度缩放 = (0, 89, 178)（R*70/100 整除）
$expect = @{ R = 0; G = 89; B = 178 }
$ok = (Near $px.zone0 $expect 2) -and (Near $px.zone1 $expect 2) -and (Near $px.zone2 $expect 2)
Report "T3 固定颜色端到端" $ok "期望RGB=($($expect.R),$($expect.G),$($expect.B)) 实测=($($px.zone0.R),$($px.zone0.G),$($px.zone0.B)) 模式='$mode'"
$bmp.Dispose()

# ============ T4 暂停 ============
Invoke-Button $root "StartButton"
Start-Sleep -Milliseconds 300
$last1 = Get-TextById $root "LastCommandText"
Start-Sleep -Milliseconds 1200
$last2 = Get-TextById $root "LastCommandText"
$btn = Get-TextById $root "StartButton"
Report "T4 暂停停帧" (($last1 -eq $last2) -and ($btn -like "*开始*")) "命令稳定=$($last1 -eq $last2) 按钮='$btn'"

# ============ T5 关闭效果 ============
Select-Effect $root "关闭"
Invoke-Button $root "StartButton"
Start-Sleep -Seconds 1
$bmp = Get-WindowBitmap $p
$px = Get-Pixels $bmp
$allBlack = (IsDark $px.zone0) -and (IsDark $px.zone1) -and (IsDark $px.zone2)
Report "T5 关闭效果黑屏" $allBlack "三列全暗=$allBlack"
$bmp.Dispose()
Get-Process ColorfulLedKeyboard.Simulator -ErrorAction SilentlyContinue | Stop-Process -Force

# ============ T6 三区视图 + 灯带 ============
$p = Start-Sim @("--view-zones", "--force-lightbar")
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
Invoke-Button $root "StartButton"
Start-Sleep -Seconds 2
$mode = Get-TextById $root "ModeText"
$last = Get-TextById $root "LastCommandText"
$bmp = Get-WindowBitmap $p
$px = Get-Pixels $bmp
$independent = (-not (Near $px.zone0 $px.zone1 6)) -and (-not (Near $px.zone1 $px.zone2 6)) -and (-not (Near $px.zone0 $px.zone2 6))
$lightbarLit = -not (IsDark $px.lightbar)
Report "T6 三区独立着色+灯带" (($mode -like "*三区分区写入*") -and ($last -like "*0xF400*") -and $independent -and $lightbarLit) "三列互异=$independent 灯带点亮=$lightbarLit 模式='$mode'"
$bmp.Dispose()

# ============ T7 通道序检验（B/R 互换） ============
# 暂停 → 记录 zone0 → 勾选互换 → 帧步进(暂停态推进单帧，色相漂移<1°) → 比对 R/B 互换
Invoke-Button $root "StartButton"; Start-Sleep -Milliseconds 300  # 暂停，冻结 _elapsedMs
$bmp = Get-WindowBitmap $p; $before = (Get-Pixels $bmp).zone0; $bmp.Dispose()
Set-Check $root "SwapBrCheck" $true
Invoke-Button $root "StepButton"  # 帧步进 ×1：elapsed 前进一个 interval 后重渲染
Start-Sleep -Milliseconds 600
$bmp = Get-WindowBitmap $p; $after = (Get-Pixels $bmp).zone0; $bmp.Dispose()
$swapped = (Near @{R=$before.B; G=$before.G; B=$before.R} $after 6) -and (-not (Near $before $after 4))
Report "T7 通道序检验(B/R互换)" $swapped "前=($($before.R),$($before.G),$($before.B)) 后=($($after.R),$($after.G),$($after.B))"
Get-Process ColorfulLedKeyboard.Simulator -ErrorAction SilentlyContinue | Stop-Process -Force

# ============ 汇总 ============
Write-Host ""
if ($script:failures -eq 0) { Write-Host "功能测试全部通过" -ForegroundColor Green } else { Write-Host "失败 $script:failures 项" -ForegroundColor Red }
exit $script:failures
