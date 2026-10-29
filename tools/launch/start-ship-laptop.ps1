<#
.SYNOPSIS
  船上筆電啟動(Windows;規劃書第 5.4–5.5 節、第 14 章「船上部署」):依 config\ship-laptop.json 啟動全部服務,Ctrl+C 一併結束。
.DESCRIPTION
  啟動:SimCore.Host(WebSocket 8765、UDP 多播、NMEA)、Sensors.Radar.Host(8766)、靜態網頁 simosa-web(app\web,8080)、
  視景(app\visual\SimosaBRM.Visual.exe 匯出檔,或 Godot 編輯器 --path src\Visual.Godot;每個 views[] 一個程序,依 --view/--monitor)、
  直連同步服務 simosa-pkg serve(7300;需安裝時完成 config\packaging.json 與 device init)。各程序 log 在 build\logs\(上一次的輪替為 <元件>.1.log…,
  啟動標頭在 launch.log)。啟動前檢查各埠未被殘留程序占住(占用即報錯),SimCore.Host 啟動後若程序結束立即回報失敗。
  安裝根目錄為本檔上兩層(發行版 app\launch\..\..;儲存庫 tools\launch\..\..),所有輸出留在根目錄內,不寫 C:\。
.PARAMETER Scenario   覆寫預設情境(例如 data\scenarios\E05_pilot_traffic.yaml)
.PARAMETER Duration   模擬秒數到達後 SimCore 自動結束並一併結束其他程序(煙霧測試)
.PARAMETER NoWait     啟動後即返回(預設前景等待 Ctrl+C;之後以 stop-all.ps1 結束:先寫停止檔讓核心寫完紀錄 footer,逾時才強制結束)
.PARAMETER Shell      以 WebView2 操船台外殼(app\shell\SimosaBRM.Shell.exe;kiosk 全螢幕,每個 web.openPages 一個程序)開啟網頁,取代預設瀏覽器;
                      config 的 web.shell=true 同義;找不到外殼時退回瀏覽器(docs\ops\安裝與啟動.md 第 4.5 節)
.EXAMPLE
  .\start-ship-laptop.ps1
  .\start-ship-laptop.ps1 -Scenario data\scenarios\E05_pilot_traffic.yaml -Duration 60 -NoVisual
  .\start-ship-laptop.ps1 -Shell
#>
[CmdletBinding()]
param(
    [string]$Config = 'config\ship-laptop.json',
    [string]$Scenario,
    [double]$Duration = 0,
    [switch]$NoVisual, [switch]$NoWeb, [switch]$NoRadar, [switch]$NoSync, [switch]$NoBrowser, [switch]$NoWait, [switch]$CheckOnly, [switch]$Shell
)
. (Join-Path $PSScriptRoot 'launch-common.ps1')
$root = Get-LaunchRoot
Set-Location $root
if (-not [System.IO.Path]::IsPathRooted($Config)) { $Config = Join-Path $root $Config }
$cfg = Read-LaunchConfig $Config
$env:SIMOSA_BRM_ROOT = $root
$env:SIMOSA_INSTALL_ROOT = $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.cache\nuget\packages' }
$logs = Get-Cfg $cfg 'logs' 'build\logs'
$shipIp = Get-Cfg $cfg 'network.directLinkIp' '127.0.0.1'
$wsPort = Get-Cfg $cfg 'ports.stateWs' 8765
$radarPort = Get-Cfg $cfg 'ports.radarWs' 8766
$webPort = Get-Cfg $cfg 'ports.web' 8080
$syncPort = Get-Cfg $cfg 'ports.sync' 7300

Write-Host "Simosa BRM 船上筆電啟動 — $(Get-Cfg $cfg 'shipId') / $(Get-Cfg $cfg 'deviceId');安裝根目錄 $root;設定 $Config"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '找不到 dotnet(需安裝 .NET 8 執行階段;docs\ops\安裝與啟動.md)' }

$hostDll = Find-LaunchDll 'simcore' 'SimCore.Host' 'SimosaBRM.SimCore.Host.dll' $root
if (-not $hostDll) { throw '找不到 SimCore.Host(app\simcore 或 build\bin\SimCore.Host)' }
$radarDll = $null; if (-not $NoRadar -and (Get-Cfg $cfg 'radar.enabled' $true)) { $radarDll = Find-LaunchDll 'radar' 'Sensors.Radar.Host' 'SimosaBRM.Sensors.Radar.Host.dll' $root; if (-not $radarDll) { Write-Warning '找不到 Sensors.Radar.Host,略過雷達' } }
$webDll = $null; if (-not $NoWeb -and (Get-Cfg $cfg 'web.enabled' $true)) { $webDll = Find-LaunchDll 'web-server' 'StaticWeb' 'simosa-web.dll' $root; if (-not $webDll) { Write-Warning '找不到 simosa-web,略過網頁伺服' } }
$pkgDll = $null; if (-not $NoSync -and (Get-Cfg $cfg 'sync.enabled' $true)) { $pkgDll = Find-LaunchDll 'simosa-pkg' 'Tools.Packaging.Cli' 'simosa-pkg.dll' $root; if (-not $pkgDll) { Write-Warning '找不到 simosa-pkg,略過同步服務' } }
$webDir = $null
foreach ($d in @((Join-Path $root 'app\web'), (Join-Path $root 'build\web\instruments'))) { if (Test-Path (Join-Path $d 'index.html')) { $webDir = $d; break } }
# WebView2 外殼(-Shell 或 config web.shell=true;預設仍為瀏覽器)
$useShell = $Shell -or [bool](Get-Cfg $cfg 'web.shell' $false)
$shellExe = $null; if ($useShell) { $shellExe = Find-LaunchShellExe $root; if (-not $shellExe) { Write-Warning '找不到 WebView2 外殼 app\shell\SimosaBRM.Shell.exe,改用預設瀏覽器' } }

if ($CheckOnly) {
    Write-Host "SimCore.Host:$hostDll"; Write-Host "雷達:$(if ($radarDll) { $radarDll } else { '略過' })"
    Write-Host "網頁:$(if ($webDll) { $webDll } else { '略過' }) ← $(if ($webDir) { $webDir } else { '(找不到 app\web)' })"; Write-Host "同步:$(if ($pkgDll) { $pkgDll } else { '略過' })"
    Write-Host "外殼:$(if ($shellExe) { $shellExe } elseif ($useShell) { '(找不到,將用瀏覽器)' } else { '未啟用(-Shell 或 web.shell=true)' })"
    return
}

Set-DirectLinkIp (Get-Cfg $cfg 'network.adapterName') $shipIp (Get-Cfg $cfg 'network.subnetMask' '255.255.255.0')
Initialize-LaunchLogs $root $logs
$logDir = Join-Path $root $logs
# 啟動前確認各埠沒有被殘留的舊程序占住(否則 SimCore.Host 於 WebSocketStateServer.Start() 崩潰,而 Wait-LaunchTcp 連到的是別的程序);同時查直連 IP
$ports = @{ 'SimCore ws' = $wsPort }
if ($radarDll) { $ports['雷達 ws'] = $radarPort }
if ($webDll -and $webDir) { $ports['網頁'] = $webPort }
if ($pkgDll -and (Test-Path (Join-Path $root 'config\packaging.json')) -and (Test-Path (Join-Path $root 'config\device'))) { $ports['同步服務'] = $syncPort }
try { Assert-LaunchPortsFree $ports @($shipIp) } catch { Remove-Item $script:PidFile -ErrorAction SilentlyContinue; throw }

try {
    # 1) SimCore.Host
    $scen = if ($Scenario) { $Scenario } else { Get-Cfg $cfg 'simcore.scenario' 'data\scenarios\E01_baseline.yaml' }
    $hostArgs = @($hostDll, '--scenario', $scen, '--port', $wsPort)
    foreach ($pair in @(@('simcore.ship', '--ship'), @('simcore.loading', '--loading'), @('simcore.dynamics', '--dynamics'), @('simcore.timescale', '--timescale'),
                        @('ports.stateMulticast', '--multicast'), @('ports.nmeaUdp', '--nmea-port'), @('ports.nmeaMulticast', '--nmea-multicast'))) {
        $v = Get-Cfg $cfg $pair[0]; if ($null -ne $v -and "$v" -ne '') { $hostArgs += @($pair[1], "$v") }
    }
    $params = Get-Cfg $cfg 'simcore.parameters'
    if ($params -and (Test-Path (Join-Path $root $params))) { $hostArgs += @('--parameters', $params) }
    if (Get-Cfg $cfg 'simcore.record' $false) { $hostArgs += '--record' }
    if ($Duration -gt 0) { $hostArgs += @('--duration', "$Duration") }
    # 停止檔(優雅停止;stop-all.ps1 先寫此檔讓核心寫完紀錄 footer):路徑記在 $logs\simcore.stopfile
    $hostArgs += @('--stop-file', (Get-LaunchStopFile $root $logs))
    $hostProc = Start-LaunchProcess -Name 'simcore' -Log (Join-Path $logDir 'simcore.log') -File 'dotnet' -Arguments $hostArgs -WorkDir $root
    if (-not (Wait-LaunchTcp '127.0.0.1' $wsPort 30 -Process $hostProc)) {
        if ($hostProc.HasExited) { throw "SimCore.Host 已結束(啟動失敗,結束碼 $($hostProc.ExitCode)),見 $logs\simcore.log、simcore.err.log" }
        throw "SimCore.Host 未在 30 s 內開啟 ws 埠 $wsPort,見 $logs\simcore.log"
    }

    # 2) 雷達服務
    if ($radarDll) {
        $radarArgs = @($radarDll, '--sim', "ws://127.0.0.1:$wsPort/", '--port', $radarPort, '--radars', (Get-Cfg $cfg 'radar.radars' 'x,s'), '--areas', (Get-Cfg $cfg 'radar.areas' 'data\areas'),
                       '--nmea-port-x', (Get-Cfg $cfg 'ports.radarNmeaX' 10111), '--nmea-port-s', (Get-Cfg $cfg 'ports.radarNmeaS' 10112))
        if ($Duration -gt 0) { $radarArgs += @('--duration', "$($Duration + 5)") }
        $radarProc = Start-LaunchProcess -Name 'radar' -Log (Join-Path $logDir 'radar.log') -File 'dotnet' -Arguments $radarArgs -WorkDir $root
        if (-not (Wait-LaunchTcp '127.0.0.1' $radarPort 15 -Process $radarProc)) { Write-Warning "雷達服務未開啟埠 $radarPort 或已結束,見 $logs\radar.log" }
    }

    # 3) 靜態網頁
    if ($webDll -and $webDir) {
        $webProc = Start-LaunchProcess -Name 'web' -Log (Join-Path $logDir 'web.log') -File 'dotnet' -Arguments @($webDll, '--root', $webDir, '--port', $webPort, '--bind', (Get-Cfg $cfg 'web.bind' '0.0.0.0')) -WorkDir $root
        if (-not (Wait-LaunchTcp '127.0.0.1' $webPort 10 -Process $webProc)) { Write-Warning "simosa-web 未開啟埠 $webPort 或已結束,見 $logs\web.log" }
        if (-not $NoBrowser) {
            $subst = @{ ship = $shipIp; stateWs = $wsPort; radarWs = $radarPort }
            if ($shellExe) { Start-LaunchShellPages $shellExe "http://127.0.0.1:$webPort" (Get-Cfg $cfg 'web.openPages' @()) $subst $root $logDir (Get-Cfg $cfg 'web.shellMonitors' $null) }
            else { Open-LaunchPages "http://127.0.0.1:$webPort" (Get-Cfg $cfg 'web.openPages' @()) $subst }
        }
    } elseif ($webDll) { Write-Warning '找不到網頁目錄(app\web 或 build\web\instruments),略過' }

    # 4) 視景:每個 views[] 一個程序(docs\design\visual-client.md 第 7 節)
    if (-not $NoVisual -and (Get-Cfg $cfg 'visual.enabled' $true)) {
        $exe = Join-Path $root (Get-Cfg $cfg 'visual.exe' 'app\visual\SimosaBRM.Visual.exe')
        $editor = Get-Cfg $cfg 'visual.godotEditor'
        if ($editor -and -not [System.IO.Path]::IsPathRooted($editor)) { $editor = Join-Path $root $editor }
        $proj = Join-Path $root 'src\Visual.Godot'
        $quality = Get-Cfg $cfg 'visual.quality' 'auto'
        # 低品質(或 quality.json 記錄為低)以 Compatibility 渲染器啟動(與 tools\visual-run.ps1 同規則)
        $compat = ($quality -eq 'low')
        $qj = Join-Path $root 'build\visual\quality.json'
        if ($quality -eq 'auto' -and (Test-Path $qj)) {
            try { $q = Get-Content -Raw $qj | ConvertFrom-Json; $ov = "$(Get-Cfg $q 'manualOverride' '')".ToLower(); $la = "$(Get-Cfg $q 'lastAutoTier' '')".ToLower()
                  if ($ov -eq 'low' -or (($ov -eq '' -or $ov -eq 'auto') -and $la -eq 'low')) { $compat = $true } } catch { }
        }
        foreach ($view in @(Get-Cfg $cfg 'visual.views' @())) {
            $v = Get-Cfg $view 'view' 'bridge'; $m = Get-Cfg $view 'monitor' 0
            $vargs = @("--view=$v", "--monitor=$m", "--quality=$quality", "--ws=ws://127.0.0.1:$wsPort", "--data-dir=$root")
            if (Get-Cfg $view 'fullscreen' $false) { $vargs += '--fullscreen' }
            $engine = @(); if ($compat) { $engine += @('--rendering-method', 'gl_compatibility') }
            if (Test-Path $exe) {
                Start-LaunchProcess -Name "visual-$v" -Log (Join-Path $logDir "visual-$v.log") -File $exe -Arguments ($engine + @('--') + $vargs) -WorkDir (Split-Path $exe) -Window | Out-Null
            } elseif ($editor -and (Test-Path $editor) -and (Test-Path (Join-Path $proj 'project.godot'))) {
                # Godot 編輯器/使用者資料留在專案內(.cache\godot\appdata)
                $env:APPDATA = Join-Path $root '.cache\godot\appdata\roaming'; $env:LOCALAPPDATA = Join-Path $root '.cache\godot\appdata\local'
                New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA | Out-Null
                if (-not (Test-Path (Join-Path $proj '.godot\mono\temp\bin\Debug\SimosaBRM.Visual.Godot.dll'))) { & dotnet build (Join-Path $proj 'Visual.Godot.csproj') -nologo -v q }
                Start-LaunchProcess -Name "visual-$v" -Log (Join-Path $logDir "visual-$v.log") -File $editor -Arguments (@('--path', $proj) + $engine + @('--') + $vargs) -WorkDir $root -Window | Out-Null
            } else { Write-Host "  視景 ${v}:略過(無 $exe,也無 Godot 編輯器 $editor;docs\ops\安裝與啟動.md)" }
        }
    }

    # 5) 直連同步服務(需安裝時完成 config\packaging.json 與 device init)
    if ($pkgDll) {
        if ((Test-Path (Join-Path $root 'config\packaging.json')) -and (Test-Path (Join-Path $root 'config\device'))) {
            $syncProc = Start-LaunchProcess -Name 'sync' -Log (Join-Path $logDir 'sync.log') -File 'dotnet' -Arguments @($pkgDll, 'serve', '--root', $root, '--port', $syncPort) -WorkDir $root
            Start-Sleep -Seconds 1
            if ($syncProc.HasExited) { Write-Warning "同步服務已結束,見 $logs\sync.log、sync.err.log" }
        } else { Write-Host '  同步服務:略過(尚未安裝:缺 config\packaging.json 或 config\device\;見 docs\ops\安裝與啟動.md「首次啟動」)' }
    }

    Write-Host "已啟動;PID 檔 $logs\simosa.pids。網頁 http://$shipIp`:$webPort/instructor.html?mode=simple  狀態 ws://$shipIp`:$wsPort  雷達 ws://$shipIp`:$radarPort/radar/x"
    if ($Duration -gt 0) {
        Write-Host "等待 SimCore 於模擬 $Duration s 後結束…"
        $hostProc.WaitForExit()
    } elseif (-not $NoWait) {
        Write-Host 'Ctrl+C 結束全部程序'
        while (-not $hostProc.HasExited) { Start-Sleep -Seconds 1 }
        Write-Warning 'SimCore.Host 已結束'
    } else { return }
}
finally {
    if (-not $NoWait -or $Duration -gt 0) { & (Join-Path $PSScriptRoot 'stop-all.ps1') -Logs $logs }
}
