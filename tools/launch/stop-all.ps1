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
.PARAMETER KeepBrowser
  不關閉以專屬設定檔(build\browser\edge)開啟的 Microsoft Edge 視窗。預設會請它們正常關閉:瀏覽器端資料(草稿、參與者資料、訓練紀錄)
  放在專案磁碟上的設定檔內,Edge 還開著就拔除隨身碟會損毀其中的 IndexedDB / localStorage。
#>
[CmdletBinding()]
param([string]$Logs = 'build\logs', [switch]$KeepSync, [int]$StopTimeoutSeconds = 10, [switch]$KeepBrowser)
$ErrorActionPreference = 'Continue'
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logDir = Join-Path $root $Logs
$pidFile = Join-Path $logDir 'simosa.pids'
$stopRec = Join-Path $logDir 'simcore.stopfile'

# 關閉以專屬設定檔開啟的 Edge(啟動腳本的 Open-LaunchPages;不在 PID 檔內:Edge 的啟動程序會把工作交給既有的瀏覽器程序)。
# 以命令列含 --user-data-dir=<根目錄>\build\browser\edge 且不含 --type= 的 msedge.exe 為主程序,CloseMainWindow 讓它正常收尾;
# 只在 Windows 執行,查詢失敗時只提示。
function Close-ProfileEdge {
    if (-not $onWindows -or $KeepBrowser) { return }
    $profileDir = Join-Path (Join-Path (Join-Path $root 'build') 'browser') 'edge'
    if (-not (Test-Path $profileDir)) { return }
    try {
        $mains = @(Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($profileDir, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and $_.CommandLine -notmatch '--type=' })
        $asked = @()
        foreach ($m in $mains) {
            $p = Get-Process -Id $m.ProcessId -ErrorAction SilentlyContinue
            if ($p) { if ($p.MainWindowHandle -ne [IntPtr]::Zero) { [void]$p.CloseMainWindow() }; $asked += $p }
        }
        if ($asked.Count -eq 0) { return }
        # 等它們真的結束(最多 5 秒):Edge 可能跳出「要關閉所有分頁嗎」確認,或還有第二個視窗 — 那樣程序不會結束
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline -and ($asked | Where-Object { -not $_.HasExited })) { Start-Sleep -Milliseconds 250 }
        $still = @($asked | Where-Object { -not $_.HasExited })
        if ($still.Count -eq 0) { Write-Host "已關閉 Simosa BRM 的 Edge 視窗(設定檔 $profileDir)" }
        else { Write-Warning "Simosa BRM 的 Edge(設定檔 $profileDir)仍在執行(可能在等「要關閉所有分頁嗎」的確認,或還有其他視窗):拔除隨身碟前請手動關閉這些 Edge 視窗" }
    } catch { Write-Warning "無法確認 Simosa BRM 的 Edge 視窗是否已關閉($($_.Exception.Message)):拔除隨身碟前請先關閉以啟動腳本開啟的 Edge 視窗" }
}

if (-not (Test-Path $pidFile)) { Write-Host "沒有 $pidFile(未啟動或已結束)"; Remove-Item $stopRec -ErrorAction SilentlyContinue; Close-ProfileEdge; return }
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
Close-ProfileEdge
