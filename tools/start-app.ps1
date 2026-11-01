# 启动 WPF 构建并等待窗口就绪（UIA 冒烟脚本的前置步骤）。
# 仓库根从 git 推导（对 BOM 副本同样有效），未装 git 时退回脚本位置推导（tools/ 的上一级）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools/start-app.ps1 [-Configuration Debug]
param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
  $gitRoot = & git -C $PSScriptRoot rev-parse --show-toplevel 2>$null
  if ($LASTEXITCODE -eq 0 -and $gitRoot) { return $gitRoot }
  return Split-Path -Parent $PSScriptRoot
}
$root = Get-RepoRoot
# exe 路径动态发现：输出目录随 TFM 变化（如 net10.0-windows10.0.17763.0），
# 且本机 Platform=x64 环境变量会出现 bin/x64 变体——取 Debug 下最新的那个
$exe = Get-ChildItem "$root/net/WinShare.App/bin" -Recurse -Filter WinShare.App.exe -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match [regex]::Escape("/$Configuration/") -or $_.FullName -match [regex]::Escape("\$Configuration\") } |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe -or -not (Test-Path $exe)) { Write-Output "EXE_NOT_FOUND under $($root)/net/WinShare.App/bin"; exit 1 }

Get-Process -Name 'WinShare*' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Start-Process -FilePath $exe -WorkingDirectory $root
Start-Sleep -Seconds 22
$p = Get-Process -Name 'WinShare.App' -ErrorAction SilentlyContinue
if (-not $p) { Write-Output "START_FAILED" } else {
  foreach ($x in $p) { Write-Output ("PID=" + $x.Id + " Responding=" + $x.Responding + " HWND=" + $x.MainWindowHandle) }
}
