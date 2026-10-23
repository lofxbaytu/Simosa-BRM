<#
.SYNOPSIS
  launch-common.ps1 / stop-all.ps1 的測試(PowerShell 7;Windows 或 Linux 皆可執行,不需 Pester)。
.DESCRIPTION
  驗證:Start-LaunchProcess 不覆寫既有 log(輪替保留歷史、啟動標頭寫入 launch.log)、Test-LaunchPortFree 偵測被占用的埠、
  Wait-LaunchTcp -Process 在程序結束時立即回傳、stop-all.ps1 -KeepSync 保留同步服務、stop-all.ps1 先寫停止檔(Get-LaunchStopFile)再逾時強制結束。所有輸出留在 build\test-tmp\launch-ps\。
  用法:pwsh -NoProfile -File tools\launch\tests\launch-common.tests.ps1(tools\launch\tests\launch-tests.sh 為 .sh 版測試)
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

Write-Host "通過 $pass,失敗 $fail$(if ($failed) { '(' + ($failed -join '、') + ')' })"
if ($fail -gt 0) { exit 1 }
