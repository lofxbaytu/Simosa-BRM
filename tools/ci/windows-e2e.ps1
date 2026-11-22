<#
.SYNOPSIS
  Windows 實機端到端檢查(GitHub Actions 的 Windows 執行器;也可在 Windows 開發機以系統管理員執行)。
.DESCRIPTION
  容器(Linux)無法驗證的 Windows 行為,在這裡以內建的 Windows PowerShell 5.1 實際執行:
    build     :tools\build-release.ps1 -Installer -SkipGodot(完整發行組裝 + Inno Setup 編譯安裝程式)
    install   :靜默安裝 — (a) 指定系統磁碟而未帶 /ALLOWSYSTEMDRIVE=1 時必須不安裝(也不可卡住);(b) 不指定位置時裝到預設位置(D: → G: → 系統磁碟);
               安裝後核對檔案、開始功能表捷徑、防火牆規則、start-ship-laptop.ps1 -CheckOnly、simosa-pkg release verify
    launch    :從安裝位置啟動 — (a) -Duration 短跑(核心 50 Hz、紀錄 footer);(b) -NoWait + 專屬 Edge 設定檔(各埠、網頁 200、Edge 設定檔與下載位置)
               → stop-all.ps1(核心經停止檔寫完 footer、Edge 視窗關閉)→ check-local-data.ps1(安裝版模式)
    shell     :-Shell(WebView2 外殼)啟動 → 使用者資料夾在 build\webview2 → stop-all.ps1 關閉
    uninstall :靜默解除安裝 → app\ 移除、紀錄 / 設定 / 瀏覽器端資料保留、捷徑與防火牆規則移除
  每個檢查獨立記錄 PASS / FAIL / WARN / INFO,不在第一個失敗就中止(一次 CI 執行取得最多資訊);最後有 FAIL 即結束碼 1。
  WARN:尚未確定 Windows 實際行為的項目(先觀察,不讓工作失敗)。所有暫存與輸出在 <專案>\build\tmp\windows-e2e\。
.PARAMETER Stage
  要執行的階段,逗號分隔(build,install,launch,shell,uninstall);all = 全部依序。
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\windows-e2e.ps1 -Stage build,install,launch,shell,uninstall
#>
[CmdletBinding()]
param(
    [string]$Stage = 'all',
    [int]$LaunchSeconds = 20
)
# -File 呼叫時「build,install」是單一字串(不會被解析成陣列),所以自行以逗號 / 空白切開
$Stage = @("$Stage" -split '[,;\s]+' | Where-Object { $_ })
$validStages = @('build', 'install', 'launch', 'shell', 'uninstall', 'all')
foreach ($s in $Stage) { if ($validStages -notcontains $s) { throw "未知的階段 $s(可用:$($validStages -join '、'))" } }
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$work = Join-Path $root 'build\tmp\windows-e2e'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stateFile = Join-Path $work 'state.json'
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($Stage -contains 'all') { $Stage = @('build', 'install', 'launch', 'shell', 'uninstall') }

$script:results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Level, [string]$Name, [string]$Detail = '') {
    $script:results.Add([pscustomobject]@{ level = $Level; name = $Name; detail = $Detail })
    $color = 'Gray'
    if ($Level -eq 'PASS') { $color = 'Green' } elseif ($Level -eq 'FAIL') { $color = 'Red' } elseif ($Level -eq 'WARN') { $color = 'Yellow' }
    $line = "[$Level] $Name"
    if ($Detail) { $line = "$line — $Detail" }
    Write-Host $line -ForegroundColor $color
}
# Check <名稱> { … }:區塊丟例外 = FAIL(訊息為例外內容);回傳字串 = PASS 的補充說明
function Check([string]$Name, [scriptblock]$Body, [switch]$Soft) {
    try {
        $r = & $Body
        $detail = ''
        if ($r) { $detail = (@($r) | ForEach-Object { "$_" }) -join ' ' }
        Add-Result 'PASS' $Name $detail
        return $true
    } catch {
        $level = 'FAIL'
        if ($Soft) { $level = 'WARN' }
        Add-Result $level $Name "$($_.Exception.Message)"
        return $false
    }
}
function Info([string]$Name, [string]$Detail) { Add-Result 'INFO' $Name $Detail }

# 以子程序執行(輸出導到檔案,不經 PowerShell 的錯誤資料流);逾時即結束整個程序樹。回傳 ExitCode / Output / TimedOut
$script:runSeq = 0
function Invoke-Proc([string]$File, [string[]]$Arguments, [int]$TimeoutSec = 600, [string]$WorkDir = $root, [string]$RawArguments = '') {
    $script:runSeq++
    $out = Join-Path $work ("run-{0:d3}.out.txt" -f $script:runSeq)
    $err = Join-Path $work ("run-{0:d3}.err.txt" -f $script:runSeq)
    $quoted = ($Arguments | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join ' '
    if ($RawArguments) { $quoted = $RawArguments }
    $sp = @{ FilePath = $File; WorkingDirectory = $WorkDir; PassThru = $true; RedirectStandardOutput = $out; RedirectStandardError = $err; WindowStyle = 'Hidden' }
    if ($quoted) { $sp.ArgumentList = $quoted }
    $p = Start-Process @sp
    $null = $p.Handle   # Windows PowerShell 5.1:先取 Handle,結束後才讀得到 ExitCode
    $timedOut = $false
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $timedOut = $true
        & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null
        Start-Sleep -Seconds 1
    }
    $text = ''
    foreach ($f in @($out, $err)) { if (Test-Path $f) { $text += (Get-Content -Raw -Encoding UTF8 $f) } }
    $code = -1
    if (-not $timedOut) { $code = $p.ExitCode }
    return [pscustomobject]@{ ExitCode = $code; Output = "$text"; TimedOut = $timedOut }
}
# 以內建的 Windows PowerShell 5.1 執行腳本,方式與安裝程式的捷徑相同(powershell.exe -ExecutionPolicy Bypass -File …)。
# 先經 cmd 把主控台字碼頁設成 65001(UTF-8):5.1 導向輸出時用主控台的輸出字碼頁,執行器預設 437,中文會變成「?」而無法比對。
function Invoke-Ps51([string]$Script, [string[]]$Arguments = @(), [int]$TimeoutSec = 600, [string]$WorkDir = $root) {
    $inner = 'chcp 65001 >nul & "' + $ps51 + '" -NoProfile -ExecutionPolicy Bypass -File "' + $Script + '"'
    foreach ($a in $Arguments) { if ("$a" -match '[\s"&|<>^]') { $inner += ' "' + $a + '"' } else { $inner += " $a" } }
    return Invoke-Proc $env:ComSpec @() $TimeoutSec $WorkDir ('/d /s /c "' + $inner + '"')
}
# 把安裝位置的 log 複製到 artifact 目錄(各階段結束前呼叫)
function Save-InstalledLogs([string]$InstallDir, [string]$Tag) {
    $src = Join-Path $InstallDir 'build\logs'
    if (-not (Test-Path $src)) { return }
    $dst = Join-Path $work "logs-$Tag"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $dst -Recurse -Force -ErrorAction SilentlyContinue
}
function Show-Tail([string]$Title, [string]$Text, [int]$Lines = 40) {
    Write-Host "---- $Title(最後 $Lines 行)----"
    @("$Text" -split "`r?`n") | Select-Object -Last $Lines | ForEach-Object { Write-Host "    $_" }
}
function Show-File([string]$Path, [int]$Lines = 40) {
    if (Test-Path $Path) { Show-Tail $Path (Get-Content -Raw -Encoding UTF8 $Path) $Lines } else { Write-Host "---- ${Path}:不存在" }
}
function Test-Tcp([string]$HostName, [int]$Port, [int]$TimeoutMs = 1000) {
    $c = New-Object System.Net.Sockets.TcpClient
    try { $t = $c.ConnectAsync($HostName, $Port); return ($t.Wait($TimeoutMs) -and $c.Connected) } catch { return $false } finally { $c.Close() }
}
function Wait-Until([scriptblock]$Cond, [int]$Seconds = 30) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Cond) { return $true }; Start-Sleep -Milliseconds 500 }
    return $false
}
function Read-State {
    if (Test-Path $stateFile) { return (Get-Content -Raw -Encoding UTF8 $stateFile | ConvertFrom-Json) }
    return $null
}
function Save-State($State) {
    [System.IO.File]::WriteAllText($stateFile, ($State | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
}
function Get-InstallDir {
    $s = Read-State
    if ($s -and $s.installDir -and (Test-Path $s.installDir)) { return $s.installDir }
    return $null
}
# 以命令列找程序(Win32_Process;Get-Process 沒有命令列)
function Get-ProcByCommandLine([string]$Name, [string]$Contains) {
    try {
        return @(Get-CimInstance Win32_Process -Filter "Name='$Name'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($Contains, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 })
    } catch { return @() }
}
function Read-Pids([string]$InstallDir) {
    $f = Join-Path $InstallDir 'build\logs\simosa.pids'
    $map = @{}
    if (Test-Path $f) {
        foreach ($line in Get-Content $f) {
            if ($line -and $line.Contains(':')) { $n, $id = $line.Split(':', 2); $map[$n] = [int]$id }
        }
    }
    return $map
}
function Get-NewestRecord([string]$InstallDir, [datetime]$After) {
    $dir = Join-Path $InstallDir 'build\records'
    if (-not (Test-Path $dir)) { return $null }
    return (Get-ChildItem -LiteralPath $dir -Filter '*.jsonl' -File | Where-Object { $_.LastWriteTime -ge $After } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}
function Assert-RecordFooter($File) {
    if (-not $File) { throw '沒有新的紀錄檔(build\records\*.jsonl)' }
    $last = Get-Content -LiteralPath $File.FullName -Encoding UTF8 | Where-Object { $_ } | Select-Object -Last 1
    if ($last -notmatch '"kind"\s*:\s*"footer"') { throw "紀錄 $($File.Name) 最後一行不是 footer:$("$last".Substring(0, [Math]::Min(120, "$last".Length)))" }
    return "$($File.Name)($([math]::Round($File.Length / 1KB)) KB)"
}

Write-Host "Simosa BRM Windows 端到端檢查 — 版本 $version;專案 $root;系統磁碟 $($env:SystemDrive);PowerShell $($PSVersionTable.PSVersion);階段 $($Stage -join ',')"

# ======================================================================================================================
if ($Stage -contains 'build') {
    Write-Host ''; Write-Host '==== build:tools\build-release.ps1 -Installer -SkipGodot(Windows PowerShell 5.1)===='
    $r = Invoke-Ps51 (Join-Path $root 'tools\build-release.ps1') @('-Installer', '-SkipGodot') 2400
    Show-Tail 'build-release.ps1' $r.Output 60
    $relDir = Join-Path $root "build\release\$version"
    $setup = Join-Path $root "build\release\installer\SimosaBRM-Setup-$version.exe"
    [void](Check 'build-release.ps1 結束碼 0' { if ($r.TimedOut) { throw '逾時' }; if ($r.ExitCode -ne 0) { throw "結束碼 $($r.ExitCode)" } })
    [void](Check '發行版目錄與清單' {
        foreach ($rel in @('manifest.json', 'README.txt', 'app\simcore\SimosaBRM.SimCore.Host.dll', 'app\radar\SimosaBRM.Sensors.Radar.Host.dll', 'app\web-server\simosa-web.dll',
                           'app\web\index.html', 'app\shell\SimosaBRM.Shell.exe', 'app\launch\start-ship-laptop.ps1', 'app\launch\check-local-data.ps1',
                           'tools\updater\simosa-pkg.dll', 'config\ship-laptop.json', 'data\scenarios\E01_baseline.yaml')) {
            if (-not (Test-Path (Join-Path $relDir $rel))) { throw "發行版缺 $rel" }
        }
        if (Test-Path (Join-Path $relDir 'config\packaging.json')) { throw '發行版不應含 config\packaging.json(開發機的本機資料)' }
        return $relDir
    })
    [void](Check '安裝程式已編譯' { if (-not (Test-Path $setup)) { throw "沒有 $setup" }; return "$([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB" })
    [void](Check '發行版的 .ps1 都帶 UTF-8 BOM' {
        $bad = @()
        foreach ($f in Get-ChildItem -LiteralPath (Join-Path $relDir 'app\launch') -Filter '*.ps1') {
            $b = [System.IO.File]::ReadAllBytes($f.FullName)
            if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) { $bad += $f.Name }
        }
        if ($bad) { throw "沒有 BOM:$($bad -join '、')" }
    })
}

# ======================================================================================================================
if ($Stage -contains 'install') {
    Write-Host ''; Write-Host '==== install:靜默安裝 ===='
    $setup = Join-Path $root "build\release\installer\SimosaBRM-Setup-$version.exe"
    if (-not (Test-Path $setup)) { Add-Result 'FAIL' '安裝程式存在' "沒有 $setup(先執行 build 階段)" }
    else {
        $sys = $env:SystemDrive
        # (a) 系統磁碟、未帶 /ALLOWSYSTEMDRIVE=1、訊息被抑制 → 必須不安裝,而且不可卡住
        $sysDir = "$sys\SimosaBRM-ci-denied"
        if (Test-Path $sysDir) { Remove-Item -Recurse -Force $sysDir -ErrorAction SilentlyContinue }
        $logA = Join-Path $work 'install-denied.log'
        $ra = Invoke-Proc $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$sysDir", "/LOG=$logA") 180
        [void](Check '系統磁碟未經同意:靜默安裝不會卡住' { if ($ra.TimedOut) { throw '180 秒內沒有結束(靜默安裝卡在選擇目錄頁?)' }; return "結束碼 $($ra.ExitCode)" })
        [void](Check '系統磁碟未經同意:沒有安裝' { if (Test-Path (Join-Path $sysDir 'manifest.json')) { throw "$sysDir 已被安裝" }; return "$sysDir 不存在或沒有內容" })
        if (Test-Path $logA) { Show-File $logA 15 }

        # (b) 不指定位置 → 預設位置(D: → G: → 系統磁碟;最後一種須帶 /ALLOWSYSTEMDRIVE=1)
        $expected = "$sys\Simosa BRM"; $extra = @('/ALLOWSYSTEMDRIVE=1')
        if ((Test-Path 'D:\') -and ($sys -ne 'D:')) { $expected = 'D:\Simosa BRM'; $extra = @() }
        elseif ((Test-Path 'G:\') -and ($sys -ne 'G:')) { $expected = 'G:\Simosa BRM'; $extra = @() }
        Info '預期的預設安裝位置' $expected
        if (Test-Path $expected) { Info '預設位置已存在' '先移除(上一次執行留下的?)'; Remove-Item -Recurse -Force $expected -ErrorAction SilentlyContinue }
        $logB = Join-Path $work 'install.log'
        $t0 = Get-Date
        $rb = Invoke-Proc $setup (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$logB") + $extra) 900
        [void](Check '預設位置的靜默安裝結束碼 0' { if ($rb.TimedOut) { throw '逾時' }; if ($rb.ExitCode -ne 0) { throw "結束碼 $($rb.ExitCode)" } })
        $installed = Check '安裝到預設位置' { if (-not (Test-Path (Join-Path $expected 'manifest.json'))) { throw "$expected\manifest.json 不存在" }; return $expected }
        if (-not $installed -and (Test-Path $logB)) { Show-File $logB 40 }
        Save-State ([pscustomobject]@{ installDir = $expected; installedAt = $t0.ToString('o') })
        if ($installed) {
            [void](Check '安裝內容' {
                foreach ($rel in @('app\launch\start-ship-laptop.ps1', 'app\launch\stop-all.ps1', 'app\launch\check-local-data.ps1', 'app\simcore\SimosaBRM.SimCore.Host.dll',
                                   'app\shell\SimosaBRM.Shell.exe', 'tools\updater\simosa-pkg.dll', 'tools\uninstall\unins000.exe', 'config\ship-laptop.json',
                                   'build\logs', 'build\records', 'data\records', 'state', 'store')) {
                    if (-not (Test-Path (Join-Path $expected $rel))) { throw "缺 $rel" }
                }
            })
            [void](Check '開始功能表捷徑' {
                $dir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Simosa BRM'
                $lnk = @(Get-ChildItem -LiteralPath $dir -Filter '*.lnk' -ErrorAction Stop)
                if ($lnk.Count -lt 4) { throw "$dir 只有 $($lnk.Count) 個捷徑" }
                return "$($lnk.Count) 個:$(($lnk | ForEach-Object { $_.BaseName }) -join '、')"
            })
            [void](Check '防火牆規則已登錄' {
                $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -like 'Simosa BRM*' })
                if ($rules.Count -eq 0) { throw '沒有名稱以 Simosa BRM 開頭的防火牆規則' }
                return "$($rules.Count) 條"
            })
            [void](Check '一般使用者對安裝根目錄有修改權限' {
                $acl = (& icacls.exe $expected 2>&1 | Out-String)
                if ($acl -notmatch '(BUILTIN\\Users|Users):.*\(M\)') { throw "icacls 未見 Users 的 (M):$acl" }
            })
            # 捷徑的執行方式:powershell.exe -ExecutionPolicy Bypass -File …
            $rc = Invoke-Ps51 (Join-Path $expected 'app\launch\start-ship-laptop.ps1') @('-CheckOnly') 300 $expected
            Show-Tail 'start-ship-laptop.ps1 -CheckOnly' $rc.Output 12
            [void](Check 'start-ship-laptop.ps1 -CheckOnly(安裝位置,5.1)' {
                if ($rc.ExitCode -ne 0) { throw "結束碼 $($rc.ExitCode)" }
                foreach ($need in @('app\simcore\SimosaBRM.SimCore.Host.dll', 'app\radar\', 'app\web-server\simosa-web.dll', 'tools\updater\simosa-pkg.dll')) {
                    if ($rc.Output.IndexOf($need, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { throw "輸出沒有 $need" }
                }
                if ($rc.Output -match '[\uFFFD]|\?\?\?\?') { throw '輸出含亂碼' }
            })
            $rv = Invoke-Proc 'dotnet' @((Join-Path $expected 'tools\updater\simosa-pkg.dll'), 'release', 'verify', '--dir', $expected) 300 $expected
            Show-Tail 'simosa-pkg release verify' $rv.Output 8
            [void](Check 'simosa-pkg release verify(安裝位置)' { if ($rv.ExitCode -ne 0) { throw "結束碼 $($rv.ExitCode)" } })
        }
    }
}

# ======================================================================================================================
if ($Stage -contains 'launch') {
    Write-Host ''; Write-Host '==== launch:從安裝位置啟動 ===='
    $inst = Get-InstallDir
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    else {
        $launch = Join-Path $inst 'app\launch'
        $logs = Join-Path $inst 'build\logs'
        $tStart = Get-Date

        # (a) -Duration 短跑:核心跑完指定的模擬秒數後自行結束,腳本一併結束其他程序
        $ra = Invoke-Ps51 (Join-Path $launch 'start-ship-laptop.ps1') @('-Scenario', 'data\scenarios\E01_baseline.yaml', '-Duration', "$LaunchSeconds", '-NoVisual', '-NoBrowser', '-NoSync') 240 $inst
        Show-Tail "start-ship-laptop.ps1 -Duration $LaunchSeconds" $ra.Output 30
        [void](Check "-Duration $LaunchSeconds 短跑結束碼 0" { if ($ra.TimedOut) { throw '逾時' }; if ($ra.ExitCode -ne 0) { throw "結束碼 $($ra.ExitCode)" } })
        [void](Check '短跑:核心 log' {
            $log = Join-Path $logs 'simcore.log'
            if (-not (Test-Path $log)) { throw "沒有 $log" }
            $text = Get-Content -Raw -Encoding UTF8 $log
            if ($text -notmatch 'WebSocket') { throw 'simcore.log 沒有 WebSocket 監聽訊息' }
            if ($text -match '無法監聽') { return "注意:$(($text -split "`r?`n" | Where-Object { $_ -match '無法監聽' } | Select-Object -First 1))" }
        })
        [void](Check '短跑:紀錄有 footer' { Assert-RecordFooter (Get-NewestRecord $inst $tStart) })
        [void](Check '短跑後沒有殘留程序' {
            $left = @()
            $pids = Read-Pids $inst
            foreach ($n in $pids.Keys) { if (Get-Process -Id $pids[$n] -ErrorAction SilentlyContinue) { $left += "${n}:$($pids[$n])" } }
            if ($left) { throw "仍在執行:$($left -join '、')" }
        })
        foreach ($f in @('simcore.err.log', 'radar.err.log', 'web.err.log')) {
            $p = Join-Path $logs $f
            if ((Test-Path $p) -and (Get-Item $p).Length -gt 0) { Show-File $p 15 }
        }

        # (b) -NoWait + 專屬 Edge 設定檔
        $tB = Get-Date
        $rb = Invoke-Ps51 (Join-Path $launch 'start-ship-laptop.ps1') @('-Scenario', 'data\scenarios\E01_baseline.yaml', '-NoWait', '-NoVisual', '-NoSync') 240 $inst
        Show-Tail 'start-ship-laptop.ps1 -NoWait' $rb.Output 30
        [void](Check '-NoWait 啟動結束碼 0' { if ($rb.TimedOut) { throw '逾時' }; if ($rb.ExitCode -ne 0) { throw "結束碼 $($rb.ExitCode)" } })
        [void](Check '狀態 WebSocket 埠 8765 開啟' { if (-not (Wait-Until { Test-Tcp '127.0.0.1' 8765 } 30)) { throw '30 秒內未開啟' } })
        [void](Check '雷達埠 8766 開啟' { if (-not (Wait-Until { Test-Tcp '127.0.0.1' 8766 } 30)) { throw '30 秒內未開啟' } })
        [void](Check 'VHF 8767 與錄影上傳 8768 開啟' { if (-not (Wait-Until { (Test-Tcp '127.0.0.1' 8767) -and (Test-Tcp '127.0.0.1' 8768) } 20)) { throw '20 秒內未開啟' } })
        foreach ($page in @('index.html', 'instructor.html', 'radar.html', 'ecdis.html', 'report.html')) {
            [void](Check "網頁 http://127.0.0.1:8080/$page 回 200" {
                $resp = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8080/$page" -TimeoutSec 10
                if ($resp.StatusCode -ne 200) { throw "狀態碼 $($resp.StatusCode)" }
                return "$($resp.RawContentLength) bytes;Cache-Control $($resp.Headers['Cache-Control'])"
            })
        }
        [void](Check 'PID 檔內的程序都在執行' {
            $pids = Read-Pids $inst
            if ($pids.Count -lt 3) { throw "simosa.pids 只有 $($pids.Count) 項" }
            $dead = @()
            foreach ($n in $pids.Keys) { if (-not (Get-Process -Id $pids[$n] -ErrorAction SilentlyContinue)) { $dead += $n } }
            if ($dead) { throw "已結束:$($dead -join '、')" }
            return (($pids.Keys | Sort-Object) -join '、')
        })
        # 專屬 Edge 設定檔(瀏覽器端資料與下載位置留在安裝根目錄)
        $profileDir = Join-Path $inst 'build\browser\edge'
        $edgeOk = Check 'Edge 以專屬設定檔啟動' {
            if ($rb.Output -match '找不到 Microsoft Edge') { throw '啟動腳本回報找不到 Microsoft Edge' }
            if (-not (Wait-Until { @(Get-ProcByCommandLine 'msedge.exe' $profileDir).Count -gt 0 } 40)) { throw "40 秒內沒有命令列含 $profileDir 的 msedge.exe" }
            return "$(@(Get-ProcByCommandLine 'msedge.exe' $profileDir).Count) 個 msedge 程序"
        } -Soft
        if ($edgeOk) {
            Start-Sleep -Seconds 8   # 讓 Edge 寫出設定檔
            [void](Check 'Edge 設定檔在安裝根目錄內' {
                foreach ($rel in @('Default', 'Local State')) { if (-not (Test-Path (Join-Path $profileDir $rel))) { throw "缺 $rel" } }
                return $profileDir
            } -Soft)
            [void](Check 'Edge 的下載位置是 build\downloads(預先寫入的設定被採用)' {
                $prefs = Join-Path $profileDir 'Default\Preferences'
                $text = [System.IO.File]::ReadAllText($prefs)
                $want = (Join-Path $inst 'build\downloads').Replace('\', '\\')
                if ($text.IndexOf('"default_directory":"' + $want + '"', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                    $m = [regex]::Match($text, '"default_directory"\s*:\s*"((?:[^"\\]|\\.)*)"')
                    throw "Preferences 內的 default_directory = $($m.Groups[1].Value)(預期 $want);檔案大小 $($text.Length)"
                }
                return "Preferences $($text.Length) 字元"
            } -Soft)
            Info 'Edge 主視窗' ((Get-ProcByCommandLine 'msedge.exe' $profileDir | Where-Object { $_.CommandLine -notmatch '--type=' } | ForEach-Object {
                        $gp = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                        if ($gp) { "PID $($gp.Id) MainWindowHandle $($gp.MainWindowHandle) 標題「$($gp.MainWindowTitle)」" } }) -join ';')
        }
        Start-Sleep -Seconds 5
        [void](Check '核心 50 Hz 無掉拍(simcore.log 的 steps/s)' {
            $text = Get-Content -Raw -Encoding UTF8 (Join-Path $logs 'simcore.log')
            $m = [regex]::Matches($text, 'steps/s\s+(\d+(?:\.\d+)?)')
            if ($m.Count -eq 0) { throw 'simcore.log 沒有 steps/s 行' }
            $vals = @($m | ForEach-Object { [double]$_.Groups[1].Value })
            $last = $vals[$vals.Count - 1]
            if ($last -lt 45) { throw "最後一筆 steps/s = $last" }
            return "最後一筆 $last($($vals.Count) 筆)"
        } -Soft)

        # stop-all.ps1:核心經停止檔寫完 footer;專屬設定檔的 Edge 視窗關閉
        $swStop = [System.Diagnostics.Stopwatch]::StartNew()
        $rs = Invoke-Ps51 (Join-Path $launch 'stop-all.ps1') @() 120 $inst
        Show-Tail 'stop-all.ps1' $rs.Output 20
        Show-File (Join-Path $logs 'simcore.log') 12
        [void](Check 'stop-all.ps1 結束碼 0' { if ($rs.TimedOut) { throw '逾時' }; if ($rs.ExitCode -ne 0) { throw "結束碼 $($rs.ExitCode)" }; return "$([math]::Round($swStop.Elapsed.TotalSeconds, 1)) s" })
        # 瀏覽器分頁還連著的情況下,核心必須自行結束(停止檔 → 寫完 footer),不可等到逾時被強制結束
        [void](Check 'stop-all:沒有強制結束核心(停止檔機制;Edge 分頁仍連線中)' {
            if ($rs.Output -match '強制結束') { throw '輸出含「強制結束」:核心未在時限內自行結束' }
            if ($rs.Output -notmatch '寫入停止檔') { throw '輸出沒有「寫入停止檔」(輸出無法辨識?)' }
            $text = Get-Content -Raw -Encoding UTF8 (Join-Path $logs 'simcore.log')
            if ($text -notmatch '紀錄已關閉') { throw 'simcore.log 沒有「紀錄已關閉」' }
        })
        [void](Check 'stop-all:各埠已關閉' { if (-not (Wait-Until { -not ((Test-Tcp '127.0.0.1' 8765 300) -or (Test-Tcp '127.0.0.1' 8766 300) -or (Test-Tcp '127.0.0.1' 8080 300)) } 15)) { throw '15 秒後仍有埠開著' } })
        [void](Check 'stop-all:紀錄有 footer(核心自行結束)' { Assert-RecordFooter (Get-NewestRecord $inst $tB) })
        if ($edgeOk) {
            [void](Check 'stop-all:專屬設定檔的 Edge 已關閉' {
                if (-not (Wait-Until { @(Get-ProcByCommandLine 'msedge.exe' $profileDir).Count -eq 0 } 15)) {
                    $left = @(Get-ProcByCommandLine 'msedge.exe' $profileDir)
                    throw "15 秒後仍有 $($left.Count) 個 msedge(主程序 $(@($left | Where-Object { $_.CommandLine -notmatch '--type=' }).Count) 個)"
                }
            } -Soft)
            # 收尾:不論上面結果,確保沒有殘留的 Edge(否則後面的解除安裝與下一個階段會受影響)
            foreach ($e in @(Get-ProcByCommandLine 'msedge.exe' $profileDir | Where-Object { $_.CommandLine -notmatch '--type=' })) { & taskkill.exe /PID $e.ProcessId /T /F 2>&1 | Out-Null }
        }

        # check-local-data.ps1(安裝版模式):執行期沒有把資料寫到系統磁碟的使用者設定檔
        $rc = Invoke-Ps51 (Join-Path $launch 'check-local-data.ps1') @('-Since', $tStart.ToString('yyyy-MM-dd HH:mm:ss')) 120 $inst
        Show-Tail 'check-local-data.ps1(安裝版模式)' $rc.Output 25
        [void](Check 'check-local-data.ps1(安裝版模式)結束碼 0' {
            if ($rc.Output -notmatch '安裝版模式') { throw '不是以安裝版模式執行' }
            $sysInstall = $inst.StartsWith($env:SystemDrive, [System.StringComparison]::OrdinalIgnoreCase)
            if ($rc.ExitCode -ne 0 -and -not ($sysInstall -and $rc.ExitCode -eq 2)) { throw "結束碼 $($rc.ExitCode)" }
        })
        Save-InstalledLogs $inst 'launch'
        Info '安裝根目錄內的執行期資料' ((@('build\logs', 'build\records', 'build\tmp', 'build\browser', 'build\downloads', '.cache') | ForEach-Object {
                    $p = Join-Path $inst $_; if (Test-Path $p) { "$_($(@(Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue).Count) 檔)" } else { "$_(無)" } }) -join '、')
    }
}

# ======================================================================================================================
if ($Stage -contains 'shell') {
    Write-Host ''; Write-Host '==== shell:WebView2 外殼 ===='
    $inst = Get-InstallDir
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    else {
        $launch = Join-Path $inst 'app\launch'
        $logs = Join-Path $inst 'build\logs'
        $rb = Invoke-Ps51 (Join-Path $launch 'start-ship-laptop.ps1') @('-Scenario', 'data\scenarios\E01_baseline.yaml', '-NoWait', '-NoVisual', '-NoSync', '-Shell') 240 $inst
        Show-Tail 'start-ship-laptop.ps1 -Shell -NoWait' $rb.Output 30
        [void](Check '-Shell 啟動結束碼 0' { if ($rb.TimedOut) { throw '逾時' }; if ($rb.ExitCode -ne 0) { throw "結束碼 $($rb.ExitCode)" } })
        $shellOk = Check '外殼程序已登錄並在執行' {
            Start-Sleep -Seconds 10
            $pids = Read-Pids $inst
            $shells = @($pids.Keys | Where-Object { $_ -like 'shell-*' })
            if ($shells.Count -eq 0) { throw "simosa.pids 沒有 shell-* 項目:$(($pids.Keys | Sort-Object) -join '、')" }
            $dead = @($shells | Where-Object { -not (Get-Process -Id $pids[$_] -ErrorAction SilentlyContinue) })
            if ($dead) { throw "已結束:$($dead -join '、')" }
            return "$($shells.Count) 個:$(($shells | Sort-Object) -join '、')"
        } -Soft
        foreach ($f in @(Get-ChildItem -LiteralPath $logs -Filter 'shell-*.log' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '\.\d+\.log$' })) { Show-File $f.FullName 12 }
        if ($shellOk) {
            [void](Check 'WebView2 使用者資料夾在 build\webview2' {
                $dir = Join-Path $inst 'build\webview2'
                if (-not (Wait-Until { @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path (Join-Path $_.FullName 'EBWebView') }).Count -gt 0 } 30)) {
                    throw "30 秒內 $dir 下沒有含 EBWebView 的設定檔資料夾:$((Get-ChildItem -LiteralPath $dir -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '、')"
                }
                return ((Get-ChildItem -LiteralPath $dir -Directory | ForEach-Object { $_.Name }) -join '、')
            } -Soft)
            [void](Check '外殼 log:下載資料夾設在 build\downloads' {
                $text = (Get-ChildItem -LiteralPath $logs -Filter 'shell-*.log' | ForEach-Object { Get-Content -Raw -Encoding UTF8 $_.FullName }) -join "`n"
                if ($text -match '下載.*失敗|無法設定下載') { throw "log 回報設定下載資料夾失敗:$(($text -split "`r?`n" | Where-Object { $_ -match '下載' } | Select-Object -First 1))" }
                if ($text -notmatch 'downloads') { throw 'log 沒有提到 downloads' }
            } -Soft)
        }
        $rs = Invoke-Ps51 (Join-Path $launch 'stop-all.ps1') @() 120 $inst
        Show-Tail 'stop-all.ps1' $rs.Output 20
        [void](Check 'stop-all.ps1(外殼)結束碼 0' { if ($rs.ExitCode -ne 0) { throw "結束碼 $($rs.ExitCode)" } })
        [void](Check 'stop-all:外殼已關閉' {
            if (-not (Wait-Until { @(Get-Process -Name 'SimosaBRM.Shell' -ErrorAction SilentlyContinue).Count -eq 0 } 15)) { throw '15 秒後仍有 SimosaBRM.Shell 程序' }
            if ($rs.Output -match '強制結束') { return '注意:有程序被強制結束(見上方輸出)' }
        } -Soft)
        foreach ($p in @(Get-Process -Name 'SimosaBRM.Shell' -ErrorAction SilentlyContinue)) { & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null }
        [void](Check 'stop-all(外殼):核心自行結束並寫完紀錄' {
            if ($rs.Output -match '強制結束') { throw '輸出含「強制結束」' }
            $text = Get-Content -Raw -Encoding UTF8 (Join-Path $logs 'simcore.log')
            if ($text -notmatch '紀錄已關閉') { throw 'simcore.log 沒有「紀錄已關閉」' }
        })
        Save-InstalledLogs $inst 'shell'
    }
}

# ======================================================================================================================
if ($Stage -contains 'uninstall') {
    Write-Host ''; Write-Host '==== uninstall:靜默解除安裝 ===='
    $inst = Get-InstallDir
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    else {
        $unins = Join-Path $inst 'tools\uninstall\unins000.exe'
        # 在每個應保留的資料夾放一個哨兵檔(模擬已有紀錄 / 帳冊 / 金鑰),解除安裝後資料夾與檔案都必須還在
        $dataDirs = @('build\records', 'data\records', 'state', 'store', 'config\trusted-keys', 'config\device', 'build\webview2', 'build\browser', 'build\downloads')
        foreach ($rel in $dataDirs) {
            $d = Join-Path $inst $rel
            New-Item -ItemType Directory -Force -Path $d | Out-Null
            Set-Content -Path (Join-Path $d 'ci-sentinel.txt') -Value 'keep' -Encoding ASCII
        }
        $had = @{}
        foreach ($rel in ($dataDirs + @('config\ship-laptop.json'))) { $had[$rel] = (Test-Path (Join-Path $inst $rel)) }
        $ru = Invoke-Proc $unins @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') 300 $root
        Start-Sleep -Seconds 5   # 解除安裝程式會把自己複製到暫存再執行
        [void](Check '解除安裝結束碼 0' { if ($ru.TimedOut) { throw '逾時' }; if ($ru.ExitCode -ne 0) { throw "結束碼 $($ru.ExitCode)" } })
        [void](Check '解除安裝:程式已移除' {
            $ok = Wait-Until { -not (Test-Path (Join-Path $inst 'app\simcore')) -and -not (Test-Path (Join-Path $inst 'manifest.json')) } 60
            if (-not $ok) { throw "app\simcore 或 manifest.json 仍在:$((Get-ChildItem -LiteralPath $inst -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join '、')" }
        })
        [void](Check '解除安裝:紀錄、設定與瀏覽器端資料保留' {
            $lost = @()
            foreach ($rel in $had.Keys) { if ($had[$rel] -and -not (Test-Path (Join-Path $inst $rel))) { $lost += $rel } }
            foreach ($rel in $dataDirs) { if (-not (Test-Path (Join-Path (Join-Path $inst $rel) 'ci-sentinel.txt'))) { $lost += "$rel\ci-sentinel.txt" } }
            if ($lost) { throw "原本存在、解除安裝後不見了:$($lost -join '、')" }
            return (($had.Keys | Where-Object { $had[$_] } | Sort-Object) -join '、')
        })
        [void](Check '解除安裝:log 與暫存已清除' {
            $left = @(@('build\logs', 'build\tmp') | Where-Object { Test-Path (Join-Path $inst $_) })
            if ($left) { throw "仍在:$($left -join '、')" }
        } -Soft)
        [void](Check '解除安裝:開始功能表捷徑已移除' {
            $dir = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Simosa BRM'
            if (Test-Path $dir) { throw "$dir 仍在" }
        })
        [void](Check '解除安裝:防火牆規則已移除' {
            $rules = @(Get-NetFirewallRule -ErrorAction Stop | Where-Object { $_.DisplayName -like 'Simosa BRM*' })
            if ($rules.Count -gt 0) { throw "仍有 $($rules.Count) 條" }
        })
        Info '解除安裝後留下的內容' ((Get-ChildItem -LiteralPath $inst -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer } | Measure-Object).Count.ToString() + ' 個檔案')
    }
}

# ======================================================================================================================
Write-Host ''
Write-Host '==== 摘要 ===='
foreach ($lvl in @('FAIL', 'WARN')) {
    foreach ($r in @($script:results | Where-Object { $_.level -eq $lvl })) { Write-Host "[$lvl] $($r.name) — $($r.detail)" }
}
$pass = @($script:results | Where-Object { $_.level -eq 'PASS' }).Count
$fail = @($script:results | Where-Object { $_.level -eq 'FAIL' }).Count
$warn = @($script:results | Where-Object { $_.level -eq 'WARN' }).Count
Write-Host "通過 $pass,失敗 $fail,待確認 $warn"
[System.IO.File]::WriteAllText((Join-Path $work ("results-" + ($Stage -join '-') + ".json")), ($script:results | ConvertTo-Json -Depth 3), (New-Object System.Text.UTF8Encoding($false)))
if ($fail -gt 0) { exit 1 }
exit 0
