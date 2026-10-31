Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]
$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$root = $AE::FromHandle($p.MainWindowHandle)
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
for ($t=0; $t -lt 10; $t++) { $items = $root.FindAll($TS::Descendants, $cItem); if ($items.Count -ge 6) { break }; Start-Sleep -Milliseconds 800; $root = $AE::FromHandle($p.MainWindowHandle) }
$items[5].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 2000
foreach ($ct in @('TabItem','Tab')) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::$ct)
  $els = $root.FindAll($TS::Descendants, $c)
  Write-Output ($ct + " count=" + $els.Count)
  foreach ($e in $els) {
    $r = $e.Current.BoundingRectangle
    Write-Output ("   '" + $e.Current.Name + "' rect=" + $r)
  }
}
