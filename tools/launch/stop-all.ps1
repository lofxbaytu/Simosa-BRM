<#
.SYNOPSIS
  結束 start-ship-laptop.ps1 / start-instructor-laptop.ps1 啟動的全部程序(讀 build\logs\simosa.pids)。
.DESCRIPTION
  SimCore.Host:先寫停止檔(路徑在 build\logs\simcore.stopfile;啟動腳本以 --stop-file 傳給核心),核心每秒檢查到即自行結束練習並寫完紀錄 footer
  (Stop-Process 不會送 Ctrl+C,主控台程序收不到訊號,所以不能直接 Stop-Process);其他程序關閉主視窗(視景)或 Stop-Process。
  最多等 -StopTimeoutSeconds(預設 10 s),逾時仍在執行者強制結束(紀錄可能缺 footer)。
.PARAMETER KeepSync
  保留直連同步服務(simosa-pkg serve,PID 檔中名稱為 sync 的程序)與其 PID 列,供教官筆電在模擬器關閉後仍能推送軟體更新包
  (docs\ops\安裝與啟動.md 第 5.2 節);下次 start-ship-laptop.ps1 會先結束它再重新啟動。
.PARAMETER StopTimeoutSeconds
  等待核心寫完 footer / 其他程序結束的上限(秒;預設 10)。
#>
[CmdletBinding()]
param([string]$Logs = 'build\logs', [switch]$KeepSync, [int]$StopTimeoutSeconds = 10)
$ErrorActionPreference = 'Continue'
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logDir = Join-Path $root $Logs
$pidFile = Join-Path $logDir 'simosa.pids'
$stopRec = Join-Path $logDir 'simcore.stopfile'
if (-not (Test-Path $pidFile)) { Write-Host "沒有 $pidFile(未啟動或已結束)"; Remove-Item $stopRec -ErrorAction SilentlyContinue; return }
$stopFile = $null
if (Test-Path $stopRec) { $stopFile = (Get-Content $stopRec | Where-Object { $_ } | Select-Object -First 1) }
$procs = @()
$kept = @()
foreach ($line in Get-Content $pidFile) {
    if (-not $line -or -not $line.Contains(':')) { continue }
    $name, $id = $line.Split(':', 2)
    $p = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
    if ($p) {
        if ($KeepSync -and $name -eq 'sync') { Write-Host "保留 $name(PID $id)"; $kept += "${name}:$id"; continue }
        if ($name -eq 'simcore' -and $stopFile) {
            # 核心:寫停止檔讓它自行結束練習並寫完紀錄 footer(不 Stop-Process)
            Write-Host "結束 $name(PID $id):寫入停止檔 $stopFile,等核心寫完紀錄 footer(最多 $StopTimeoutSeconds s)"
            try { New-Item -ItemType File -Force -Path $stopFile -ErrorAction Stop | Out-Null }
            catch { Write-Warning "無法寫入停止檔($($_.Exception.Message)),改以 Stop-Process 結束"; try { $p | Stop-Process -ErrorAction Stop } catch { } }
        } else {
            Write-Host "結束 $name(PID $id)"
            # CloseMainWindow 只在 Windows 有效(Linux 上的 pwsh 測試用)
            if (-not ($onWindows -and $p.CloseMainWindow())) {
                try { $p | Stop-Process -ErrorAction Stop } catch { }
            }
        }
        $procs += $p
    }
}
$deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
while ((Get-Date) -lt $deadline -and ($procs | Where-Object { -not $_.HasExited })) { Start-Sleep -Milliseconds 250 }
foreach ($p in $procs) { if (-not $p.HasExited) { Write-Host "強制結束 PID $($p.Id)(逾時 $StopTimeoutSeconds s;紀錄可能缺 footer)"; Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
# 核心未消化的停止檔與路徑紀錄一併清掉(下次啟動時核心也會刪殘留檔)
if ($stopFile) { Remove-Item $stopFile -ErrorAction SilentlyContinue }
Remove-Item $stopRec -ErrorAction SilentlyContinue
if ($kept.Count -gt 0) {
    Set-Content -Path $pidFile -Value $kept -Encoding UTF8
    Write-Host "已結束全部程序(保留 $(($kept | ForEach-Object { $_.Split(':')[0] }) -join '、');PID 檔 $pidFile)"
} else {
    Remove-Item $pidFile -ErrorAction SilentlyContinue
    Write-Host '已結束全部程序'
}
