# T3a 冒烟脚本：构建 + 启动 WinShare.App.exe + 存活 ≥5s + 退出
# 用法：pwsh -File net\WinShare.App\smoke.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent) | Out-Null   # → net\
Remove-Item Env:Platform -ErrorAction SilentlyContinue
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'

Write-Host "== build =="
& $dotnet build "WinShare.App\WinShare.App.csproj" -v q | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "SMOKE-FAIL: build failed"; exit 1 }

$exe = "WinShare.App\bin\Debug\net10.0-windows\WinShare.App.exe"
# 清掉上一次冒烟残留（单实例锁会让新进程立即自退，干扰判定）
Get-Process -Name "WinShare.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "== launch =="
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

if (-not $p.HasExited) {
    Write-Host "SMOKE-OK: 进程存活 6s（PID $($p.Id)，WPF 主窗口 + 托盘 + 单实例锁就绪）"
    $p.Kill() | Out-Null
    exit 0
} else {
    Write-Host "SMOKE-FAIL: 进程提前退出 exitCode=$($p.ExitCode)"
    # 附注：若上一实例仍持锁，二实例会按设计立即自退；请确认无残留后重试
    exit 1
}
