<#
.SYNOPSIS
  長時間穩定性(soak)測試(Windows;Linux/容器版 tools\soak-test.sh 參數相同):規劃書第 14 章「效能」列(核心 50 Hz 無掉拍、延遲 < 50 ms、RSS 不持續成長、連續 2 小時)。
.DESCRIPTION
  1) 建置 SimCore.Host、Sensors.Radar.Host、StaticWeb(Release;-SkipBuild 略過;網頁缺 build\web\instruments 時 pnpm build)
  2) 啟動 SimCore.Host(-Scenario,預設 E05;--record 到 build\soak\<run>\records\;VHF 與錄影端開啟於 -PortBase+3/+4;--no-udp --no-nmea;--stop-file)、
     雷達服務(x+s)、靜態網頁 simosa-web
  3) 啟動合成客戶端 tests\soak\clients.mjs(N 個;狀態幀 + 雷達 X 頻影像 + 網頁 GET;每秒統計幀率/延遲/斷線)與學員 tests\soak\student.mjs
     (每 10 s 舵令/車令、每 60 s 注入/清除故障、每 5 min 快照)
  4) 每 -SampleSeconds s 取樣主控程序 CPU%(TotalProcessorTime 差 / 牆鐘)、RSS(WorkingSet64)、執行緒數、steps/s 與掉拍(主控程序 stdout 行);雷達/網頁/客戶端程序的 CPU 與 RSS;
     dotnet-counters(PATH 或 .cache\dotnet-tools)可用時另收 System.Runtime GC 計數器,否則略過
  5) 時間到 → stop-all.ps1(停止檔讓核心寫完紀錄 footer)→ node tests\soak\report.mjs 寫 build\soak\report-<run>.md 與 .json 並判定
  全部輸出在 build\soak\(不寫 C:\);結束碼 0 合格、1 不合格(含中止)、2 前置條件錯誤。需獨佔 CPU,不要與整套測試併行。
  -WhatIf:只檢查參數與工具鏈(dotnet、node、建置輸出、埠)並印出計畫,不啟動任何程序、不寫檔(tools\run-ps-tests.sh 以此做靜態/參數檢查;
  容器沒有 Windows 實機時本腳本只經 pwsh 解析與 -WhatIf 驗證,實跑結果以 soak-test.sh 為準)。
.EXAMPLE
  .\tools\soak-test.ps1 -Minutes 120 -Clients 3 -Note "船上內顯筆電"
  .\tools\soak-test.ps1 -Minutes 10 -Clients 3 -SkipBuild
  .\tools\soak-test.ps1 -WhatIf -Minutes 1 -Clients 2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [double]$Minutes = 10,
    [int]$Clients = 3,
    [string]$Scenario = 'E05_pilot_traffic',
    [int]$PortBase = 18800,
    [switch]$NoRadar, [switch]$NoWeb, [switch]$SkipBuild,
    [int]$SampleSeconds = 10,
    [double]$MaxLatencyMs = 50, [double]$MaxRssSlopeMbPerMin = 1, [double]$MaxRssGrowthMb = 64, [int]$WarmupSeconds = 120,
    [string]$Run,
    [string]$Note
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
. (Join-Path $root 'tools\launch\launch-common.ps1')   # Find-LaunchDll、Initialize-LaunchLogs、Start-LaunchProcess、Wait-LaunchTcp、Assert-LaunchPortsFree、Get-LaunchStopFile
if ($Minutes * 60 -lt 10) { throw '-Minutes 太短(至少 10 s)' }
if ($Clients -lt 1 -or $SampleSeconds -lt 1) { throw '-Clients 與 -SampleSeconds 須 ≥ 1' }
if ($Scenario -notlike '*.yaml' -and $Scenario -notmatch '[\\/]') { $Scenario = Join-Path (Join-Path 'data' 'scenarios') "$Scenario.yaml" }
if (-not (Test-Path $Scenario)) { throw "找不到情境檔 $Scenario" }
$scenId = [System.IO.Path]::GetFileNameWithoutExtension($Scenario)
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '找不到 dotnet(需 .NET 8)' }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw '找不到 node(需 Node.js 22,內建 WebSocket)' }
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.cache\dotnet-home' }
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.cache\nuget\packages' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:SIMOSA_BRM_ROOT = $root
if (-not $Run) { $Run = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') }
$outRel = "build\soak\$Run"; $outDir = Join-Path $root $outRel; $logsRel = "$outRel\logs"; $stats = Join-Path $outDir 'stats'
$reportMd = Join-Path $root "build\soak\report-$Run.md"; $reportJson = Join-Path $root "build\soak\report-$Run.json"
$wsPort = $PortBase; $radarPort = $PortBase + 1; $webPort = $PortBase + 2; $vhfPort = $PortBase + 3; $mediaPort = $PortBase + 4
$gitRev = $null
if (Test-Path '.git\HEAD') {
    $head = (Get-Content '.git\HEAD' -Raw).Trim()
    if ($head.StartsWith('ref: ')) { $refFile = Join-Path '.git' ($head.Substring(5) -replace '/', '\'); if (Test-Path $refFile) { $gitRev = (Get-Content $refFile -Raw).Trim() } } else { $gitRev = $head }
}
function Step([string]$m) { Write-Host ''; Write-Host "=== [soak] $m" }
function Find-DotnetCounters {
    foreach ($c in @((Join-Path $root '.cache\dotnet-tools\dotnet-counters.exe'), (Join-Path $root '.cache\dotnet-tools\dotnet-counters'))) { if (Test-Path $c) { return $c } }
    $g = Get-Command dotnet-counters -ErrorAction SilentlyContinue; if ($g) { return $g.Source }
    return $null
}
$dc = Find-DotnetCounters
$webDir = @((Join-Path $root 'build\web\instruments'), (Join-Path $root 'app\web')) | Where-Object { Test-Path (Join-Path $_ 'index.html') } | Select-Object -First 1

if ($WhatIfPreference) {
    # -WhatIf:只印計畫與工具鏈檢查,不建置、不啟動、不寫檔
    Write-Host "WhatIf:Simosa BRM soak 測試 ${Run}:情境 $scenId、客戶端 $Clients、$Minutes 分鐘 → $outRel\"
    Write-Host "  dotnet $(& dotnet --version)、node $(& node --version)、dotnet-counters $(if ($dc) { $dc } else { '無(GC 計數器略過)' })"
    Write-Host "  1/5 建置:$(if ($SkipBuild) { '略過(-SkipBuild)' } else { 'SimCore.Host、Sensors.Radar.Host、StaticWeb(Release)' + $(if (-not $NoWeb -and -not $webDir) { ';網頁 pnpm build' } else { '' }) })"
    Write-Host "  2/5 SimCore.Host(ws $wsPort、VHF $vhfPort、錄影 $mediaPort;紀錄 $outRel\records\)$(if ($NoRadar) { '' } else { "、雷達 x+s(ws $radarPort)" })$(if ($NoWeb) { '' } else { "、網頁(http $webPort ← $(if ($webDir) { $webDir } else { '缺 build\web\instruments' }))" })"
    Write-Host "  3/5 tests\soak\clients.mjs ×$Clients + tests\soak\student.mjs($Minutes 分鐘)"
    Write-Host "  4/5 每 $SampleSeconds s 取樣 CPU%/RSS/執行緒/steps\/s/掉拍 → stats\host.jsonl"
    Write-Host "  5/5 stop-all.ps1 → tests\soak\report.mjs(延遲 p95 < $MaxLatencyMs ms;RSS 斜率 ≤ $MaxRssSlopeMbPerMin MB/min、增量 ≤ $MaxRssGrowthMb MB;暖機 $WarmupSeconds s)→ build\soak\report-$Run.md/.json"
    foreach ($p in @($wsPort, $radarPort, $webPort, $vhfPort, $mediaPort)) { if (-not (Test-LaunchPortFree '127.0.0.1' $p)) { Write-Host "  注意:埠 $p 目前被占用" } }
    return
}

if (-not $SkipBuild) {
    Step '建置 SimCore.Host、Sensors.Radar.Host、StaticWeb(Release)'
    foreach ($p in @('src\SimCore.Host', 'src\Sensors.Radar.Host', 'tools\launch\StaticWeb')) { & dotnet build $p -c Release --nologo -v q; if ($LASTEXITCODE -ne 0) { throw "建置 $p 失敗" } }
    if (-not $NoWeb -and -not $webDir) {
        Step '建置網頁(src\Instruments.Web → build\web\instruments)'
        Push-Location 'src\Instruments.Web'
        try {
            if (-not (Test-Path 'node_modules')) { & pnpm install --frozen-lockfile; if ($LASTEXITCODE -ne 0) { throw 'pnpm install 失敗' } }
            & pnpm build; if ($LASTEXITCODE -ne 0) { throw 'pnpm build 失敗' }
        } finally { Pop-Location }
        $webDir = Join-Path $root 'build\web\instruments'
    }
}
$hostDll = Find-LaunchDll 'simcore' 'SimCore.Host' 'SimosaBRM.SimCore.Host.dll' $root
if (-not $hostDll) { throw '找不到 SimCore.Host(dotnet build src\SimCore.Host -c Release)' }
$radarDll = $null; if (-not $NoRadar) { $radarDll = Find-LaunchDll 'radar' 'Sensors.Radar.Host' 'SimosaBRM.Sensors.Radar.Host.dll' $root; if (-not $radarDll) { throw '找不到 Sensors.Radar.Host(或 -NoRadar)' } }
$webDll = $null; if (-not $NoWeb) { $webDll = Find-LaunchDll 'web-server' 'StaticWeb' 'simosa-web.dll' $root; if (-not $webDll) { throw '找不到 simosa-web(或 -NoWeb)' }; if (-not $webDir) { throw '找不到網頁輸出 build\web\instruments\index.html(或 -NoWeb)' } }

# 建置輸出複製一份到本次執行目錄 bin\(長時間執行中若有人重新 dotnet build,覆寫執行中的 dll 會讓核心崩潰;與 soak-test.sh 相同)
function Copy-BinSnapshot([string]$Dll, [string]$Name) {
    $dest = Join-Path $outDir "bin\$Name"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Recurse -Force (Join-Path (Split-Path -Parent $Dll) '*') $dest
    return (Join-Path $dest (Split-Path -Leaf $Dll))
}
$ports = @{ 'SimCore ws' = $wsPort; 'VHF' = $vhfPort; '錄影' = $mediaPort }
if ($radarDll) { $ports['雷達'] = $radarPort }
if ($webDll) { $ports['網頁'] = $webPort }
Assert-LaunchPortsFree $ports
foreach ($d in @('records', 'media', 'stats', 'logs')) { New-Item -ItemType Directory -Force -Path (Join-Path $outDir $d) | Out-Null }
$hostDll = Copy-BinSnapshot $hostDll 'simcore'
if ($radarDll) { $radarDll = Copy-BinSnapshot $radarDll 'radar' }
if ($webDll) { $webDll = Copy-BinSnapshot $webDll 'web-server' }
Initialize-LaunchLogs $root $logsRel
$stopFile = Get-LaunchStopFile $root $logsRel
$startedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$script:aborted = $false; $script:hostExit = $null; $script:footer = $false; $script:endedUtc = $null
$script:hostProc = $null; $script:radarProc = $null; $script:webProc = $null; $script:clientsProc = $null; $script:studentProc = $null

function Write-Meta {
    $meta = [ordered]@{
        run = $Run; scenario = $scenId; scenarioPath = $Scenario; clients = $Clients; minutes = $Minutes; sample_s = $SampleSeconds
        ports = [ordered]@{ ws = $wsPort; radar = $(if ($radarDll) { $radarPort } else { 0 }); web = $(if ($webDll) { $webPort } else { 0 }); vhf = $vhfPort; media = $mediaPort }
        startedUtc = $startedUtc; endedUtc = $script:endedUtc
        platform = "$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription) $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
        cpuCount = [Environment]::ProcessorCount; machineNote = $(if ($Note) { $Note } else { 'Windows 開發機/船上筆電(以 -Note 註明)' })
        dotnet = (& dotnet --version); node = (& node --version); gitRev = $gitRev
        recordPath = "$outRel\records\soak-$scenId.jsonl" -replace '\\', '/'
        hostPid = $(if ($script:hostProc) { $script:hostProc.Id } else { 0 }); hostExit = $script:hostExit; footer = $script:footer; aborted = $script:aborted
        counters = [bool]$dc
        thresholds = [ordered]@{ maxLatencyMs = $MaxLatencyMs; maxRssSlopeMbPerMin = $MaxRssSlopeMbPerMin; maxRssGrowthMb = $MaxRssGrowthMb; warmupS = $WarmupSeconds }
    }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $outDir 'meta.json') -Encoding UTF8
}

# 程序取樣:CPU% = TotalProcessorTime 差 / 牆鐘差(可 > 100);RSS = WorkingSet64
$script:prevCpu = @{}
function Get-ProcJson([string]$Name, [System.Diagnostics.Process]$Proc, [double]$DeltaWallMs) {
    if ($null -eq $Proc) { return 'null' }
    try { $Proc.Refresh() } catch { }
    if ($Proc.HasExited) { return '{"alive":false,"cpu_pct":null,"rss_mb":null,"threads":null}' }
    try {
        $cpuMs = $Proc.TotalProcessorTime.TotalMilliseconds
        $prev = if ($script:prevCpu.ContainsKey($Name)) { $script:prevCpu[$Name] } else { $cpuMs }
        $script:prevCpu[$Name] = $cpuMs
        $pct = if ($DeltaWallMs -gt 0) { [math]::Round(($cpuMs - $prev) / $DeltaWallMs * 100, 1) } else { 0 }
        $rss = [math]::Round($Proc.WorkingSet64 / 1MB, 1)
        return ('{{"alive":true,"cpu_pct":{0},"rss_mb":{1},"threads":{2}}}' -f $pct.ToString([cultureinfo]::InvariantCulture), $rss.ToString([cultureinfo]::InvariantCulture), $Proc.Threads.Count)
    } catch { return '{"alive":true,"cpu_pct":null,"rss_mb":null,"threads":null}' }
}
# 主控程序 stdout 的每秒狀態行:「steps/s N」與累計「掉拍 N」;只看上次取樣之後的新行
$script:logDone = 0; $script:dropped = 0
function Get-HostLogJson([string]$Log) {
    $lines = @()
    try { $lines = @([System.IO.File]::ReadAllLines($Log)) } catch { }
    $new = if ($lines.Count -gt $script:logDone) { $lines[$script:logDone..($lines.Count - 1)] } else { @() }
    $script:logDone = $lines.Count
    $vals = @()
    foreach ($l in $new) {
        if ($l -match 'steps/s (\d+)') { $vals += [int]$Matches[1] }
        if ($l -match '掉拍 (\d+)') { $script:dropped = [int]$Matches[1] }
    }
    if ($vals.Count -eq 0) { return 'null' }
    $m = ($vals | Measure-Object -Average -Minimum -Maximum)
    return ('{{"n":{0},"mean":{1},"min":{2},"max":{3}}}' -f $vals.Count, ([math]::Round($m.Average, 2)).ToString([cultureinfo]::InvariantCulture), [int]$m.Minimum, [int]$m.Maximum)
}

$script:finished = $false
function Complete-Soak {
    if ($script:finished) { return 0 }
    $script:finished = $true
    Step '結束:等客戶端收尾 → 停止檔讓核心寫完紀錄 footer → 其他程序結束'
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and -not $script:aborted) {
        $busy = @($script:clientsProc, $script:studentProc) | Where-Object { $_ -and -not $_.HasExited }
        if ($busy.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    }
    # 外部程式與子腳本的輸出一律送 Out-Host:本函式以回傳值傳結束碼,不能讓它們的輸出混進回傳值
    try { & (Join-Path $root 'tools\launch\stop-all.ps1') -Logs $logsRel -StopTimeoutSeconds 20 | Out-Host } catch { Write-Warning "stop-all:$($_.Exception.Message)" }
    if ($script:hostProc) { try { $script:hostProc.WaitForExit(5000) | Out-Null; $script:hostExit = $script:hostProc.ExitCode } catch { } }
    $log = Join-Path (Join-Path $root $logsRel) 'simcore.log'
    if ((Test-Path $log) -and ((Get-Content -Raw $log) -match '紀錄已關閉')) { $script:footer = $true }
    $script:endedUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    Write-Meta
    Step '報告'
    & node (Join-Path $root 'tests\soak\report.mjs') --dir $outDir --md $reportMd --json $reportJson --max-latency-ms "$MaxLatencyMs" --max-rss-slope-mb-per-min "$MaxRssSlopeMbPerMin" --max-rss-growth-mb "$MaxRssGrowthMb" --warmup-s "$WarmupSeconds" 2>&1 | Out-Host
    $rc = $LASTEXITCODE
    if ($script:aborted) { Write-Host '測試中途中止(Ctrl+C / 核心提前結束),結果不作為合格依據'; $rc = 1 }
    Write-Host "全部輸出:$outRel\(logs、stats、records、media、meta.json);報告 build\soak\report-$Run.md"
    return $rc
}

$exitCode = 2
try {
    Step "啟動 SimCore.Host($scenId;紀錄 $outRel\records\、VHF $vhfPort、錄影 $mediaPort)"
    $hostArgs = @($hostDll, '--scenario', $Scenario, '--record', (Join-Path $outDir "records\soak-$scenId.jsonl"), '--port', "$wsPort", '--vhf-port', "$vhfPort", '--media-port', "$mediaPort",
                  '--media-dir', "$outRel\media", '--no-udp', '--no-nmea', '--stop-file', $stopFile)
    $script:hostProc = Start-LaunchProcess -Name 'simcore' -Log (Join-Path (Join-Path $root $logsRel) 'simcore.log') -File 'dotnet' -Arguments $hostArgs -WorkDir $root
    Write-Meta
    if (-not (Wait-LaunchTcp '127.0.0.1' $wsPort 45 -Process $script:hostProc)) { throw "SimCore.Host 未開啟埠 $wsPort(或已結束),見 $logsRel\simcore.log" }
    if ($radarDll) {
        Step '啟動雷達服務(X+S)'
        $script:radarProc = Start-LaunchProcess -Name 'radar' -Log (Join-Path (Join-Path $root $logsRel) 'radar.log') -File 'dotnet' -Arguments @($radarDll, '--sim', "ws://127.0.0.1:$wsPort/", '--port', "$radarPort", '--radars', 'x,s', '--areas', 'data\areas', '--no-nmea') -WorkDir $root
        if (-not (Wait-LaunchTcp '127.0.0.1' $radarPort 30 -Process $script:radarProc)) { throw "雷達服務未開啟埠 $radarPort,見 $logsRel\radar.log" }
    }
    if ($webDll) {
        Step "啟動靜態網頁(simosa-web ← $webDir)"
        $script:webProc = Start-LaunchProcess -Name 'web' -Log (Join-Path (Join-Path $root $logsRel) 'web.log') -File 'dotnet' -Arguments @($webDll, '--root', $webDir, '--port', "$webPort", '--bind', '127.0.0.1', '--quiet') -WorkDir $root
        if (-not (Wait-LaunchTcp '127.0.0.1' $webPort 15 -Process $script:webProc)) { throw "simosa-web 未開啟埠 $webPort,見 $logsRel\web.log" }
    }
    if ($dc) {
        Step "dotnet-counters(System.Runtime,每 $SampleSeconds s)→ stats\counters.csv"
        Start-LaunchProcess -Name 'counters' -Log (Join-Path (Join-Path $root $logsRel) 'counters.log') -File $dc -Arguments @('collect', '--process-id', "$($script:hostProc.Id)", '--refresh-interval', "$SampleSeconds", '--format', 'csv', '--output', (Join-Path $stats 'counters.csv'), '--counters', 'System.Runtime') -WorkDir $root | Out-Null
    } else { Write-Host '  dotnet-counters 不可用(PATH 或 .cache\dotnet-tools 皆無):GC 計數器略過,RSS 以 WorkingSet64 取樣' }

    Step "啟動合成客戶端 ×$Clients 與學員($Minutes 分鐘)"
    $clientArgs = @((Join-Path $root 'tests\soak\clients.mjs'), '--ws', "ws://127.0.0.1:$wsPort/", '--clients', "$Clients", '--minutes', "$Minutes", '--out', (Join-Path $stats 'clients.jsonl'))
    if ($radarDll) { $clientArgs += @('--radar', "ws://127.0.0.1:$radarPort/radar/x") }
    if ($webDll) { $clientArgs += @('--web', "http://127.0.0.1:$webPort") }
    $script:clientsProc = Start-LaunchProcess -Name 'clients' -Log (Join-Path (Join-Path $root $logsRel) 'clients.log') -File 'node' -Arguments $clientArgs -WorkDir $root
    $script:studentProc = Start-LaunchProcess -Name 'student' -Log (Join-Path (Join-Path $root $logsRel) 'student.log') -File 'node' -Arguments @((Join-Path $root 'tests\soak\student.mjs'), '--ws', "ws://127.0.0.1:$wsPort/", '--minutes', "$Minutes", '--out', (Join-Path $stats 'student.jsonl')) -WorkDir $root

    Step "取樣(每 $SampleSeconds s → stats\host.jsonl;Ctrl+C 可提前結束並仍產生報告)"
    $hostLog = Join-Path (Join-Path $root $logsRel) 'simcore.log'
    $sw = [System.Diagnostics.Stopwatch]::StartNew(); $lastMs = 0.0
    Get-ProcJson 'host' $script:hostProc 0 | Out-Null; Get-ProcJson 'radar' $script:radarProc 0 | Out-Null; Get-ProcJson 'web' $script:webProc 0 | Out-Null; Get-ProcJson 'clients' $script:clientsProc 0 | Out-Null
    Get-HostLogJson $hostLog | Out-Null
    $deadlineS = $Minutes * 60 + 90
    $hostStats = Join-Path $stats 'host.jsonl'
    while ($true) {
        Start-Sleep -Seconds $SampleSeconds
        $nowMs = $sw.Elapsed.TotalMilliseconds; $dWall = $nowMs - $lastMs; $lastMs = $nowMs
        $elapsed = [math]::Round($nowMs / 1000, 1)
        $hj = Get-ProcJson 'host' $script:hostProc $dWall; $rj = Get-ProcJson 'radar' $script:radarProc $dWall; $wj = Get-ProcJson 'web' $script:webProc $dWall; $cj = Get-ProcJson 'clients' $script:clientsProc $dWall
        $steps = Get-HostLogJson $hostLog
        $hj = $hj.Substring(0, $hj.Length - 1) + (',"stepsPerS":{0},"dropped":{1}}}' -f $steps, $script:dropped)
        $line = '{{"kind":"sample","wall":"{0}","elapsed_s":{1},"host":{2},"radar":{3},"web":{4},"clients":{5}}}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $elapsed.ToString([cultureinfo]::InvariantCulture), $hj, $rj, $wj, $cj
        Add-Content -Path $hostStats -Value $line -Encoding UTF8
        Write-Host ("  {0,6}s  核心 {1}  steps/s {2}  掉拍 {3}" -f $elapsed, $hj, $steps, $script:dropped)
        if ($script:hostProc.HasExited) { Write-Warning "SimCore.Host 提前結束(見 $logsRel\simcore.log)"; $script:aborted = $true; break }
        if ($script:clientsProc.HasExited -and $script:studentProc.HasExited) { break }
        if ($elapsed -ge $deadlineS) { Write-Warning '客戶端逾時未結束,強制收尾'; break }
    }
    $exitCode = Complete-Soak
} catch {
    Write-Host "[soak] 失敗:$($_.Exception.Message)"
    $script:aborted = $true
    if ($script:hostProc) { $exitCode = Complete-Soak } else { try { & (Join-Path $root 'tools\launch\stop-all.ps1') -Logs $logsRel | Out-Host } catch { }; $exitCode = 2 }
}
exit $exitCode
