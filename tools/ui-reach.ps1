# 可达性诊断：每个页面有多少控件 BoundingRectangle 为空（=未布局/不可见）
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FG2 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
[FG2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[FG2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 900
$root = $AE::FromHandle($p.MainWindowHandle)
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
$items = $root.FindAll($TS::Descendants, $cItem)
Write-Output ("NAV=" + $items.Count)

$names = @('仪表盘','共享管理','用户权限','会话监控','服务配置','应用设置')
foreach ($i in 0..5) {
  try { $items[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
  Start-Sleep -Milliseconds 1300
  $total = 0; $empty = 0; $maxBottom = 0
  foreach ($ct in @($CT::Text, $CT::Button, $CT::Edit, $CT::CheckBox, $CT::ComboBox, $CT::DataGrid)) {
    try {
      $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ct)
      foreach ($e in $root.FindAll($TS::Descendants, $c)) {
        $r = $e.Current.BoundingRectangle
        if ($r.IsEmpty) { $empty++ } else { if ($r.Bottom -gt $maxBottom) { $maxBottom = [int]$r.Bottom } }
        $total++
      }
    } catch {}
  }
  Write-Output ("  " + $names[$i] + "  控件=" + $total + "  尺寸为空=" + $empty + "  最低可见底边=" + $maxBottom + "px")
}
