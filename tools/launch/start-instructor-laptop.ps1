<#
.SYNOPSIS
  教官筆電啟動(訪船船長;規劃書第 5.4 節):教官站網頁、雷達鏡像頁、直連檢查與 simosa-pkg sync 說明;離船時可單機執行 SimCore。
.DESCRIPTION
  依 config\instructor-laptop.json:本機 simosa-web 提供 app\web(教官站 instructor.html、雷達 radar.html、講評 report.html),
  頁面的 ws 參數指向船上筆電(直連 IP);-Standalone(或 standalone.enabled)時改啟動本機 SimCore.Host 與雷達供岸上備課、審閱紀錄。
  直連前檢查 Wi-Fi/藍牙(第 5.4 節:直連期間停用;-CheckWifi 時 Wi-Fi 開啟則拒絕繼續)。同步不自動執行:顯示 simosa-pkg sync 指令
  (需硬體金鑰 --cert,且更新包產生前須完成受影響情境驗證;docs\design\packaging-sync.md 第 6.2–6.3 節)。
.EXAMPLE
  .\start-instructor-laptop.ps1 -Ship FSB1
  .\start-instructor-laptop.ps1 -Standalone
#>
[CmdletBinding()]
param(
    [string]$Config = 'config\instructor-laptop.json',
    [string]$Ship,
    [switch]$Standalone, [switch]$CheckWifi, [switch]$NoBrowser, [switch]$NoWait
)
. (Join-Path $PSScriptRoot 'launch-common.ps1')
$root = Get-LaunchRoot
Set-Location $root
if (-not [System.IO.Path]::IsPathRooted($Config)) { $Config = Join-Path $root $Config }
$cfg = Read-LaunchConfig $Config
$env:SIMOSA_BRM_ROOT = $root
$env:SIMOSA_INSTALL_ROOT = $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$logs = Get-Cfg $cfg 'logs' 'build\logs'
if (-not $Ship) { $Ship = Get-Cfg $cfg 'ship' 'FSB1' }
$shipIp = Get-Cfg $cfg "ships.$Ship" $Ship
$standalone = $Standalone -or (Get-Cfg $cfg 'standalone.enabled' $false)
if ($standalone) { $shipIp = '127.0.0.1' }
$wsPort = Get-Cfg $cfg 'ports.stateWs' 8765
$radarPort = Get-Cfg $cfg 'ports.radarWs' 8766
$webPort = Get-Cfg $cfg 'ports.web' 8080

Write-Host "Simosa BRM 教官筆電 — 目標船 $Ship($shipIp);安裝根目錄 $root"
if (-not $standalone) {
    $wifi = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and ($_.PhysicalMediaType -match '802\.11|Native' -or $_.InterfaceDescription -match 'Wi-?Fi|Wireless|Bluetooth') }
    if ($wifi) {
        Write-Warning "直連模式:下列無線介面仍啟用,請停用後再同步:$(($wifi | ForEach-Object { $_.Name }) -join '、')"
        if ($CheckWifi) { throw '教官筆電 Wi-Fi/藍牙開啟時直連模式拒絕同步(規劃書第 14 章「船上部署」)' }
    }
    Set-DirectLinkIp (Get-Cfg $cfg 'network.adapterName') (Get-Cfg $cfg 'network.directLinkIp') (Get-Cfg $cfg 'network.subnetMask' '255.255.255.0')
}
Initialize-LaunchLogs $root $logs
$logDir = Join-Path $root $logs
try {
    if ($standalone) {
        $hostDll = Find-LaunchDll 'simcore' 'SimCore.Host' 'SimosaBRM.SimCore.Host.dll' $root
        if (-not $hostDll) { throw '找不到 SimCore.Host' }
        $hostArgs = @($hostDll, '--scenario', (Get-Cfg $cfg 'standalone.scenario' 'data\scenarios\E01_baseline.yaml'), '--port', $wsPort, '--no-nmea')
        foreach ($pair in @(@('standalone.ship', '--ship'), @('standalone.loading', '--loading'))) { $v = Get-Cfg $cfg $pair[0]; if ($v) { $hostArgs += @($pair[1], "$v") } }
        if (Get-Cfg $cfg 'standalone.record' $false) { $hostArgs += '--record' }
        $hostArgs += @('--stop-file', (Get-LaunchStopFile $root $logs))  # stop-all.ps1 以停止檔讓核心寫完紀錄 footer
        $hostProc = Start-LaunchProcess -Name 'simcore' -Log (Join-Path $logDir 'simcore.log') -File 'dotnet' -Arguments $hostArgs -WorkDir $root
        if (-not (Wait-LaunchTcp '127.0.0.1' $wsPort 30)) { Write-Warning "SimCore.Host 未開啟 $wsPort" }
        $radarDll = Find-LaunchDll 'radar' 'Sensors.Radar.Host' 'SimosaBRM.Sensors.Radar.Host.dll' $root
        if ($radarDll) { Start-LaunchProcess -Name 'radar' -Log (Join-Path $logDir 'radar.log') -File 'dotnet' -Arguments @($radarDll, '--sim', "ws://127.0.0.1:$wsPort/", '--port', $radarPort, '--localhost-only', '--no-nmea') -WorkDir $root | Out-Null }
    } else {
        if (Wait-LaunchTcp $shipIp $wsPort 3) { Write-Host "  船上 SimCore.Host 狀態流 ws://$shipIp`:$wsPort 可連線" }
        else { Write-Warning "尚未連到 ws://$shipIp`:$wsPort(船上筆電未啟動或網路線未接;第 14 章:接線後 1 分鐘內應可連線)" }
    }
    $webDir = $null
    foreach ($d in @((Join-Path $root 'app\web'), (Join-Path $root 'build\web\instruments'))) { if (Test-Path (Join-Path $d 'index.html')) { $webDir = $d; break } }
    if ((Get-Cfg $cfg 'web.enabled' $true) -and $webDir) {
        $webDll = Find-LaunchDll 'web-server' 'StaticWeb' 'simosa-web.dll' $root
        if ($webDll) {
            Start-LaunchProcess -Name 'web' -Log (Join-Path $logDir 'web.log') -File 'dotnet' -Arguments @($webDll, '--root', $webDir, '--port', $webPort, '--bind', (Get-Cfg $cfg 'web.bind' '127.0.0.1')) -WorkDir $root | Out-Null
            Wait-LaunchTcp '127.0.0.1' $webPort 10 | Out-Null
            $pages = Get-Cfg $cfg 'web.openPages' @()
            if ($NoBrowser) { foreach ($p in @($pages)) { Write-Host "  http://127.0.0.1:$webPort/$p" } }
            else { Open-LaunchPages "http://127.0.0.1:$webPort" $pages @{ ship = $shipIp; stateWs = $wsPort; radarWs = $radarPort } }
        }
    }
    $cert = Get-Cfg $cfg 'sync.certThumbprint' ''
    $certArg = if ($cert) { $cert } else { '<硬體金鑰憑證指紋>' }
    Write-Host @"
同步(訪船時;更新包產生前須完成受影響情境驗證,docs\design\packaging-sync.md 第 6.2–6.3 節):
  simosa-pkg sync --ship $Ship --cert $certArg [--push build\packages\<更新包>.sbupd]...   # 推更新包、取回紀錄包、送已備份回報
  simosa-pkg store backup --to <加密外接碟>                                             # 回岸後備份紀錄庫,下次訪船才回報已備份
  simosa-pkg export --to <隨身碟> --role instructor --push <更新包> --cert $certArg       # 網路線不可用時的隨身碟備援
(simosa-pkg = dotnet tools\updater\simosa-pkg.dll,或儲存庫 tools\simosa-pkg.ps1)
"@
    if (-not $NoWait) {
        Write-Host 'Ctrl+C 結束全部程序'
        while ($true) { Start-Sleep -Seconds 1 }
    }
}
finally {
    if (-not $NoWait) { & (Join-Path $PSScriptRoot 'stop-all.ps1') -Logs $logs }
}
