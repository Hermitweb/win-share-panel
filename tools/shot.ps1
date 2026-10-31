param([int]$NavIndex = 4)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class SH {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]
$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$root = $AE::FromHandle($p.MainWindowHandle)
$cItem = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::ListItem)
for ($t = 0; $t -lt 10; $t++) {
  $items = $root.FindAll($TS::Descendants, $cItem)
  if ($items.Count -ge 6) { break }
  Start-Sleep -Milliseconds 800
  $root = $AE::FromHandle($p.MainWindowHandle)
}
if ($items.Count -lt 6) { Write-Output "NAV_FAIL"; exit 1 }
$items[$NavIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 2500

$r = New-Object SH+RECT
[SH]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $h = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [SH]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$bmp.Save((Join-Path $PSScriptRoot 'shot.png'))
Write-Output ("saved nav=" + $NavIndex + " ok=" + $ok + " " + $w + "x" + $h)
