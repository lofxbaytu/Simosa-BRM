<#
.SYNOPSIS
  結束 start-ship-laptop.ps1 / start-instructor-laptop.ps1 啟動的全部程序(讀 build\logs\simosa.pids)。
.DESCRIPTION
  先嘗試關閉主視窗(視景)或 Ctrl+C 等價的正常結束讓 SimCore 寫完紀錄 footer;5 秒後仍在執行者強制結束。
.PARAMETER KeepSync
  保留直連同步服務(simosa-pkg serve,PID 檔中名稱為 sync 的程序)與其 PID 列,供教官筆電在模擬器關閉後仍能推送軟體更新包
  (docs\ops\安裝與啟動.md 第 5.2 節);下次 start-ship-laptop.ps1 會先結束它再重新啟動。
#>
[CmdletBinding()]
param([string]$Logs = 'build\logs', [switch]$KeepSync)
$ErrorActionPreference = 'Continue'
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pidFile = Join-Path (Join-Path $root $Logs) 'simosa.pids'
if (-not (Test-Path $pidFile)) { Write-Host "沒有 $pidFile(未啟動或已結束)"; return }
$procs = @()
$kept = @()
foreach ($line in Get-Content $pidFile) {
    if (-not $line -or -not $line.Contains(':')) { continue }
    $name, $id = $line.Split(':', 2)
    $p = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
    if ($p) {
        if ($KeepSync -and $name -eq 'sync') { Write-Host "保留 $name(PID $id)"; $kept += "${name}:$id"; continue }
        Write-Host "結束 $name(PID $id)"
        # CloseMainWindow 只在 Windows 有效(Linux 上的 pwsh 測試用)
        if (-not ($onWindows -and $p.CloseMainWindow())) {
            # 主控台程序:送 Ctrl+C 需附加主控台,改以正常 Stop 前先給 1 s 緩衝;SimCore 每 10 s 快照、footer 於結束時寫入
            try { $p | Stop-Process -ErrorAction Stop } catch { }
        }
        $procs += $p
    }
}
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline -and ($procs | Where-Object { -not $_.HasExited })) { Start-Sleep -Milliseconds 250 }
foreach ($p in $procs) { if (-not $p.HasExited) { Write-Host "強制結束 PID $($p.Id)"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
if ($kept.Count -gt 0) {
    Set-Content -Path $pidFile -Value $kept -Encoding UTF8
    Write-Host "已結束全部程序(保留 $(($kept | ForEach-Object { $_.Split(':')[0] }) -join '、');PID 檔 $pidFile)"
} else {
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    Write-Host '已結束全部程序'
}
