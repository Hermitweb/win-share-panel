Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$root = $AE::FromHandle($p.MainWindowHandle)
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
for ($t=0; $t -lt 10; $t++) { $items = $root.FindAll($TS::Descendants, $cItem); if ($items.Count -ge 6) { break }; Start-Sleep -Milliseconds 800; $root = $AE::FromHandle($p.MainWindowHandle) }
if ($items.Count -lt 6) { Write-Output "NAV_FAIL"; exit 1 }

function CpuPct($proc, $seconds) {
  $t0 = $proc.TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Seconds $seconds
  $proc.Refresh()
  $t1 = $proc.TotalProcessorTime.TotalMilliseconds
  return [math]::Round(($t1 - $t0) / ($seconds * 1000 * 16) * 100, 2)
}

Write-Output ("空闲 CPU（15s）= " + (CpuPct $p 15) + "%")

# 反复访问仪表盘 10 次（每次离开再回来）
for ($k = 0; $k -lt 10; $k++) {
  $items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 350
  $items[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 350
}
Start-Sleep -Seconds 2
Write-Output ("访问 10 次后 CPU（15s）= " + (CpuPct $p 15) + "%")
$p.Refresh()
Write-Output ("内存 = " + [math]::Round($p.WorkingSet64/1MB) + "MB")
