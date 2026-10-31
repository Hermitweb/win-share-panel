# WPF 面板自动化冒烟测试（UI Automation 驱动）
# 用法：pwsh -NoProfile -File tools/ui-smoke.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

function Get-Win {
  $p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue |
       Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $p) { return $null }
  return $p
}

$proc = Get-Win
if (-not $proc) { Write-Output "FAIL: 找不到 WinShare.App 窗口"; exit 1 }
$root = $AE::FromHandle($proc.MainWindowHandle)

function Count-By($ct) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct)
  return $root.FindAll($TS::Descendants, $c).Count
}
function Has-Text($needle) {
  # 按具体控件类型搜名字：页面标记可能是 Text，也可能是 Button/TabItem 的文案。
  # 不用 TrueCondition 全树枚举 —— 应用设置页的 UIA provider 会在全树 FindAll 时抛异常。
  foreach ($ct in @($CT::Text, $CT::Button, $CT::TabItem, $CT::CheckBox, $CT::RadioButton)) {
    try {
      $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct)
      foreach ($t in $root.FindAll($TS::Descendants, $c)) {
        if ($t.Current.Name -and $t.Current.Name.Contains($needle)) { return $true }
      }
    } catch {}
  }
  return $false
}

# 侧栏导航项
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
$items = $root.FindAll($TS::Descendants, $cItem)
Write-Output ("NAV_ITEMS=" + $items.Count)
if ($items.Count -ne 6) { Write-Output "FAIL: 侧栏项数不是 6"; exit 1 }

$pages = @(
  @{ Idx = 0; Name = '仪表盘';   Marker = '共享总数' },
  @{ Idx = 1; Name = '共享管理'; Marker = '新建共享' },
  @{ Idx = 2; Name = '用户权限'; Marker = '本地用户' },
  @{ Idx = 3; Name = '会话监控'; Marker = '刷新间隔' },
  @{ Idx = 4; Name = '服务配置'; Marker = '一键诊断' },
  @{ Idx = 5; Name = '应用设置'; Marker = '版本号' }
)

$fail = 0
Write-Output ""
Write-Output "=== 逐页功能测试 ==="
foreach ($pg in $pages) {
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  try {
    $items[$pg.Idx].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  } catch {
    Write-Output ("  [FAIL] " + $pg.Name + " 无法选中: " + $_.Exception.Message)
    $fail++; continue
  }
  Start-Sleep -Milliseconds 1200
  $sw.Stop()
  $ok = Has-Text $pg.Marker
  $texts = Count-By $CT::Text
  $btns = Count-By $CT::Button
  $edits = Count-By $CT::Edit
  $grids = Count-By $CT::DataGrid
  $chk = Count-By $CT::CheckBox
  $state = if ($ok) { 'PASS' } else { $fail++; 'FAIL' }
  Write-Output ("  [" + $state + "] " + $pg.Name.PadRight(6) +
    " 标记=" + $pg.Marker.PadRight(6) +
    " Text=" + $texts + " Button=" + $btns + " Edit=" + $edits +
    " Grid=" + $grids + " CheckBox=" + $chk +
    " 切换=" + $sw.ElapsedMilliseconds + "ms")
}

Write-Output ""
Write-Output "=== 快速切换压力测试（30 次随机切页）==="
$rand = New-Object System.Random 42
$stressFail = 0
$swAll = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 30; $i++) {
  $idx = $rand.Next(0, 6)
  try {
    $items[$idx].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  } catch { $stressFail++ }
  Start-Sleep -Milliseconds 220
}
$swAll.Stop()
$proc.Refresh()
$memAfter = [math]::Round($proc.WorkingSet64 / 1MB)
$respond = $proc.Responding
Write-Output ("  切换 30 次耗时=" + $swAll.ElapsedMilliseconds + "ms  失败=" + $stressFail +
              "  Responding=" + $respond + "  内存=" + $memAfter + "MB")
if ($stressFail -gt 0 -or -not $respond) { $fail++ }

Write-Output ""
Write-Output "=== 结果 ==="
if ($fail -eq 0) { Write-Output "ALL PASS" } else { Write-Output ("FAILURES=" + $fail) }
exit $fail
