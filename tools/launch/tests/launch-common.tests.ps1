<#
.SYNOPSIS
  launch-common.ps1 / stop-all.ps1 的測試(PowerShell 7;Windows 或 Linux 皆可執行,不需 Pester)。
.DESCRIPTION
  驗證:Start-LaunchProcess 不覆寫既有 log(輪替保留歷史、啟動標頭寫入 launch.log)、Test-LaunchPortFree 偵測被占用的埠、
  Wait-LaunchTcp -Process 在程序結束時立即回傳、stop-all.ps1 -KeepSync 保留同步服務、stop-all.ps1 先寫停止檔(Get-LaunchStopFile)再逾時強制結束、
  WebView2 外殼:Find-LaunchShellExe 搜尋順序、Start-LaunchShellPages 參數組合(--url/--kiosk/--no-close-confirm/--monitor)與 simosa.pids 登錄、
  找不到外殼時退回 Open-LaunchPages(以假 exe 於 build\test-tmp\launch-ps\app\shell\SimosaBRM.Shell.exe)。所有輸出留在 build\test-tmp\launch-ps\。
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
        & (Join-Path $launch 'stop-all.ps1') -Logs $logs -KeepSync | Out-Null
        Start-Sleep -Milliseconds 500
        Assert-True $web.HasExited 'web 程序應已結束'
        Assert-True (-not $sync.HasExited) 'sync 程序應被保留'
        $lines = @(Get-Content (Join-Path $dir 'simosa.pids') | Where-Object { $_ })
        Assert-True ($lines -contains "sync:$($sync.Id)") "PID 檔應保留 sync 列:$($lines -join '、')"
        Assert-True (-not ($lines -like 'web:*')) 'PID 檔不應再有 web 列'
        & (Join-Path $launch 'stop-all.ps1') -Logs $logs | Out-Null
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
        $out = (& (Join-Path $launch 'stop-all.ps1') -Logs $logs -StopTimeoutSeconds 1 *>&1 | Out-String)
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
        Set-Content -Path $Path -Value $body -Encoding ascii
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
    & (Join-Path $launch 'stop-all.ps1') -Logs $logs | Out-Null
}

Invoke-Test 'Start-LaunchShellPages 找不到外殼時退回 Open-LaunchPages 且不登錄 PID' {
    . (Join-Path $launch 'launch-common.ps1')
    # 以假的 Open-LaunchPages 取代(真的會 Start-Process 網址開瀏覽器);在 dot-source 之後定義才會蓋過原函式
    $script:openCalls = @()
    ${function:Open-LaunchPages} = { param([string]$BaseUrl, $Pages, [hashtable]$Subst) $script:openCalls += , @($BaseUrl, @($Pages), $Subst) }
    $logs = 'build\test-tmp\launch-ps\logs5'
    $logDir = Join-Path $root $logs
    Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
    Initialize-LaunchLogs $root $logs
    $pages = @('index.html?station=bridge', 'radar.html?radar=x')
    $missing = Join-Path $tmp 'nope\SimosaBRM.Shell.exe'
    $warn1 = (Start-LaunchShellPages $missing 'http://127.0.0.1:18080' $pages @{ ship = '127.0.0.1' } $root $logDir @(0, 1) 3>&1 | Out-String)
    $warn2 = (Start-LaunchShellPages $null 'http://127.0.0.1:18080' $pages @{ ship = '127.0.0.1' } $root $logDir 3>&1 | Out-String)
    Assert-True ($warn1.Contains('改用預設瀏覽器') -and $warn2.Contains('改用預設瀏覽器')) "exe 不存在或為 `$null 時都應警告改用預設瀏覽器:$warn1 / $warn2"
    Assert-True ($script:openCalls.Count -eq 2) "應呼叫 Open-LaunchPages 2 次,實際 $($script:openCalls.Count)"
    Assert-True ($script:openCalls[0][0] -eq 'http://127.0.0.1:18080' -and (@($script:openCalls[0][1]) -join ',') -eq ($pages -join ',') -and $script:openCalls[0][2]['ship'] -eq '127.0.0.1') '應以相同 BaseUrl、全部頁面與代換表呼叫 Open-LaunchPages'
    $pidLines = @(Get-Content (Join-Path $logDir 'simosa.pids') | Where-Object { $_ })
    Assert-True ($pidLines.Count -eq 0) "退回瀏覽器時不應登錄任何 PID:$($pidLines -join '、')"
    Assert-True (-not (Get-ChildItem $logDir -Filter 'shell-*.log')) '退回瀏覽器時不應產生 shell-*.log'
    Assert-True (-not (Test-Path (Join-Path $logDir 'launch.log'))) '退回瀏覽器時不應寫啟動標頭'
}

Write-Host "通過 $pass,失敗 $fail$(if ($failed) { '(' + ($failed -join '、') + ')' })"
if ($fail -gt 0) { exit 1 }
