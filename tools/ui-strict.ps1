# 严格切页测试（精简版）：每页验证自己的标记出现 + 重复点击仍正常
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FG {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
# 不主动置前台：实测 SetForegroundWindow 反而会让 UIA 树枚举为空（冒烟测试不置前台就正常）
# WPF 的 UIA 树会间歇性枚举为空（与是否前台无关）——重试直到拿到侧栏项
$root = $AE::FromHandle($p.MainWindowHandle)
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
$items = $null
for ($try = 1; $try -le 12; $try++) {
  Start-Sleep -Milliseconds 900
  try { $items = $root.FindAll($TS::Descendants, $cItem) } catch { $items = $null }
  if ($items -and $items.Count -ge 6) { break }
  $root = $AE::FromHandle($p.MainWindowHandle)   # 重新取根节点
}
$navCount = if ($items) { $items.Count } else { 0 }
Write-Output ("NAV=" + $navCount + " (尝试 " + $try + " 次)")

$names   = @('仪表盘','共享管理','用户权限','会话监控','服务配置','应用设置')
$markers = @('共享总数','新建共享','本地用户','刷新间隔','一键诊断','查看 releases')

function Has($needle) {
  foreach ($ct in @($CT::Text, $CT::Button)) {
    try {
      $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct)
      foreach ($t in $root.FindAll($TS::Descendants, $c)) {
        if ($t.Current.Name -and $t.Current.Name.Contains($needle)) { return $true }
      }
    } catch {}
  }
  return $false
}

$fail = 0
Write-Output "=== 单次点击 ==="
foreach ($i in 0..5) {
  try { $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
  Start-Sleep -Milliseconds 1500
  $own = Has $markers[$i]
  if (-not $own) { $fail++ }
  Write-Output ("  [" + $(if($own){'PASS'}else{'FAIL'}) + "] " + $names[$i])
}

Write-Output "=== 重复点击 3 次 ==="
foreach ($i in 0..5) {
  for ($k = 0; $k -lt 3; $k++) {
    try { $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
    Start-Sleep -Milliseconds 400
  }
  Start-Sleep -Milliseconds 1000
  $own = Has $markers[$i]
  if (-not $own) { $fail++ }
  Write-Output ("  [" + $(if($own){'PASS'}else{'FAIL'}) + "] " + $names[$i])
}

Write-Output ""
if ($fail -eq 0) { Write-Output "ALL PASS" } else { Write-Output ("FAILURES=" + $fail) }
exit $fail
