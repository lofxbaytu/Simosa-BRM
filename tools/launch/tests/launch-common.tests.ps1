<#
.SYNOPSIS
  launch-common.ps1 / stop-all.ps1 的測試(PowerShell 7;Windows 或 Linux 皆可執行,不需 Pester)。
.DESCRIPTION
  驗證:Start-LaunchProcess 不覆寫既有 log(輪替保留歷史、啟動標頭寫入 launch.log)、Test-LaunchPortFree 偵測被占用的埠、
  Wait-LaunchTcp -Process 在程序結束時立即回傳、stop-all.ps1 -KeepSync 保留同步服務、stop-all.ps1 先寫停止檔(Get-LaunchStopFile)再逾時強制結束、
  WebView2 外殼:Find-LaunchShellExe 搜尋順序、Start-LaunchShellPages 參數組合(--url/--kiosk/--no-close-confirm/--monitor)與 simosa.pids 登錄、
  找不到外殼時退回 Open-LaunchPages(以假 exe 於 build\test-tmp\launch-ps\app\shell\SimosaBRM.Shell.exe)、
  USB 操控台服務:Get-LaunchConsoleArgs 的參數規則(序列埠 / loopback:)與以 loopback 假裝置實跑 app\console 服務(ACK、LED,LINK,0 回寫;無核心)、
  NMEA 選項 Get-LaunchNmeaArgs(ports.nmeaTargets/nmeaBind/nmeaFile → --nmea-target/--nmea-bind/--nmea-file;空/null 不加)與
  Assert-LaunchPortsFree 對 VHF 8767 / 錄影 8768 埠名稱的回報(第二十四輪)、
  發行版狀態(第三十輪;教官站頁首的「發行版 <版本>」與降版提醒):Get-LaunchReleaseStatusPath、Find-LaunchReleaseStatusTool(安裝版才有、不建置)、
  Write-LaunchReleaseStatus(以真實的 simosa-pkg status --json 寫出狀態檔;失敗、逾時、輸出不是 JSON 都不丟例外並刪掉舊檔)與啟動腳本的 -CheckOnly 輸出、
  船上筆電的網頁位址(第三十一輪;教官筆電的教官站向船上取發行版狀態):Get-LaunchShipWebUrl(直連而且有設 ports.shipWeb 才有)、
  Get-LaunchShipWebPort(只有整數是埠;8081.0、陣列、物件、true 警告並略過)、Add-LaunchShipWebParam(只有教官站頁面加 shipWeb 參數;
  帶 # 片段時加在片段之前)與 start-instructor-laptop.ps1 的接法(Linux 的 pwsh 另外實際啟動:直連模式與 -Standalone)。
  所有輸出留在 build\test-tmp\launch-ps\。
  用法:pwsh -NoProfile -File tools\launch\tests\launch-common.tests.ps1(tools\launch\tests\launch-tests.sh 為 .sh 版測試;
  容器/開發機沒有 pwsh 時以 tools\run-ps-tests.sh 自 .cache\dotnet-tools 取用 dotnet 全域工具版 PowerShell)
#>
[CmdletBinding()]
param([int]$PortBase = 18790)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$launch = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path (Join-Path $launch '..\..')).Path
$tmp = Join-Path $root 'build\test-tmp\launch-ps'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$sleeper = if ($IsWindows) { @{ File = 'powershell'; Args = @('-NoProfile', '-Command', 'Start-Sleep 600') } } else { @{ File = 'sleep'; Args = @('600') } }
$pass = 0; $fail = 0; $failed = @()

function Invoke-Test([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name"
    try { & $Body; $script:pass++; Write-Host '    通過' }
    catch { $script:fail++; $script:failed += $Name; Write-Host "    失敗:$($_.Exception.Message)" }
}
function Assert-True($Cond, [string]$Msg) { if (-not $Cond) { throw $Msg } }

Invoke-Test 'Start-LaunchProcess 保留既有 log 與啟動標頭' {
    . (Join-Path $launch 'launch-common.ps1')
    $logs = 'build\test-tmp\launch-ps\logs1'
    Remove-Item -Recurse -Force (Join-Path $root $logs) -ErrorAction SilentlyContinue
    Initialize-LaunchLogs $root $logs
    $log = Join-Path (Join-Path $root $logs) 'tool.log'
    Set-Content -Path $log -Value "上一次的紀錄 第 1 行`n上一次的崩潰原因" -Encoding UTF8
    $p = Start-LaunchProcess -Name 'tool' -Log $log -File 'dotnet' -Arguments @('--version') -WorkDir $root
    $p.WaitForExit()
    $history = @(Get-ChildItem (Join-Path $root $logs) -Filter 'tool*.log' | Where-Object { $_.Name -ne 'tool.log' -and $_.Name -notlike '*.err.log' })
    Assert-True ($history.Count -ge 1) "上一次的 log 應被保留為另一個檔案(目前只有:$((Get-ChildItem (Join-Path $root $logs)).Name -join '、'))"
    $kept = ($history | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
    Assert-True ($kept.Contains('上一次的崩潰原因')) '上一次的 log 內容應仍可找到'
    $launchLog = Join-Path (Join-Path $root $logs) 'launch.log'
    Assert-True (Test-Path $launchLog) '應有 launch.log(啟動標頭:時間、名稱、PID、指令與參數)'
    $hdr = Get-Content -Raw $launchLog
    Assert-True ($hdr.Contains('dotnet --version') -and $hdr.Contains("PID $($p.Id)")) "launch.log 應含指令、參數與 PID:$hdr"
    # 參數確實傳給程序(dotnet --version 印版本號;參數遺失時 dotnet 會印用法)
    Assert-True ((Get-Content -Raw $log) -match '\d+\.\d+\.\d+') "目前的 tool.log 應為 dotnet --version 的輸出(參數需確實傳入):$(Get-Content -Raw $log)"
    # 連續啟動多次只保留最近 3 份歷史
    1..5 | ForEach-Object { (Start-LaunchProcess -Name 'tool' -Log $log -File 'dotnet' -Arguments @('--version') -WorkDir $root).WaitForExit() }
    $history = @(Get-ChildItem (Join-Path $root $logs) -Filter 'tool.*.log' | Where-Object { $_.Name -notlike '*.err*' })
    Assert-True ($history.Count -le 3) "歷史 log 應最多保留 3 份(目前 $($history.Count))"
}

Invoke-Test 'Test-LaunchPortFree 偵測被占用的埠' {
    . (Join-Path $launch 'launch-common.ps1')
    Assert-True (Get-Command Test-LaunchPortFree -ErrorAction SilentlyContinue) '應有 Test-LaunchPortFree 函式'
    $port = $PortBase + 1
    $l = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), $port
    $l.Start()
    try { Assert-True (-not (Test-LaunchPortFree '127.0.0.1' $port)) "埠 $port 被占用時應回傳 false" }
    finally { $l.Stop() }
    Assert-True (Test-LaunchPortFree '127.0.0.1' $port) "埠 $port 釋放後應回傳 true"
}

Invoke-Test 'Wait-LaunchTcp -Process 在程序結束時立即失敗' {
    . (Join-Path $launch 'launch-common.ps1')
    $p = Start-Process -FilePath 'dotnet' -ArgumentList '--version' -PassThru -RedirectStandardOutput (Join-Path $tmp 'ver.out')
    $p.WaitForExit()
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $r = Wait-LaunchTcp '127.0.0.1' ($PortBase + 2) 30 -Process $p
    $sw.Stop()
    Assert-True (-not $r) '程序已結束時應回傳 false'
    Assert-True ($sw.Elapsed.TotalSeconds -lt 5) "應立即回傳而非等滿 30 s(耗時 $([int]$sw.Elapsed.TotalSeconds) s)"
}

Invoke-Test 'stop-all.ps1 -KeepSync 保留同步服務' {
    $logs = 'build\test-tmp\launch-ps\logs2'
    $dir = Join-Path $root $logs
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $web = Start-Process -FilePath $sleeper.File -ArgumentList $sleeper.Args -PassThru
    $sync = Start-Process -FilePath $sleeper.File -ArgumentList $sleeper.Args -PassThru
    try {
        Set-Content -Path (Join-Path $dir 'simosa.pids') -Value @("web:$($web.Id)", "sync:$($sync.Id)") -Encoding UTF8
        & (Join-Path $launch 'stop-all.ps1') -Logs $logs -KeepSync -KeepBrowser | Out-Null
        Start-Sleep -Milliseconds 500
        Assert-True $web.HasExited 'web 程序應已結束'
        Assert-True (-not $sync.HasExited) 'sync 程序應被保留'
        $lines = @(Get-Content (Join-Path $dir 'simosa.pids') | Where-Object { $_ })
        Assert-True ($lines -contains "sync:$($sync.Id)") "PID 檔應保留 sync 列:$($lines -join '、')"
        Assert-True (-not ($lines -like 'web:*')) 'PID 檔不應再有 web 列'
        & (Join-Path $launch 'stop-all.ps1') -Logs $logs -KeepBrowser | Out-Null
        Start-Sleep -Milliseconds 500
        Assert-True $sync.HasExited '第二次 stop-all 應結束 sync'
        Assert-True (-not (Test-Path (Join-Path $dir 'simosa.pids'))) 'PID 檔應被移除'
    } finally {
        foreach ($p in @($web, $sync)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    }
}

Invoke-Test 'stop-all.ps1 先寫停止檔讓 SimCore 自行結束,逾時才強制結束' {
    . (Join-Path $launch 'launch-common.ps1')
    $logs = 'build\test-tmp\launch-ps\logs3'
    $dir = Join-Path $root $logs
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    # Get-LaunchStopFile:回傳 <logs>\simcore.stop 並把路徑記在 simcore.stopfile(stop-all.ps1 讀取)
    $stop = Get-LaunchStopFile $root $logs
    Assert-True ($stop -eq (Join-Path $dir 'simcore.stop')) "停止檔路徑應為 <logs>\simcore.stop:$stop"
    Assert-True ((Get-Content (Join-Path $dir 'simcore.stopfile') | Where-Object { $_ } | Select-Object -First 1) -eq $stop) 'simcore.stopfile 應記錄停止檔路徑'
    # 假的核心(sleep 不會消化停止檔):stop-all 應先寫停止檔、等候 -StopTimeoutSeconds 後才強制結束,並清掉停止檔與路徑紀錄
    $core = Start-Process -FilePath $sleeper.File -ArgumentList $sleeper.Args -PassThru
    try {
        Set-Content -Path (Join-Path $dir 'simosa.pids') -Value @("simcore:$($core.Id)") -Encoding UTF8
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $out = (& (Join-Path $launch 'stop-all.ps1') -Logs $logs -StopTimeoutSeconds 1 -KeepBrowser *>&1 | Out-String)
        $sw.Stop()
        Assert-True ($out.Contains('寫入停止檔')) "應回報寫入停止檔:$out"
        Assert-True ($out.Contains('強制結束')) "假核心不會自行結束,逾時後應強制結束:$out"
        Assert-True ($sw.Elapsed.TotalSeconds -ge 1 -and $sw.Elapsed.TotalSeconds -lt 8) "應等滿 1 s 逾時再強制結束(耗時 $([int]$sw.Elapsed.TotalSeconds) s)"
        Start-Sleep -Milliseconds 500
        Assert-True $core.HasExited '假核心應已被強制結束'
        Assert-True (-not (Test-Path $stop)) '未被消化的停止檔應被 stop-all 清掉'
        Assert-True (-not (Test-Path (Join-Path $dir 'simcore.stopfile'))) 'simcore.stopfile 應被清掉'
        Assert-True (-not (Test-Path (Join-Path $dir 'simosa.pids'))) 'PID 檔應被移除'
    } finally {
        if (-not $core.HasExited) { Stop-Process -Id $core.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ---- WebView2 操船台外殼(第二十輪審查 minor:補外殼案例;docs\ops\安裝與啟動.md 第 4.5 節)----
# 假外殼 SimosaBRM.Shell.exe:Linux 為回印每個參數一行的 sh 腳本(可驗證參數確實傳到程序);Windows 為 pwsh 本身的複本(帶 --url 等未知參數時印用法並立即結束)
function New-FakeShellExe([string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    if ($IsWindows) { Copy-Item (Get-Process -Id $PID).Path $Path -Force }
    else {
        $body = @'
#!/bin/sh
printf '%s\n' "$@"
'@
        # 本檔在 Linux 的全新簽出為 CRLF(.gitattributes):here-string 內的 CR 會讓 shebang 變成「/bin/sh\r」而無法執行,先去掉
        Set-Content -Path $Path -Value ($body -replace "`r", '') -Encoding ascii
        & chmod +x $Path
    }
    return $Path
}
# 等 simosa.pids 內的程序全部結束(假外殼立即結束;逾時不報錯,由後續斷言判定)
function Wait-TestPids([string]$PidFile, [int]$Seconds = 10) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $alive = @(Get-Content $PidFile | Where-Object { $_ } | ForEach-Object { [int]($_.Split(':', 2)[1]) } | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($alive.Count -eq 0) { return }
        Start-Sleep -Milliseconds 200
    }
}

Invoke-Test 'Find-LaunchShellExe 搜尋順序:app\shell → Release win-x64 → Release → Debug win-x64 → Debug' {
    . (Join-Path $launch 'launch-common.ps1')
    $fakeRoot = Join-Path $tmp 'shellroot'
    Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $fakeRoot | Out-Null
    Assert-True ($null -eq (Find-LaunchShellExe $fakeRoot)) '沒有任何外殼時應回傳 $null'
    # 由低到高優先序逐一加入,每次都應回傳剛加入(優先序較高)的那一個
    $order = @('build\bin\Shell.WebView2\Debug\net8.0-windows\SimosaBRM.Shell.exe',
               'build\bin\Shell.WebView2\Debug\net8.0-windows\win-x64\SimosaBRM.Shell.exe',
               'build\bin\Shell.WebView2\Release\net8.0-windows\SimosaBRM.Shell.exe',
               'build\bin\Shell.WebView2\Release\net8.0-windows\win-x64\SimosaBRM.Shell.exe',
               'app\shell\SimosaBRM.Shell.exe')
    foreach ($rel in $order) {
        $exe = New-FakeShellExe (Join-Path $fakeRoot $rel)
        $found = Find-LaunchShellExe $fakeRoot
        Assert-True ($found -and ((Resolve-Path $found).Path -eq (Resolve-Path $exe).Path)) "加入 $rel 後應優先回傳它,實際:$found"
    }
    # 其他路徑的同名檔不算(例如 build\bin\Shell.WebView2\Release\net8.0\)
    Remove-Item -Recurse -Force $fakeRoot
    New-FakeShellExe (Join-Path $fakeRoot 'build\bin\Shell.WebView2\Release\net8.0\SimosaBRM.Shell.exe') | Out-Null
    Assert-True ($null -eq (Find-LaunchShellExe $fakeRoot)) '非候選路徑的 SimosaBRM.Shell.exe 不應被採用'
}

Invoke-Test 'Start-LaunchShellPages 參數組合(--url/--kiosk/--no-close-confirm/--monitor)、PID 登錄、同主檔名頁面' {
    . (Join-Path $launch 'launch-common.ps1')
    $logs = 'build\test-tmp\launch-ps\logs4'
    $logDir = Join-Path $root $logs
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    Initialize-LaunchLogs $root $logs
    $pidFile = Join-Path $logDir 'simosa.pids'
    $exe = New-FakeShellExe (Join-Path $tmp 'app\shell\SimosaBRM.Shell.exe')
    # 與 config\ship-laptop.json 相同形式的 openPages / shellMonitors({stateWs} 代換;第 2 頁螢幕 $null、第 4 頁超出 shellMonitors 長度)
    $pages = @('index.html?station=bridge&ws={stateWs}', 'instructor.html?mode=simple', 'radar.html?radar=x', 'index.html?station=portWing')
    Start-LaunchShellPages $exe 'http://127.0.0.1:18080' $pages @{ stateWs = 18765 } $root $logDir @(0, $null, 1)
    Wait-TestPids $pidFile
    $pids = @(Get-Content $pidFile | Where-Object { $_ })
    $names = @($pids | ForEach-Object { $_.Split(':', 2)[0] })
    Assert-True (($names -join ',') -eq 'shell-index,shell-instructor,shell-radar,shell-index-2') "simosa.pids 應依序登錄 4 個外殼(同主檔名第 2 頁為 shell-index-2),實際:$($names -join ',')"
    Assert-True (@($pids | Where-Object { $_ -match ':\d+$' }).Count -eq 4) "每列都應有 PID:$($pids -join '、')"
    $hdr = @(Get-Content (Join-Path $logDir 'launch.log') | Where-Object { $_ -like '# *' })
    Assert-True ($hdr.Count -eq 4) "launch.log 應有 4 行啟動標頭,實際 $($hdr.Count)"
    $h = @{}; foreach ($line in $hdr) { $h[($line -split ' ')[3]] = $line }
    Assert-True ($h['shell-index'].EndsWith('--url http://127.0.0.1:18080/index.html?station=bridge&ws=18765 --kiosk --no-close-confirm --monitor 0')) "第 1 頁參數應含代換後網址、--kiosk、--no-close-confirm、--monitor 0:$($h['shell-index'])"
    Assert-True ($h['shell-instructor'].EndsWith('--url http://127.0.0.1:18080/instructor.html?mode=simple --kiosk --no-close-confirm')) "第 2 頁螢幕為 `$null 時不應有 --monitor:$($h['shell-instructor'])"
    Assert-True ($h['shell-radar'].EndsWith('--url http://127.0.0.1:18080/radar.html?radar=x --kiosk --no-close-confirm --monitor 1')) "第 3 頁應為 --monitor 1:$($h['shell-radar'])"
    Assert-True ($h['shell-index-2'].EndsWith('--url http://127.0.0.1:18080/index.html?station=portWing --kiosk --no-close-confirm')) "第 4 頁超出 shellMonitors 長度時不應有 --monitor:$($h['shell-index-2'])"
    Assert-True ($h['shell-index'].Contains(" $exe ")) "啟動標頭應記錄外殼 exe 路徑:$($h['shell-index'])"
    foreach ($n in $names) { Assert-True (Test-Path (Join-Path $logDir "$n.log")) "應有 $n.log(每頁一個 log,同主檔名不互相覆寫)" }
    if (-not $IsWindows) {
        # Linux 假外殼回印參數:確認引號/空白處理後程序實際收到的參數與組合完全一致
        $got = @(Get-Content (Join-Path $logDir 'shell-index.log') | Where-Object { $_ })
        Assert-True (($got -join ' ') -eq '--url http://127.0.0.1:18080/index.html?station=bridge&ws=18765 --kiosk --no-close-confirm --monitor 0') "外殼程序實際收到的參數不符:$($got -join ' ')"
        $got2 = @(Get-Content (Join-Path $logDir 'shell-instructor.log') | Where-Object { $_ })
        Assert-True (($got2 -join ' ') -eq '--url http://127.0.0.1:18080/instructor.html?mode=simple --kiosk --no-close-confirm') "第 2 頁外殼實際收到的參數不符:$($got2 -join ' ')"
    }
    # 省略 $Monitors(預設 $null)與 shellMonitors 為單一數值時
    Set-Content -Path $pidFile -Value '' -Encoding UTF8
    Start-LaunchShellPages $exe 'http://127.0.0.1:18080' @('report.html') @{} $root $logDir
    Start-LaunchShellPages $exe 'http://127.0.0.1:18080' 'radar.html?radar=s' @{} $root $logDir 1
    Wait-TestPids $pidFile
    $hdr2 = @(Get-Content (Join-Path $logDir 'launch.log') | Where-Object { $_ -like '# *' } | Select-Object -Last 2)
    Assert-True ($hdr2[0].EndsWith('--url http://127.0.0.1:18080/report.html --kiosk --no-close-confirm')) "省略 Monitors 時不應有 --monitor:$($hdr2[0])"
    Assert-True ($hdr2[1].EndsWith('--url http://127.0.0.1:18080/radar.html?radar=s --kiosk --no-close-confirm --monitor 1')) "單一頁面字串與單一螢幕數值應可用:$($hdr2[1])"
    $names2 = @(Get-Content $pidFile | Where-Object { $_ } | ForEach-Object { $_.Split(':', 2)[0] })
    Assert-True (($names2 -join ',') -eq 'shell-report,shell-radar') "PID 名稱應為 shell-report、shell-radar:$($names2 -join ',')"
    & (Join-Path $launch 'stop-all.ps1') -Logs $logs -KeepBrowser | Out-Null
}

Invoke-Test 'Start-LaunchShellPages 找不到外殼時退回 Open-LaunchPages 且不登錄 PID' {
    . (Join-Path $launch 'launch-common.ps1')
    # 以假的 Open-LaunchPages 取代(真的會 Start-Process 網址開瀏覽器);在 dot-source 之後定義才會蓋過原函式
    $script:openCalls = @()
    ${function:Open-LaunchPages} = { param([string]$BaseUrl, $Pages, [hashtable]$Subst, [string]$ProfileDir = '') $script:openCalls += , @($BaseUrl, @($Pages), $Subst, $ProfileDir) }
    $logs = 'build\test-tmp\launch-ps\logs5'
    $logDir = Join-Path $root $logs
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    Initialize-LaunchLogs $root $logs
    $pages = @('index.html?station=bridge', 'radar.html?radar=x')
    $missing = Join-Path $tmp 'nope\SimosaBRM.Shell.exe'
    $profile1 = Get-LaunchBrowserProfile $root
    $warn1 = (Start-LaunchShellPages $missing 'http://127.0.0.1:18080' $pages @{ ship = '127.0.0.1' } $root $logDir @(0, 1) $profile1 3>&1 | Out-String)
    $warn2 = (Start-LaunchShellPages $null 'http://127.0.0.1:18080' $pages @{ ship = '127.0.0.1' } $root $logDir 3>&1 | Out-String)
    Assert-True ($warn1.Contains('改用瀏覽器') -and $warn2.Contains('改用瀏覽器')) "exe 不存在或為 `$null 時都應警告改用瀏覽器:$warn1 / $warn2"
    # 瀏覽器設定檔位置原樣轉交(有給 → 專屬 Edge 設定檔;沒給 → 空字串 = 預設瀏覽器)
    Assert-True ($script:openCalls[0][3] -eq $profile1 -and $script:openCalls[1][3] -eq '') "應把 BrowserProfile 轉交給 Open-LaunchPages:'$($script:openCalls[0][3])' / '$($script:openCalls[1][3])'"
    Assert-True ($script:openCalls.Count -eq 2) "應呼叫 Open-LaunchPages 2 次,實際 $($script:openCalls.Count)"
    Assert-True ($script:openCalls[0][0] -eq 'http://127.0.0.1:18080' -and (@($script:openCalls[0][1]) -join ',') -eq ($pages -join ',') -and $script:openCalls[0][2]['ship'] -eq '127.0.0.1') '應以相同 BaseUrl、全部頁面與代換表呼叫 Open-LaunchPages'
    $pidLines = @(Get-Content (Join-Path $logDir 'simosa.pids') | Where-Object { $_ })
    Assert-True ($pidLines.Count -eq 0) "退回瀏覽器時不應登錄任何 PID:$($pidLines -join '、')"
    Assert-True (-not (Get-ChildItem $logDir -Filter 'shell-*.log')) '退回瀏覽器時不應產生 shell-*.log'
    Assert-True (-not (Test-Path (Join-Path $logDir 'launch.log'))) '退回瀏覽器時不應寫啟動標頭'
}

# ---- 本機資料位置(守則:放 D: 或 G:,不寫 C:\):快取、暫存、瀏覽器端資料都在安裝根目錄內 ----
Invoke-Test 'Set-LaunchProjectEnv:NuGet/.NET 快取與 TEMP/TMP 導到根目錄內;已在根目錄內者沿用、指到根目錄外者改回;SIMOSA_SYSTEM_TEMP=1 不改暫存' {
    . (Join-Path $launch 'launch-common.ps1')
    $names = @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH', 'TEMP', 'TMP', 'SIMOSA_SYSTEM_TEMP')
    $saved = @{}; foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
    try {
        $fakeRoot = Join-Path $tmp 'envroot'
        Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $fakeRoot | Out-Null
        foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $null) }
        # 已設定且在根目錄內 → 沿用;指到根目錄外(例如專案從 D: 複製到 G: 後殘留的使用者層級設定,或指到 C: 的舊值)→ 改指回本根目錄
        $inside = Join-Path (Join-Path $fakeRoot 'custom') 'packages'
        [Environment]::SetEnvironmentVariable('NUGET_PACKAGES', $inside)
        [Environment]::SetEnvironmentVariable('NUGET_SCRATCH', (Join-Path (Join-Path $tmp 'other-copy') 'scratch'))
        $note = (Set-LaunchProjectEnv $fakeRoot 6>&1 | Out-String)
        $norm = { param($p) ([string]$p) -replace '\\', '/' }
        $base = & $norm $fakeRoot
        Assert-True ($env:NUGET_PACKAGES -eq $inside) "已設定且在根目錄內的變數應沿用:$($env:NUGET_PACKAGES)"
        Assert-True ($note.Contains('NUGET_SCRATCH') -and $note.Contains('other-copy')) "改掉指到根目錄外的變數時應說明原值:$note"
        Assert-True (Test-LaunchUnderRoot (Join-Path $fakeRoot 'a') $fakeRoot) 'Test-LaunchUnderRoot:根目錄下的路徑'
        Assert-True (-not (Test-LaunchUnderRoot ($fakeRoot + '-other') $fakeRoot)) 'Test-LaunchUnderRoot:名稱以根目錄為字首的同層資料夾不算在其下'
        Assert-True (-not (Test-LaunchUnderRoot '' $fakeRoot)) 'Test-LaunchUnderRoot:空字串不算'
        Assert-True ((& $norm $env:DOTNET_CLI_HOME) -eq "$base/.cache/dotnet-home") "DOTNET_CLI_HOME:$($env:DOTNET_CLI_HOME)"
        Assert-True ((& $norm $env:NUGET_HTTP_CACHE_PATH) -eq "$base/.cache/nuget/http") "NUGET_HTTP_CACHE_PATH:$($env:NUGET_HTTP_CACHE_PATH)"
        Assert-True ((& $norm $env:NUGET_PLUGINS_CACHE_PATH) -eq "$base/.cache/nuget/plugins") "NUGET_PLUGINS_CACHE_PATH:$($env:NUGET_PLUGINS_CACHE_PATH)"
        Assert-True ((& $norm $env:NUGET_SCRATCH) -eq "$base/.cache/nuget/scratch") "NUGET_SCRATCH:$($env:NUGET_SCRATCH)"
        Assert-True ((& $norm $env:TEMP) -eq "$base/build/tmp" -and (& $norm $env:TMP) -eq "$base/build/tmp") "TEMP/TMP 應為 <根目錄>/build/tmp:$($env:TEMP) / $($env:TMP)"
        Assert-True (Test-Path (Join-Path (Join-Path $fakeRoot 'build') 'tmp')) '應建立 build/tmp'
        # SIMOSA_SYSTEM_TEMP=1(經同意才使用):不改 TEMP/TMP
        [Environment]::SetEnvironmentVariable('TEMP', 'system-temp'); [Environment]::SetEnvironmentVariable('TMP', 'system-tmp')
        $env:SIMOSA_SYSTEM_TEMP = '1'
        Set-LaunchProjectEnv $fakeRoot
        Assert-True ($env:TEMP -eq 'system-temp' -and $env:TMP -eq 'system-tmp') "SIMOSA_SYSTEM_TEMP=1 時不應改 TEMP/TMP:$($env:TEMP) / $($env:TMP)"
    } finally { foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) } }
}

Invoke-Test 'Invoke-LaunchWithGodotUserDir:啟動期間 APPDATA/LOCALAPPDATA 在根目錄內(子程序繼承),結束後還原;動作丟例外也還原' {
    . (Join-Path $launch 'launch-common.ps1')
    $savedA = $env:APPDATA; $savedL = $env:LOCALAPPDATA
    try {
        $fakeRoot = Join-Path $tmp 'godotroot'
        Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $fakeRoot | Out-Null
        $env:APPDATA = 'orig-roaming'; $env:LOCALAPPDATA = 'orig-local'
        $seen = @{}
        # 子程序(視景執行檔的替身)在啟動當下繼承環境變數
        Invoke-LaunchWithGodotUserDir $fakeRoot {
            $seen['inner'] = $env:APPDATA; $seen['innerLocal'] = $env:LOCALAPPDATA
            $seen['child'] = if ($IsWindows) { (& cmd /c 'echo %APPDATA%' | Out-String).Trim() } else { (& sh -c 'printf %s "$APPDATA"' | Out-String).Trim() }
        }
        $norm = { param($p) ([string]$p) -replace '\\', '/' }
        $base = & $norm $fakeRoot
        Assert-True ((& $norm $seen['inner']) -eq "$base/.cache/godot/appdata/roaming" -and (& $norm $seen['innerLocal']) -eq "$base/.cache/godot/appdata/local") "動作期間應指到根目錄內:$($seen['inner']) / $($seen['innerLocal'])"
        Assert-True ((& $norm $seen['child']) -eq "$base/.cache/godot/appdata/roaming") "子程序應繼承導向後的 APPDATA:$($seen['child'])"
        Assert-True (Test-Path $seen['inner']) '應建立 .cache/godot/appdata/roaming'
        Assert-True ($env:APPDATA -eq 'orig-roaming' -and $env:LOCALAPPDATA -eq 'orig-local') "結束後應還原:$($env:APPDATA) / $($env:LOCALAPPDATA)"
        $threw = $false
        try { Invoke-LaunchWithGodotUserDir $fakeRoot { throw '啟動失敗' } } catch { $threw = $true }
        Assert-True ($threw -and $env:APPDATA -eq 'orig-roaming' -and $env:LOCALAPPDATA -eq 'orig-local') "動作丟例外時也應還原:$($env:APPDATA) / $($env:LOCALAPPDATA)"
    } finally { $env:APPDATA = $savedA; $env:LOCALAPPDATA = $savedL }
}

Invoke-Test '瀏覽器端資料位置:Get-LaunchBrowserProfile 在根目錄內;Edge 參數含 --user-data-dir 且路徑加引號' {
    . (Join-Path $launch 'launch-common.ps1')
    $fakeRoot = Join-Path $tmp 'Simosa BRM'
    $p = Get-LaunchBrowserProfile $fakeRoot
    $rel = ($p.Substring($fakeRoot.Length) -replace '\\', '/')
    Assert-True ($p.StartsWith($fakeRoot) -and $rel -eq '/build/browser/edge') "設定檔應在 <根目錄>/build/browser/edge:$p"
    $a = Get-LaunchEdgeArguments $p 'http://127.0.0.1:8080/instructor.html?mode=simple'
    Assert-True ($a.Count -eq 4 -and $a[0] -eq "--user-data-dir=`"$p`"" -and $a[1] -eq '--no-first-run' -and $a[2] -eq '--no-default-browser-check' -and $a[3] -eq '"http://127.0.0.1:8080/instructor.html?mode=simple"') "Edge 參數:$($a -join ' ')"
    # 第一次建立設定檔時預先寫入下載位置(<根目錄>/build/downloads);Preferences 為無 BOM 的 UTF-8 JSON;已存在時不覆寫
    Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
    Initialize-LaunchBrowserProfile $p
    $prefsPath = Join-Path (Join-Path $p 'Default') 'Preferences'
    $downloads = Join-Path (Join-Path $fakeRoot 'build') 'downloads'
    Assert-True ((Test-Path $prefsPath) -and (Test-Path $downloads)) "應建立 Default/Preferences 與 build/downloads:$prefsPath"
    $bytes = [System.IO.File]::ReadAllBytes($prefsPath)
    Assert-True ($bytes[0] -eq 0x7B) "Preferences 應為無 BOM 的 JSON(第一個位元組 '{'):0x$('{0:X2}' -f $bytes[0])"
    $prefs = Get-Content -Raw -Encoding UTF8 $prefsPath | ConvertFrom-Json
    Assert-True ($prefs.download.default_directory -eq $downloads -and $prefs.download.prompt_for_download -eq $false -and $prefs.savefile.default_directory -eq $downloads) "下載位置應為 ${downloads};實際:$(Get-Content -Raw $prefsPath)"
    # 既有設定檔:使用者改到根目錄內其他資料夾 → 不動(逐位元相同)
    $userChoice = (Join-Path (Join-Path $fakeRoot 'data') 'exports')
    $esc = { param($s) ([string]$s).Replace('\', '\\') }
    $keep = '{"browser":{"x":1},"download":{"default_directory":"' + (& $esc $userChoice) + '","prompt_for_download":true},"savefile":{"default_directory":"' + (& $esc $downloads) + '"}}'
    [System.IO.File]::WriteAllText($prefsPath, $keep, (New-Object System.Text.UTF8Encoding($false)))
    Initialize-LaunchBrowserProfile $p
    Assert-True ([System.IO.File]::ReadAllText($prefsPath) -eq $keep) '下載位置在根目錄內的既有 Preferences 不應被改寫'
    # 既有設定檔:下載位置指到根目錄外(專案資料夾換了磁碟代號或整個複製後的舊路徑)→ 只改寫這兩個值,其餘內容與「無 BOM」不變
    $oldRoot = Join-Path $tmp 'old drive'
    $oldDl = Join-Path (Join-Path $oldRoot 'build') 'downloads'
    $stale = '{"browser":{"x":1},"download":{"default_directory":"' + (& $esc $oldDl) + '","prompt_for_download":true},"savefile":{"default_directory":"' + (& $esc $oldDl) + '"},"zz":"中文不受影響"}'
    [System.IO.File]::WriteAllText($prefsPath, $stale, (New-Object System.Text.UTF8Encoding($false)))
    $msg = (Initialize-LaunchBrowserProfile $p 6>&1 | Out-String)
    $after = [System.IO.File]::ReadAllText($prefsPath)
    $expect = $stale.Replace((& $esc $oldDl), (& $esc $downloads))
    Assert-True ($after -eq $expect) "指到根目錄外的下載位置應改回 build/downloads,其餘不變:$after"
    Assert-True ([System.IO.File]::ReadAllBytes($prefsPath)[0] -eq 0x7B) '改寫後仍應為無 BOM 的 JSON'
    Assert-True ($msg.Contains('下載位置')) "改寫時應說明:$msg"
    # 與平台無關的案例:Preferences 內是 Windows 樣式的字面路徑(JSON 內反斜線加倍、結尾帶反斜線、含空白與中文)— 在 Linux 上也屬根目錄外,
    # 走到「還原 \\ → 判斷 → 改寫」的分支(上面的案例在 Linux 上路徑沒有反斜線,這段邏輯等於沒執行)
    $winStale = '{"download":{"default_directory":"D:\\old drive\\訓練\\build\\downloads\\","directory_upgrade":true},"note":"\"default_directory\":\"x\"","savefile":{"default_directory":"D:\\old drive\\訓練\\build\\downloads"}}'
    [System.IO.File]::WriteAllText($prefsPath, $winStale, (New-Object System.Text.UTF8Encoding($false)))
    Initialize-LaunchBrowserProfile $p 6>$null
    $winAfter = [System.IO.File]::ReadAllText($prefsPath)
    $dlJson = (& $esc $downloads)
    $winExpect = '{"download":{"default_directory":"' + $dlJson + '","directory_upgrade":true},"note":"\"default_directory\":\"x\"","savefile":{"default_directory":"' + $dlJson + '"}}'
    Assert-True ($winAfter -eq $winExpect) "Windows 樣式的舊路徑應改回 build/downloads,字串內被跳脫的同名文字不動:$winAfter"
    # 找不到 Edge(非 Windows 的 pwsh 一律如此):不自動退回預設瀏覽器(資料與下載會落到 C:\Users,須先經同意)— 只警告並列出網址,不開啟任何東西
    if (-not $IsWindows) {
        Assert-True ($null -eq (Find-LaunchEdge)) '非 Windows 應回傳 $null'
        $script:started = @()
        function Start-Process { $script:started += ,@($args) }
        try {
            $warn = (Open-LaunchPages 'http://127.0.0.1:18080' @('instructor.html?mode={m}', 'report.html') @{ m = 'simple' } $p 3>&1 | Out-String)
        } finally { Remove-Item Function:\Start-Process }
        Assert-True ($script:started.Count -eq 0) "找不到 Edge 時不應開啟任何瀏覽器:$($script:started.Count) 次"
        Assert-True ($warn.Contains('-DefaultBrowser') -and $warn.Contains('-Shell') -and $warn.Contains('http://127.0.0.1:18080/instructor.html?mode=simple') -and $warn.Contains('http://127.0.0.1:18080/report.html')) "警告應說明處理方式並列出網址:$warn"
        # -DefaultBrowser(ProfileDir 為空):用預設瀏覽器開啟每個網址
        function Start-Process { $script:started += ,@($args) }
        try { Open-LaunchPages 'http://127.0.0.1:18080' @('report.html') @{} '' 6>$null } finally { Remove-Item Function:\Start-Process }
        Assert-True ($script:started.Count -eq 1 -and "$($script:started[0][0])" -eq 'http://127.0.0.1:18080/report.html') "ProfileDir 為空時應以預設瀏覽器開啟:$($script:started | ForEach-Object { $_ -join ' ' })"
    }
}

Invoke-Test 'Find-LaunchDll:尚未建置時先建置,回傳值只有 DLL 路徑(建置輸出不可混進回傳值)' {
    . (Join-Path $launch 'launch-common.ps1')
    $fakeRoot = Join-Path $tmp 'dllroot'
    Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
    $projDir = Join-Path (Join-Path $fakeRoot 'src') 'Fake.Tool'
    New-Item -ItemType Directory -Force -Path $projDir | Out-Null
    Set-Content -Path (Join-Path $projDir 'Fake.Tool.csproj') -Value '<Project />' -Encoding ascii
    $expected = Join-Path $fakeRoot 'build\bin\Fake.Tool\Release\net8.0\Fake.Tool.dll'
    # dotnet 的替身:像真的 dotnet build 一樣把訊息寫到標準輸出,並產生 DLL
    $script:fakeBuilds = 0
    function dotnet {
        $script:fakeBuilds++
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $expected) | Out-Null
        Set-Content -Path $expected -Value 'x' -Encoding ascii
        $global:LASTEXITCODE = 0
        'Build succeeded.'; '    0 Warning(s)'; '    0 Error(s)'
    }
    try {
        $dll = Find-LaunchDll 'faketool' 'Fake.Tool' 'Fake.Tool.dll' $fakeRoot 6>$null
        Assert-True ($script:fakeBuilds -eq 1) "找不到 DLL 且有 csproj 時應建置一次,實際 $($script:fakeBuilds)"
        Assert-True ($dll -is [string]) "回傳值應為單一字串,實際型別 $($dll.GetType().Name):$($dll -join ' | ')"
        Assert-True ($dll -eq $expected) "回傳值應為建置出的 DLL 路徑:$dll"
        # 已建置:直接回傳,不再建置
        $dll2 = Find-LaunchDll 'faketool' 'Fake.Tool' 'Fake.Tool.dll' $fakeRoot
        Assert-True ($script:fakeBuilds -eq 1 -and $dll2 -eq $expected) '已有 DLL 時不應再建置'
    } finally { Remove-Item Function:\dotnet }
}

# ---- USB 操控台服務(src\Contracts\console-serial.md;start-ship-laptop.ps1 -Console)----
Invoke-Test 'Get-LaunchConsoleArgs:序列埠與 loopback 規格;loopback 假裝置實跑服務回寫 ACK/LED' {
    . (Join-Path $launch 'launch-common.ps1')
    $a = Get-LaunchConsoleArgs 'COM5' 18765
    Assert-True (($a -join ' ') -eq '--port COM5 --baud 115200 --ws ws://127.0.0.1:18765 --log -') "序列埠規格應為 --port/--baud/--ws/--log -:$($a -join ' ')"
    $a = Get-LaunchConsoleArgs '/dev/ttyACM0' 8765 9600
    Assert-True (($a -join ' ') -eq '--port /dev/ttyACM0 --baud 9600 --ws ws://127.0.0.1:8765 --log -') "鮑率應可覆寫:$($a -join ' ')"
    $a = Get-LaunchConsoleArgs 'loopback:in.txt,out.txt' 18765
    Assert-True (($a -join ' ') -eq '--loopback in.txt,out.txt --ws ws://127.0.0.1:18765 --log -') "loopback 規格應為 --loopback <讀,寫>:$($a -join ' ')"
    # 實跑服務(需建置好的 Console.Serial;Find-LaunchDll 缺少時會建置):無核心 → LED,LINK,0;HELLO 宣告 CS → 回寫帶校驗和
    $dll = Find-LaunchDll 'console' 'Console.Serial' 'SimosaBRM.Console.Serial.dll' $root
    Assert-True ([bool]$dll) '應找到或建置 SimosaBRM.Console.Serial.dll'
    $logs = 'build\test-tmp\launch-ps\logs6'
    $logDir = Join-Path $root $logs
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    Initialize-LaunchLogs $root $logs
    $inFile = Join-Path $tmp 'console-in.txt'; $outFile = Join-Path $tmp 'console-out.txt'
    Remove-Item $outFile -ErrorAction SilentlyContinue
    Set-Content -Path $inFile -Value "HELLO,1.0.0,RUD+LED+DISP+CS*48`nRUD,10*6E`n" -Encoding ascii -NoNewline
    $args = @($dll) + (Get-LaunchConsoleArgs "loopback:$inFile,$outFile" ($PortBase + 9)) + @('--duration', '3')
    $p = Start-LaunchProcess -Name 'console' -Log (Join-Path $logDir 'console.log') -File 'dotnet' -Arguments $args -WorkDir $root
    $p.WaitForExit()
    Assert-True ($p.ExitCode -eq 0) "服務應以 0 結束(--duration),實際 $($p.ExitCode):$(Get-Content -Raw (Join-Path $logDir 'console.log'))"
    $out = @(Get-Content $outFile | Where-Object { $_ })
    Assert-True ($out -contains 'ACK*49') "回寫應有帶校驗和的 ACK*49:$($out -join '、')"
    Assert-True ($out -contains 'LED,LINK,0*7D') "無核心時應回寫 LED,LINK,0(帶校驗和):$($out -join '、')"
    $log = Get-Content -Raw (Join-Path $logDir 'console.log')
    Assert-True ($log.Contains('HELLO 韌體 1.0.0') -and $log.Contains('"type":"rudder","value":10')) "console.log 應有 HELLO 與轉送的 rudder 指令:$log"
    Assert-True ((Get-Content (Join-Path $logDir 'simosa.pids') | Where-Object { $_ }) -like 'console:*') 'simosa.pids 應登錄 console'
}

# ---- NMEA 選項與 VHF/錄影埠預檢(第二十四輪;config\ship-laptop.json ports.nmeaTargets/nmeaBind/nmeaFile、vhfWs/mediaHttp)----
Invoke-Test 'Get-LaunchNmeaArgs:nmeaTargets/nmeaBind/nmeaFile → Host 參數;空陣列/null 不加;Assert-LaunchPortsFree 回報 VHF 埠' {
    . (Join-Path $launch 'launch-common.ps1')
    $cfg = '{"ports":{"nmeaTargets":["192.168.77.21:10110","192.168.77.22"],"nmeaBind":"192.168.77.11","nmeaFile":"build/nmea/E05.nmea"}}' | ConvertFrom-Json
    $a = @(Get-LaunchNmeaArgs $cfg)
    Assert-True (($a -join ' ') -eq '--nmea-target 192.168.77.21:10110 --nmea-target 192.168.77.22 --nmea-bind 192.168.77.11 --nmea-file build/nmea/E05.nmea') "參數組合不符:$($a -join ' ')"
    $empty = '{"ports":{"nmeaTargets":[],"nmeaBind":null,"nmeaFile":null}}' | ConvertFrom-Json
    Assert-True (@(Get-LaunchNmeaArgs $empty).Count -eq 0) "空陣列/null 不應產生參數:$(@(Get-LaunchNmeaArgs $empty) -join ' ')"
    Assert-True (@(Get-LaunchNmeaArgs ('{"ports":{}}' | ConvertFrom-Json)).Count -eq 0) '沒有 nmea 鍵時不應產生參數'
    $one = '{"ports":{"nmeaTargets":["127.0.0.1:19110"]}}' | ConvertFrom-Json
    Assert-True ((@(Get-LaunchNmeaArgs $one) -join ' ') -eq '--nmea-target 127.0.0.1:19110') '單一目標、無 bind/file'
    $hostArgs = @('x.dll', '--port', '8765'); $hostArgs += @(Get-LaunchNmeaArgs $empty)
    Assert-True ($hostArgs.Count -eq 3) "空輸出時 += @(…) 不得加入 `$null/空元素(實際 $($hostArgs.Count) 個)"
    $hostArgs += @(Get-LaunchNmeaArgs $cfg)
    Assert-True ($hostArgs.Count -eq 11 -and $hostArgs[3] -eq '--nmea-target' -and $hostArgs[10] -eq 'build/nmea/E05.nmea') "有參數時應逐一加入 8 個(實際共 $($hostArgs.Count) 個)"
    # 儲存庫的 config:預設不產生 NMEA 參數;VHF/錄影埠預設 8767/8768;教官 config 的 nmeaUdp 為 null(standalone 時 --no-nmea)
    $ship = Read-LaunchConfig (Join-Path $root 'config\ship-laptop.json')
    Assert-True (@(Get-LaunchNmeaArgs $ship).Count -eq 0) 'config\ship-laptop.json 預設(nmeaTargets 空、nmeaBind/nmeaFile null)不應產生參數'
    Assert-True ((Get-Cfg $ship 'ports.vhfWs') -eq 8767 -and (Get-Cfg $ship 'ports.mediaHttp') -eq 8768) 'config\ship-laptop.json 應有 ports.vhfWs 8767 與 ports.mediaHttp 8768'
    $instr = Read-LaunchConfig (Join-Path $root 'config\instructor-laptop.json')
    Assert-True ($null -eq (Get-Cfg $instr 'ports.nmeaUdp') -and @(Get-LaunchNmeaArgs $instr).Count -eq 0) 'config\instructor-laptop.json 預設 nmeaUdp null、無 NMEA 參數'
    # 啟動腳本把 VHF/錄影埠列入預檢並傳 --vhf-port/--media-port;操控台不在預檢清單
    $ps1 = Get-Content -Raw (Join-Path $launch 'start-ship-laptop.ps1')
    Assert-True ($ps1.Contains("'VHF ws' = `$vhfPort") -and $ps1.Contains("'錄影 HTTP' = `$mediaPort")) 'start-ship-laptop.ps1 應把 VHF ws 與錄影 HTTP 列入埠預檢'
    Assert-True ($ps1.Contains("@('ports.vhfWs', '--vhf-port')") -and $ps1.Contains("@('ports.mediaHttp', '--media-port')") -and $ps1.Contains('$hostArgs += @(Get-LaunchNmeaArgs $cfg)')) 'start-ship-laptop.ps1 應傳 --vhf-port/--media-port 與 NMEA 參數(以 @() 包住,空輸出時不加 $null)'
    Assert-True (-not $ps1.Contains("`$ports['操控台']")) '操控台服務無監聽埠,不應在預檢清單'
    # Assert-LaunchPortsFree:VHF 埠被占用時訊息含名稱
    $port = $PortBase + 3
    $l = New-Object System.Net.Sockets.TcpListener ([System.Net.IPAddress]::Loopback), $port
    $l.Start()
    try {
        $msg = $null
        try { Assert-LaunchPortsFree @{ 'SimCore ws' = ($PortBase + 4); 'VHF ws' = $port; '錄影 HTTP' = ($PortBase + 5) } } catch { $msg = $_.Exception.Message }
        Assert-True ($msg -and $msg.Contains("埠 $port 已被占用(VHF ws")) "應以名稱回報 VHF 埠被占用:$msg"
    } finally { $l.Stop() }
    Assert-LaunchPortsFree @{ 'VHF ws' = $port; '錄影 HTTP' = ($PortBase + 5) }
}

Invoke-Test 'Get-LaunchGitInfo:有 git 時乾淨 / 修改 / 未追蹤檔;-NoGit 時讀 .git(鬆散 ref、packed-refs、worktree 的 .git 檔與 commondir、不是儲存庫)' {
    . (Join-Path $launch 'launch-common.ps1')
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Host '    略過:沒有 git'; return }
    $d = Join-Path $tmp 'gitinfo'
    Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
    $repo = Join-Path $d 'repo'; $wt = Join-Path $d 'wt'; $plain = Join-Path $d 'plain'
    New-Item -ItemType Directory -Force -Path $repo, $plain | Out-Null
    $g = @('-c', 'user.name=test', '-c', 'user.email=test@example.invalid', '-c', 'init.defaultBranch=main', '-c', 'core.hooksPath=/dev/null')
    & git @g -C $repo init -q; Set-Content -Path (Join-Path $repo 'a.txt') -Value 'a'; & git @g -C $repo add a.txt; & git @g -C $repo commit -q -m init
    $head = (& git -C $repo rev-parse HEAD).Trim()
    $i = Get-LaunchGitInfo $repo
    Assert-True ($i.Rev -eq $head -and $i.Dirty -eq $false) "乾淨:$($i.Rev) / $($i.Dirty)"
    Add-Content -Path (Join-Path $repo 'a.txt') -Value 'b'
    Assert-True ((Get-LaunchGitInfo $repo).Dirty -eq $true) '修改追蹤檔應為 Dirty'
    & git -C $repo checkout -q -- a.txt; Set-Content -Path (Join-Path $repo 'new.txt') -Value 'n'
    Assert-True ((Get-LaunchGitInfo $repo).Dirty -eq $true) '未追蹤檔應為 Dirty'
    Remove-Item (Join-Path $repo 'new.txt')
    $i = Get-LaunchGitInfo $repo -NoGit
    Assert-True ($i.Rev -eq $head -and $null -eq $i.Dirty) "-NoGit(鬆散 ref):$($i.Rev) / $($i.Dirty)"
    & git -C $repo pack-refs --all
    Assert-True (-not (Test-Path (Join-Path $repo '.git/refs/heads/main'))) 'pack-refs 後鬆散 ref 應已移除'
    Assert-True ((Get-LaunchGitInfo $repo -NoGit).Rev -eq $head) '-NoGit(packed-refs)應讀到 HEAD'
    & git -C $repo worktree add -q -b wt $wt 2>$null
    Assert-True (Test-Path (Join-Path $wt '.git') -PathType Leaf) 'worktree 的 .git 應是檔案'
    $i = Get-LaunchGitInfo $wt
    Assert-True ($i.Rev -eq $head -and $i.Dirty -eq $false) "worktree(git):$($i.Rev) / $($i.Dirty)"
    $i = Get-LaunchGitInfo $wt -NoGit
    Assert-True ($i.Rev -eq $head -and $null -eq $i.Dirty) "worktree(-NoGit):$($i.Rev) / $($i.Dirty)"
    $i = Get-LaunchGitInfo $plain -NoGit
    Assert-True ($null -eq $i.Rev -and $null -eq $i.Dirty) "不是儲存庫:$($i.Rev) / $($i.Dirty)"
    & git -C $repo worktree remove --force $wt 2>$null
    Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue
}

# ---- 發行版狀態(第三十輪;docs\design\packaging-sync.md 第 5.1 節;start-*-laptop.ps1 在啟動網頁伺服前呼叫)----
Invoke-Test '發行版狀態:狀態檔位置、安裝版才取(不建置)、Write-LaunchReleaseStatus 成功寫檔並記 launch.log;失敗 / 逾時 / 不是 JSON 不丟例外、刪掉舊檔' {
    . (Join-Path $launch 'launch-common.ps1')
    $norm = { param($p) ([string]$p) -replace '\\', '/' }
    $fakeRoot = Join-Path $tmp 'relroot'
    Remove-Item -Recurse -Force $fakeRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $fakeRoot 'data') | Out-Null
    $statusPath = Get-LaunchReleaseStatusPath $fakeRoot
    Assert-True ((& $norm $statusPath) -eq ((& $norm $fakeRoot) + '/build/run/release-status.json')) "狀態檔應為 <根目錄>/build/run/release-status.json:$statusPath"
    $logDir = Join-Path (Join-Path $fakeRoot 'build') 'logs'
    # 開發模式(沒有 manifest.json;儲存庫本身也是):沒有工具,不產生檔、不輸出、不警告
    Assert-True ($null -eq (Find-LaunchReleaseStatusTool $fakeRoot)) '沒有 manifest.json 時不應有發行版狀態工具'
    Assert-True (-not (Test-Path (Join-Path $root 'manifest.json'))) '測試前置:儲存庫根目錄不應有 manifest.json(開發模式)'
    Assert-True ($null -eq (Find-LaunchReleaseStatusTool $root)) '儲存庫(開發模式)不應有發行版狀態工具(即使 simosa-pkg 已建置)'
    $quiet = (Write-LaunchReleaseStatus $fakeRoot $logDir 3>&1 6>&1 | Out-String)
    Assert-True ($quiet.Trim() -eq '' -and -not (Test-Path $statusPath)) "開發模式應不輸出、不產生檔:$quiet"
    # 安裝版:manifest.json(以真實的 simosa-pkg 產生)
    $pkgDll = Find-LaunchDll 'simosa-pkg' 'Tools.Packaging.Cli' 'simosa-pkg.dll' $root
    Assert-True ([bool]$pkgDll) '應找到或建置 simosa-pkg.dll'
    $pkgDll = (Resolve-Path $pkgDll).Path
    & dotnet $pkgDll release manifest --dir $fakeRoot --version 2026.11.30 | Out-Null
    Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $fakeRoot 'manifest.json'))) 'release manifest 應產生 manifest.json'
    # 有 manifest.json 但找不到 simosa-pkg:警告、不產生檔;tools\updater 有 simosa-pkg.dll 時回傳它(優先於開發建置輸出)
    Assert-True ($null -eq (Find-LaunchReleaseStatusTool $fakeRoot)) '有 manifest.json 但沒有 simosa-pkg.dll 時應為 $null'
    $warn = (Write-LaunchReleaseStatus $fakeRoot $logDir 3>&1 | Out-String)
    Assert-True ($warn.Contains('找不到') -and $warn.Contains('simosa-pkg.dll') -and -not (Test-Path $statusPath)) "找不到 simosa-pkg 時應警告、不產生檔:$warn"
    $devDll = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $fakeRoot 'build') 'bin') 'Tools.Packaging.Cli') 'Release') 'net8.0') 'simosa-pkg.dll'
    $instDll = Join-Path (Join-Path (Join-Path $fakeRoot 'tools') 'updater') 'simosa-pkg.dll'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $devDll), (Split-Path -Parent $instDll) | Out-Null
    Set-Content -Path $devDll -Value 'x' -Encoding ascii
    Assert-True ((& $norm (Find-LaunchReleaseStatusTool $fakeRoot)) -eq (& $norm $devDll)) '沒有 tools\updater 時用已建置的開發輸出'
    Set-Content -Path $instDll -Value 'x' -Encoding ascii
    Assert-True ((& $norm (Find-LaunchReleaseStatusTool $fakeRoot)) -eq (& $norm $instDll)) 'tools\updater\simosa-pkg.dll 優先'
    Remove-Item -Force $devDll, $instDll
    # 成功(-PkgDll 指到建置好的 simosa-pkg):回傳狀態檔路徑;檔案是 status --json 的輸出(UTF-8、無 BOM);launch.log 記一行;不留暫存檔
    $info = (Write-LaunchReleaseStatus $fakeRoot $logDir -PkgDll $pkgDll 6>&1 | Out-String)
    Assert-True ($info.Contains('發行版狀態:發行版 2026.11.30')) "應回報發行版 2026.11.30:$info"
    $r = Write-LaunchReleaseStatus $fakeRoot $logDir -PkgDll $pkgDll 6>$null
    Assert-True ($r -is [string] -and (& $norm $r) -eq (& $norm $statusPath)) "回傳值應為狀態檔路徑(單一字串):$r"
    $bytes = [System.IO.File]::ReadAllBytes($statusPath)
    Assert-True ($bytes[0] -eq 0x7B) "狀態檔應為無 BOM 的 JSON(第一個位元組 '{'):0x$('{0:X2}' -f $bytes[0])"
    $st = Get-Content -Raw -Encoding UTF8 $statusPath | ConvertFrom-Json
    Assert-True ($st.type -eq 'statusResult' -and $st.releaseVersion -eq '2026.11.30') "狀態檔應是 status 的輸出、releaseVersion 2026.11.30:$(Get-Content -Raw $statusPath)"
    Assert-True (@($st.warnings).Count -ge 1 -and ("$($st.warnings[0])" -match '[一-鿿]')) "中文(warnings)應原樣保留、不是亂碼:$($st.warnings -join '、')"
    Assert-True ((Get-Content -Raw -Encoding UTF8 (Join-Path $logDir 'launch.log')).Contains('release-status 發行版 2026.11.30')) 'launch.log 應記一行'
    Assert-True (-not (Test-Path ($statusPath + '.tmp'))) '不應留下暫存檔'
    # 結束碼非 0(simosa-pkg.dll 不存在):不丟例外、回傳 $null、警告,上一次的狀態檔被刪掉(不顯示過期的版本)
    $threw = $false; $r2 = 'x'; $warn2 = ''
    try { $warn2 = (Write-LaunchReleaseStatus $fakeRoot $logDir -PkgDll (Join-Path $tmp 'nope.dll') 3>&1 | Out-String) } catch { $threw = $true }
    Assert-True (-not $threw) '失敗時不應丟例外(不擋住啟動)'
    Assert-True ($warn2.Contains('結束碼') -and -not (Test-Path $statusPath)) "失敗時應警告並刪掉舊的狀態檔:$warn2"
    $r2 = Write-LaunchReleaseStatus $fakeRoot $logDir -PkgDll (Join-Path $tmp 'nope.dll') 3>$null
    Assert-True ($null -eq $r2) "失敗時回傳值應為 `$null:$r2"
    if (-not $IsWindows) {
        # 逾時與輸出不是 JSON:以 PATH 最前面的假 dotnet(sh 腳本)模擬;Windows 的 CreateProcess 不會把 .cmd 當成 dotnet,略過
        $fakeBin = Join-Path $tmp 'fakebin'
        New-Item -ItemType Directory -Force -Path $fakeBin | Out-Null
        $fakeDotnet = Join-Path $fakeBin 'dotnet'
        $savedPath = $env:PATH
        try {
            Set-Content -Path $fakeDotnet -Value "#!/bin/sh`nexec sleep 30`n" -Encoding ascii -NoNewline
            & chmod +x $fakeDotnet
            Set-Content -Path $statusPath -Value '{"stale":true}' -Encoding ascii
            $env:PATH = $fakeBin + [System.IO.Path]::PathSeparator + $savedPath
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $warn3 = (Write-LaunchReleaseStatus $fakeRoot $logDir -TimeoutSeconds 1 -PkgDll $pkgDll 3>&1 | Out-String)
            $sw.Stop()
            Assert-True ($warn3.Contains('超過 1 秒') -and -not (Test-Path $statusPath)) "逾時應警告並刪掉舊的狀態檔:$warn3"
            Assert-True ($sw.Elapsed.TotalSeconds -lt 10) "逾時 1 秒應很快返回(耗時 $([int]$sw.Elapsed.TotalSeconds) s)"
            Set-Content -Path $fakeDotnet -Value "#!/bin/sh`necho not-json`n" -Encoding ascii -NoNewline
            $warn4 = (Write-LaunchReleaseStatus $fakeRoot $logDir -PkgDll $pkgDll 3>&1 | Out-String)
            Assert-True ($warn4.Contains('無法取得') -and -not (Test-Path $statusPath) -and -not (Test-Path ($statusPath + '.tmp'))) "輸出不是 JSON 時應警告、不留檔:$warn4"
        } finally { $env:PATH = $savedPath }
        $log = Get-Content -Raw -Encoding UTF8 (Join-Path $logDir 'launch.log')
        Assert-True ($log.Contains('release-status 逾時') -and $log.Contains('release-status 無法取得')) "launch.log 應記下逾時與失敗:$log"
    }
    # 啟動腳本:啟動網頁伺服前取狀態,並把狀態檔交給 simosa-web(--status-file);-CheckOnly 列出發行版狀態的來源
    foreach ($name in @('start-ship-laptop.ps1', 'start-instructor-laptop.ps1')) {
        $ps1 = Get-Content -Raw -Encoding UTF8 (Join-Path $launch $name)
        $iWrite = $ps1.IndexOf('Write-LaunchReleaseStatus $root $logDir | Out-Null')
        $iWeb = $ps1.IndexOf("'--status-file', (Get-LaunchReleaseStatusPath `$root)")
        Assert-True ($iWrite -gt 0 -and $iWeb -gt $iWrite) "$name 應先 Write-LaunchReleaseStatus,再以 --status-file 啟動 simosa-web"
    }
    # -CheckOnly 在同一個工作階段執行(腳本會改目前目錄與 TEMP / NuGet 等環境變數:執行後還原)
    $envNames = @('SIMOSA_BRM_ROOT', 'SIMOSA_INSTALL_ROOT', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH', 'TEMP', 'TMP')
    $savedEnv = @{}; foreach ($n in $envNames) { $savedEnv[$n] = [Environment]::GetEnvironmentVariable($n) }
    $savedLocation = Get-Location
    try { $checkOut = (& (Join-Path $launch 'start-ship-laptop.ps1') -CheckOnly 3>&1 6>&1 | Out-String) }
    finally { Set-Location $savedLocation; foreach ($n in $envNames) { [Environment]::SetEnvironmentVariable($n, $savedEnv[$n]) } }
    Assert-True ($checkOut.Contains('SimCore.Host:')) "-CheckOnly 應列出找到的元件:$checkOut"
    Assert-True ($checkOut.Contains('發行版狀態:略過(開發模式')) "-CheckOnly 在開發模式應印「發行版狀態:略過」:$checkOut"
}

Invoke-Test '船上筆電的網頁位址:Get-LaunchShipWebUrl 與 Add-LaunchShipWebParam(教官站網址的 shipWeb 參數)' {
    . (Join-Path $launch 'launch-common.ps1')
    # 直連模式而且有設船上網頁埠(1–65535)才有位址;standalone(核心在本機)、沒設、不是埠 → 空字串(教官站由 ws 的主機推得 http://<船上 IP>:8080)
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' 8081) -eq 'http://192.168.77.11:8081') '直連 + 埠 8081'
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' '8081') -eq 'http://192.168.77.11:8081') '埠是字串也可以'
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' 65535) -eq 'http://192.168.77.11:65535') '埠 65535'
    foreach ($bad in @($null, '', 'abc', 0, 65536, '08081', '80;x', -1, 8081.5, [double]8081.0, [decimal]8081.0, $true)) {
        $r = Get-LaunchShipWebUrl $false '192.168.77.11' $bad
        Assert-True ($r -eq '') "埠「${bad}」不是 1–65535:應為空字串,實際 $r"
    }
    # 審查 S-5:JSON 的 8081.0(pwsh 7 是 Double、Windows PowerShell 5.1 是 Decimal;"$值" 會變成「8081」)、陣列、物件都不是埠 —— 與 launch-common.sh 相同
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' @(8081)) -eq '') '埠是陣列 [8081]:應為空字串'
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' ([pscustomobject]@{ port = 8081 })) -eq '') '埠是物件:應為空字串'
    # Get-LaunchShipWebPort:設定檔的 ports.shipWeb → Port(合格的整數,否則 0)與 Invalid(不合格時原值的文字;啟動腳本據此警告)
    $portCases = [ordered]@{
        '{"ports":{"shipWeb":8081}}'            = '8081|'
        '{"ports":{"shipWeb":"8081"}}'          = '8081|'
        '{"ports":{"shipWeb":65535}}'           = '65535|'
        '{"ports":{"shipWeb":null}}'            = '0|'
        '{"ports":{"shipWeb":""}}'              = '0|'
        '{"ports":{"web":8080}}'                = '0|'
        '{}'                                    = '0|'
        '{"ports":{"shipWeb":8081.0}}'          = '0|8081.0'
        '{"ports":{"shipWeb":[8081]}}'          = '0|陣列或物件'
        '{"ports":{"shipWeb":[]}}'              = '0|陣列或物件'
        '{"ports":{"shipWeb":{"port":8081}}}'   = '0|陣列或物件'
        '{"ports":{"shipWeb":true}}'            = '0|true'
        '{"ports":{"shipWeb":"abc"}}'           = '0|abc'
        '{"ports":{"shipWeb":0}}'               = '0|0'
        '{"ports":{"shipWeb":65536}}'           = '0|65536'
        '{"ports":{"shipWeb":-1}}'              = '0|-1'
        '{"ports":{"shipWeb":" 8081"}}'         = '0| 8081'
    }
    foreach ($json in $portCases.Keys) {
        $r = Get-LaunchShipWebPort ($json | ConvertFrom-Json)
        Assert-True ("$($r.Port)|$($r.Invalid)" -ceq $portCases[$json]) "Get-LaunchShipWebPort ${json} 應為「$($portCases[$json])」,實際「$($r.Port)|$($r.Invalid)」"
        Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' $r.Port) -eq $(if ($r.Port) { "http://192.168.77.11:$($r.Port)" } else { '' })) "Get-LaunchShipWebUrl 對 ${json} 的 Port"
    }
    Assert-True ((Get-LaunchShipWebUrl $true '192.168.77.11' 8081) -eq '') 'standalone 不取船上的版本'
    Assert-True ((Get-LaunchShipWebUrl $false '' 8081) -eq '') '沒有船上 IP'
    # 只有教官站頁面、而且還沒有 shipWeb 參數時才加;其他頁面、位址是空的原樣回傳
    $u = 'http://192.168.77.11:8081'
    $cases = [ordered]@{
        'instructor.html?ws=ws://{ship}:{stateWs}'          = 'instructor.html?ws=ws://{ship}:{stateWs}&shipWeb=http://192.168.77.11:8081'
        'instructor.html'                                    = 'instructor.html?shipWeb=http://192.168.77.11:8081'
        'instructor.html?mode=simple&shipWeb=http://x:1'     = 'instructor.html?mode=simple&shipWeb=http://x:1'
        'instructor.html?shipWeb=0&ws=ws://a:1'              = 'instructor.html?shipWeb=0&ws=ws://a:1'
        'radar.html?radar=x&ws=ws://{ship}:{radarWs}'        = 'radar.html?radar=x&ws=ws://{ship}:{radarWs}'
        'report.html'                                        = 'report.html'
        'xinstructor.html?a=1'                               = 'xinstructor.html?a=1'
        'instructor.htmlx'                                   = 'instructor.htmlx'
        'index.html?next=instructor.html'                    = 'index.html?next=instructor.html'
        # 審查 S-4:頁面帶 # 片段時加在片段之前(加在後面會落在片段裡,網頁讀不到);片段裡的 shipWeb= 不算已經寫了
        'instructor.html?ws=ws://{ship}:{stateWs}#frag'      = 'instructor.html?ws=ws://{ship}:{stateWs}&shipWeb=http://192.168.77.11:8081#frag'
        'instructor.html#frag'                               = 'instructor.html?shipWeb=http://192.168.77.11:8081#frag'
        'instructor.html?a=1#x&shipWeb=1'                    = 'instructor.html?a=1&shipWeb=http://192.168.77.11:8081#x&shipWeb=1'
        'instructor.html?shipWeb=0#f'                        = 'instructor.html?shipWeb=0#f'
        'radar.html#instructor.html'                         = 'radar.html#instructor.html'
        # 名稱只是以 shipWeb 結尾的其他參數不算已經寫了
        'instructor.html?xshipWeb=1'                         = 'instructor.html?xshipWeb=1&shipWeb=http://192.168.77.11:8081'
    }
    foreach ($page in $cases.Keys) {
        $got = Add-LaunchShipWebParam $page $u
        Assert-True ($got -ceq $cases[$page]) "Add-LaunchShipWebParam「${page}」應為「$($cases[$page])」,實際「${got}」"
        Assert-True ((Add-LaunchShipWebParam $page '') -ceq $page) "位址是空的時「${page}」應原樣回傳"
    }
    # 設定檔範例:ports.shipWeb 預設 null(不必設;註解說明用途)
    $instr = Read-LaunchConfig (Join-Path $root 'config\instructor-laptop.json')
    Assert-True ($null -eq (Get-Cfg $instr 'ports.shipWeb')) 'config\instructor-laptop.json 的 ports.shipWeb 預設應為 null'
    Assert-True ((Get-LaunchShipWebUrl $false '192.168.77.11' (Get-Cfg $instr 'ports.shipWeb')) -eq '') '預設設定不加 shipWeb 參數'
    $pages = @(@(Get-Cfg $instr 'web.openPages' @()) | ForEach-Object { Add-LaunchShipWebParam ([string]$_) $u })
    Assert-True (@($pages | Where-Object { $_ -cmatch 'shipWeb=' }).Count -eq 1 -and $pages[0] -ceq 'instructor.html?ws=ws://{ship}:{stateWs}&shipWeb=http://192.168.77.11:8081') "範例設定的 openPages 只有教官站頁面加 shipWeb:$($pages -join '、')"
    # 啟動腳本的接法:讀 ports.shipWeb、直連模式才處理網址並提示船上版本的來源
    $ps1 = (Get-Content -Raw -Encoding UTF8 (Join-Path $launch 'start-instructor-laptop.ps1')) -replace "`r`n", "`n"
    Assert-True ($ps1.Contains('$shipWebCfg = Get-LaunchShipWebPort $cfg') -and $ps1.Contains('$shipWebPort = $shipWebCfg.Port')) 'start-instructor-laptop.ps1 應以 Get-LaunchShipWebPort 讀 ports.shipWeb'
    $iUrl = $ps1.IndexOf('$shipWebUrl = Get-LaunchShipWebUrl ([bool]$standalone) $shipIp $shipWebPort')
    $iAdd = $ps1.IndexOf('Add-LaunchShipWebParam ([string]$_) $shipWebUrl')
    $iOpen = $ps1.IndexOf('Open-LaunchPages "http://127.0.0.1:$webPort" $pages')
    Assert-True ($iUrl -gt 0 -and $iAdd -gt $iUrl -and $iOpen -gt $iAdd) 'start-instructor-laptop.ps1 應先算船上網頁位址、加到教官站頁面,再開啟網頁'
    Assert-True ($ps1.Contains('/_status/release.json 取船上筆電的發行版本與降版提醒')) 'start-instructor-laptop.ps1 應提示船上版本的來源'
    # 審查 S-1:警告、加參數、提示都在 if (-not $standalone) { … } 區塊內(standalone 沒有另一台電腦:不加參數、不提示)
    $m = [regex]::Match($ps1, '(?s)\$shipWebUrl = Get-LaunchShipWebUrl[^\n]*\n {12}if \(-not \$standalone\) \{\n(.*?)\n {12}\}\n {12}if \(\$NoBrowser\)')
    Assert-True $m.Success 'start-instructor-laptop.ps1:算出船上網頁位址之後應緊接 if (-not $standalone) { … } 區塊,再開啟網頁'
    foreach ($inside in @('Write-Warning "設定檔的 ports.shipWeb(', 'Add-LaunchShipWebParam ([string]$_) $shipWebUrl', 'Write-Host "  船上筆電的版本:')) {
        Assert-True ($m.Groups[1].Value.Contains($inside)) "start-instructor-laptop.ps1:「${inside}」應在 if (-not `$standalone) 區塊內"
        Assert-True (([regex]::Matches($ps1, [regex]::Escape($inside))).Count -eq 1) "start-instructor-laptop.ps1:「${inside}」應只出現一次(不在區塊外)"
    }
}

# 審查 S-1:實際啟動 start-instructor-laptop.ps1(-NoBrowser -NoWait),不只比對原始碼。只在 Linux / macOS 的 pwsh 執行:Windows 上直連模式會動到
# 真的網路介面檢查(Get-NetAdapter、Set-DirectLinkIp),Windows 的實際啟動由 tools\ci\windows-e2e.ps1 的 instructor 階段(-Standalone)涵蓋。
# 直連模式(船上 IP 用 127.0.0.2,沒有船上筆電也照常啟動)以空的 Get-NetAdapter 取代 Windows 專用的 cmdlet。
Invoke-Test '船上筆電的網頁位址:實際啟動 start-instructor-laptop.ps1(直連:加 shipWeb 與提示;不是埠:警告;-Standalone:不加、不提示)' {
    if ($IsWindows) { Write-Host '    (Windows:略過實際啟動;見 tools\ci\windows-e2e.ps1)'; return }
    . (Join-Path $launch 'launch-common.ps1')
    function Get-NetAdapter { }
    $logs = 'build/test-tmp/launch-ps/logs8'
    $web = $PortBase + 14
    $envNames = @('SIMOSA_BRM_ROOT', 'SIMOSA_INSTALL_ROOT', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH', 'TEMP', 'TMP')
    # 以範例設定檔為底(改埠與 log 位置);回傳啟動腳本的全部輸出(含警告),結束時 stop-all 並還原目前目錄與環境變數
    function Invoke-Instructor([string]$Name, $ShipWeb, [hashtable]$Params) {
        $c = Get-Content -Raw -Encoding UTF8 (Join-Path $root 'config\instructor-laptop.json') | ConvertFrom-Json
        $c.ports.stateWs = $PortBase + 12; $c.ports.radarWs = $PortBase + 13; $c.ports.web = $web; $c.ports.sync = $PortBase + 15
        $c.ports.shipWeb = $ShipWeb
        $c.network.adapterName = ''
        $c.logs = $logs
        $file = Join-Path $tmp "instructor-$Name.json"
        Set-Content -Path $file -Value ($c | ConvertTo-Json -Depth 8) -Encoding UTF8
        $savedEnv = @{}; foreach ($n in $envNames) { $savedEnv[$n] = [Environment]::GetEnvironmentVariable($n) }
        $savedLocation = Get-Location
        try { return (& (Join-Path $launch 'start-instructor-laptop.ps1') -Config $file -NoBrowser -NoWait @Params 3>&1 6>&1 | Out-String) }
        finally {
            & (Join-Path $launch 'stop-all.ps1') -Logs $logs -KeepBrowser *>&1 | Out-Null
            Set-Location $savedLocation; foreach ($n in $envNames) { [Environment]::SetEnvironmentVariable($n, $savedEnv[$n]) }
        }
    }
    $hint = '船上筆電的版本:教官站連上船上的核心後,頁首向 '
    # 直連模式 + ports.shipWeb:只有教官站頁面加 shipWeb(-NoBrowser 印出的是代換 {ship} 之前的頁面),提示寫設定的埠
    $out = Invoke-Instructor 'shipweb-direct' 18081 @{ Ship = '127.0.0.2' }
    Assert-True ($out.Contains("http://127.0.0.1:$web/instructor.html?ws=ws://{ship}:{stateWs}&shipWeb=http://127.0.0.2:18081")) "直連模式設了 ports.shipWeb:教官站網址應加 shipWeb=http://<船上 IP>:<埠>:$out"
    Assert-True (([regex]::Matches($out, 'shipWeb=')).Count -eq 1) "只有教官站頁面加 shipWeb(雷達、ECDIS、講評頁不加):$out"
    Assert-True ($out.Contains("${hint}http://127.0.0.2:18081/_status/release.json 取")) "直連模式應提示船上版本的來源(設定的埠):$out"
    Assert-True (-not $out.Contains('不是 1–65535 的埠')) "合格的埠不應警告:$out"
    # 直連模式 + 不是埠的值(陣列):警告、不加參數、提示寫預設的 8080
    $out = Invoke-Instructor 'shipweb-array' @(8081) @{ Ship = '127.0.0.2' }
    Assert-True ($out.Contains('設定檔的 ports.shipWeb(陣列或物件)不是 1–65535 的埠,略過')) "ports.shipWeb = [8081] 應警告:$out"
    Assert-True (-not $out.Contains('shipWeb=')) "ports.shipWeb = [8081] 不應加參數:$out"
    Assert-True ($out.Contains("${hint}http://127.0.0.2:8080/_status/release.json 取")) "ports.shipWeb 不是埠時提示應寫預設的 8080:$out"
    # -Standalone(核心在本機,沒有另一台電腦):即使設了 ports.shipWeb 也不加參數、不提示
    $out = Invoke-Instructor 'shipweb-standalone' 18081 @{ Standalone = $true }
    Assert-True ($out.Contains("http://127.0.0.1:$web/instructor.html?ws=ws://{ship}:{stateWs}")) "-Standalone 應照常印出教官站頁面:$out"
    Assert-True (-not $out.Contains('shipWeb=') -and -not $out.Contains('船上筆電的版本')) "-Standalone 不應加 shipWeb 參數、不應提示船上版本的來源:$out"
    Assert-True (-not $out.Contains('SimCore.Host 未開啟')) "-Standalone 的本機核心應已啟動:$out"
}

Write-Host "通過 $pass,失敗 $fail$(if ($failed) { '(' + ($failed -join '、') + ')' })"
if ($fail -gt 0) { exit 1 }
