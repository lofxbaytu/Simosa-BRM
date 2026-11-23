<#
.SYNOPSIS
  Windows 實機端到端檢查(GitHub Actions 的 Windows 執行器;也可在 Windows 開發機以系統管理員執行)。
.DESCRIPTION
  容器(Linux)無法驗證的 Windows 行為,在這裡以內建的 Windows PowerShell 5.1 實際執行:
    build     :tools\build-release.ps1 -Installer -SkipGodot(完整發行組裝 + Inno Setup 編譯安裝程式)
    install   :靜默安裝 — (a) 指定系統磁碟而未帶 /ALLOWSYSTEMDRIVE=1 時必須不安裝(也不可卡住);(b) 不指定位置時裝到預設位置(D: → G: → 系統磁碟);
               安裝後核對檔案、開始功能表捷徑、防火牆規則、start-ship-laptop.ps1 -CheckOnly、simosa-pkg release verify(一般與 --installed)
    launch    :從安裝位置啟動 — (a) -Duration 短跑(核心 50 Hz、紀錄 footer);(b) -NoWait + 專屬 Edge 設定檔(各埠、網頁 200、Edge 設定檔與下載位置、
               發行版狀態:build\run\release-status.json 的版本 = 安裝的版本、網頁 /_status/release.json 只含發行版本、軟體更新包版次與降版資訊)
               → stop-all.ps1(核心經停止檔寫完 footer、Edge 視窗關閉)→ check-local-data.ps1(安裝版模式)
    shell     :-Shell(WebView2 外殼)啟動 → 使用者資料夾在 build\webview2 → stop-all.ps1 關閉
    packaging :simosa-pkg(安裝位置):信任金鑰、裝置金鑰(Windows 以 DPAPI 保護)、status、更新包 verify / apply / rollback(app\ 的檔案替換)、
               紀錄包 records pack / inspect、教官端 store import、直連同步 serve + sync(本機回送)
    instructor:start-instructor-laptop.ps1 -Standalone(本機核心 + 雷達 + 網頁 + 專屬 Edge 設定檔)→ stop-all.ps1
    reinstall :在使用中的安裝位置上以同一個安裝程式重新安裝(升級 / 修復的情境):程式目錄內殘留的舊檔被清除、
               設定 / 紀錄 / 金鑰 / 帳冊保留、安裝模式核對由失敗變通過
    uninstall :靜默解除安裝 → app\ 移除、紀錄 / 設定 / 瀏覽器端資料保留、捷徑與防火牆規則移除
    summary   :把各階段的結果(build\tmp\windows-e2e\results-*.json)印成一張總表(CI log 的最後一段)
  每個檢查獨立記錄 PASS / FAIL / WARN / INFO,不在第一個失敗就中止(一次 CI 執行取得最多資訊);最後有 FAIL 即結束碼 1。
  WARN:尚未確定 Windows 實際行為的項目(先觀察,不讓工作失敗)。所有暫存與輸出在 <專案>\build\tmp\windows-e2e\。
.PARAMETER Stage
  要執行的階段,逗號分隔(build,install,launch,shell,packaging,instructor,reinstall,uninstall,summary);all = 全部依序。
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
$validStages = @('build', 'install', 'launch', 'shell', 'packaging', 'instructor', 'reinstall', 'uninstall', 'summary', 'all')
foreach ($s in $Stage) { if ($validStages -notcontains $s) { throw "未知的階段 $s(可用:$($validStages -join '、'))" } }
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$work = Join-Path $root 'build\tmp\windows-e2e'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$stateFile = Join-Path $work 'state.json'
$ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($Stage -contains 'all') { $Stage = @('build', 'install', 'launch', 'shell', 'packaging', 'instructor', 'reinstall', 'uninstall', 'summary') }

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
# 核心 log 的收尾段:略過每秒一行的狀態列(t= …),只留事件與關閉訊息(每行最多 200 字);simcore.err.log 有內容時一併印出
function Show-CoreLog([string]$LogDir, [int]$Lines = 24) {
    $log = Join-Path $LogDir 'simcore.log'
    if (-not (Test-Path $log)) { Write-Host "---- ${log}:不存在"; return }
    Write-Host "---- simcore.log(不含每秒狀態列,最後 $Lines 行)----"
    @(Get-Content -Encoding UTF8 $log | Where-Object { $_ -and $_ -notmatch '^t=\s' }) | Select-Object -Last $Lines | ForEach-Object {
        $s = "$_"; if ($s.Length -gt 200) { $s = $s.Substring(0, 200) + '…' }
        Write-Host "    $s"
    }
    $err = Join-Path $LogDir 'simcore.err.log'
    if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) { Show-File $err 12 }
}
# 核心每秒狀態列的「ws N」(狀態 WebSocket 的連線數;雷達主機算 1 個):取最近 15 筆的最大值。
# 用來確認頁面真的在瀏覽器 / WebView2 內載入、執行並連上核心(只看網頁伺服器回 200 不代表頁面能動)
function Get-CoreWsClients([string]$LogDir) {
    $log = Join-Path $LogDir 'simcore.log'
    if (-not (Test-Path $log)) { return -1 }
    $m = [regex]::Matches((Get-Content -Raw -Encoding UTF8 $log), 'steps/s\s+\d+\s+ws\s+(\d+)')
    if ($m.Count -eq 0) { return -1 }
    $vals = @($m | ForEach-Object { [int]$_.Groups[1].Value } | Select-Object -Last 15)
    return [int](($vals | Measure-Object -Maximum).Maximum)
}
function Assert-PagesConnected([string]$LogDir, [string]$What, [int]$Min = 2, [int]$WaitSec = 25) {
    $script:wsSeen = -1
    $ok = Wait-Until { $script:wsSeen = Get-CoreWsClients $LogDir; return ($script:wsSeen -ge $Min) } $WaitSec
    if (-not $ok) { throw "$WaitSec 秒內狀態 WebSocket 連線數最多 $($script:wsSeen)(預期至少 ${Min}:雷達主機 1 個 + ${What} 至少 1 個)" }
    return "連線數 $($script:wsSeen)(含雷達主機 1 個)"
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
                # 發行版狀態(教官站頁首的「發行版 <版本>」):安裝版應列出 simosa-pkg.dll → build\run\release-status.json
                if ($rc.Output -notmatch '發行版狀態:.*simosa-pkg\.dll.*release-status\.json') { throw '輸出沒有發行版狀態的來源(simosa-pkg.dll → build\run\release-status.json)' }
            })
            $rv = Invoke-Proc 'dotnet' @((Join-Path $expected 'tools\updater\simosa-pkg.dll'), 'release', 'verify', '--dir', $expected) 300 $expected
            Show-Tail 'simosa-pkg release verify' $rv.Output 8
            [void](Check 'simosa-pkg release verify(安裝位置)' { if ($rv.ExitCode -ne 0) { throw "結束碼 $($rv.ExitCode)" } })
            $rvi = Invoke-Proc 'dotnet' @((Join-Path $expected 'tools\updater\simosa-pkg.dll'), 'release', 'verify', '--dir', $expected, '--installed') 300 $expected
            Show-Tail 'simosa-pkg release verify --installed(剛安裝)' $rvi.Output 10
            [void](Check '安裝模式核對(release verify --installed):剛安裝好' { if ($rvi.ExitCode -ne 0) { throw "結束碼 $($rvi.ExitCode)" } })
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
        # 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs\design\packaging-sync.md 第 5.1 節):啟動腳本在啟動網頁伺服前以
        # simosa-pkg status --json 寫出 build\run\release-status.json(-NoSync 也要有);simosa-web 以 /_status/release.json 只提供網頁需要的欄位
        [void](Check '發行版狀態檔 build\run\release-status.json:存在,版本等於安裝的版本' {
            $f = Join-Path $inst 'build\run\release-status.json'
            if (-not (Test-Path $f)) { throw "沒有 ${f}(啟動輸出有沒有「發行版狀態」:$($rb.Output -match '發行版狀態'))" }
            $bytes = [System.IO.File]::ReadAllBytes($f)
            if ($bytes.Length -lt 2 -or $bytes[0] -ne 0x7B) { throw "不是無 BOM 的 JSON(第一個位元組 0x$('{0:X2}' -f $bytes[0]))" }
            $st = Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json
            if ("$($st.type)" -ne 'statusResult') { throw "type = $($st.type)(預期 statusResult)" }
            if ("$($st.releaseVersion)" -ne $version) { throw "releaseVersion = $($st.releaseVersion)(預期 $version)" }
            if ($null -ne $st.releaseDowngrade) { throw "剛安裝不應有降版資訊:$($st.releaseDowngrade.message)" }
            if ($rb.Output -notmatch ('發行版狀態:發行版 ' + [regex]::Escape($version))) { throw '啟動輸出沒有「發行版狀態:發行版 <版本>」' }
            return "發行版 $($st.releaseVersion)($($bytes.Length) bytes)"
        })
        [void](Check '網頁 http://127.0.0.1:8080/_status/release.json:200、no-store、只有發行版本、軟體更新包版次與降版資訊' {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8080/_status/release.json' -TimeoutSec 10
            if ($resp.StatusCode -ne 200) { throw "狀態碼 $($resp.StatusCode)" }
            if ("$($resp.Headers['Cache-Control'])" -ne 'no-store') { throw "Cache-Control = $($resp.Headers['Cache-Control'])(預期 no-store)" }
            $body = "$($resp.Content)"
            $j = $body | ConvertFrom-Json
            $names = (@($j.PSObject.Properties | ForEach-Object { $_.Name }) | Sort-Object) -join ','
            if ($names -ne 'format,generatedUtc,releaseDowngrade,releaseVersion,softwareVersion') { throw "欄位:${names}(預期只有 format、generatedUtc、releaseDowngrade、releaseVersion、softwareVersion)" }
            if ("$($j.format)" -ne 'simosa-release-status/1') { throw "format = $($j.format)" }
            if ("$($j.releaseVersion)" -ne $version) { throw "releaseVersion = $($j.releaseVersion)(預期 $version)" }
            if ($null -ne $j.releaseDowngrade) { throw '剛安裝不應有降版資訊' }
            if ($null -ne $j.softwareVersion) { throw "剛安裝(沒套用過軟體更新包)softwareVersion 應為 null,實際 $($j.softwareVersion)" }
            foreach ($leak in @('deviceId', 'trustedKeys', 'environment', 'warnings')) { if ($body.IndexOf($leak) -ge 0) { throw "回應含 status 的其他欄位 $leak" } }
            return "$body"
        })
        [void](Check '網頁 /_status/ 之下其他路徑回 404' {
            foreach ($u in @('/_status/', '/_status/other.json', '/_STATUS/release.json')) {
                $code = 0
                try { $code = [int](Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8080$u" -TimeoutSec 10).StatusCode }
                catch { if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode } }
                if ($code -ne 404) { throw "$u 的狀態碼 $code(預期 404)" }
            }
        })
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
        if ($edgeOk) { [void](Check 'Edge 內的頁面已連上核心(狀態 WebSocket 連線數)' { return (Assert-PagesConnected $logs 'Edge 頁面') } -Soft) }

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
            [void](Check 'WebView2 外殼內的頁面已連上核心(狀態 WebSocket 連線數)' { return (Assert-PagesConnected $logs 'WebView2 外殼頁面') } -Soft)
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
if ($Stage -contains 'packaging') {
    Write-Host ''; Write-Host '==== packaging:simosa-pkg(信任金鑰、裝置金鑰、更新包、紀錄包、直連同步)===='
    $inst = Get-InstallDir
    $keys = Join-Path $root 'build\release\dev-keys'
    $relPkg = Join-Path $root "build\release\$version\packages\simosa-$version-software.sbupd"
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    elseif (-not (Test-Path (Join-Path $keys 'dev-primary.key.pem'))) { Add-Result 'FAIL' '開發金鑰' "找不到 $keys\dev-primary.key.pem(先執行 build 階段)" }
    else {
        $pkgDll = Join-Path $inst 'tools\updater\simosa-pkg.dll'
        $script:pkgSeq = 0
        # 執行 simosa-pkg 並把輸出印出來(每個指令的最後幾行);回傳 Invoke-Proc 的結果
        function Invoke-Pkg([string[]]$a, [int]$TimeoutSec = 300, [int]$Show = 8) {
            $r = Invoke-Proc 'dotnet' (@($pkgDll) + $a) $TimeoutSec $inst
            Show-Tail ("simosa-pkg " + ($a -join ' ')) $r.Output $Show
            return $r
        }
        # 1) 信任金鑰(公鑰)
        $r1 = Invoke-Pkg @('trust', 'add', '--root', $inst, '--file', (Join-Path $keys 'dev-primary.pub.pem'), '--admin', 'CI')
        $r2 = Invoke-Pkg @('trust', 'add', '--root', $inst, '--file', (Join-Path $keys 'dev-backup.pub.pem'), '--admin', 'CI')
        [void](Check 'trust add(主金鑰與備援金鑰的公鑰)' { if ($r1.ExitCode -ne 0 -or $r2.ExitCode -ne 0) { throw "結束碼 $($r1.ExitCode) / $($r2.ExitCode)" }; return ((Get-ChildItem -LiteralPath (Join-Path $inst 'config\trusted-keys') | ForEach-Object { $_.Name }) -join '、') })
        # 2) 裝置金鑰(Windows:DPAPI 綁定本機帳號)
        $r3 = Invoke-Pkg @('device', 'init', '--root', $inst, '--ship', 'FSB1', '--device-id', 'FSB1-CI')
        [void](Check 'device init(裝置金鑰;Windows 以 DPAPI 保護)' {
            if ($r3.ExitCode -ne 0) { throw "結束碼 $($r3.ExitCode)" }
            $files = @(Get-ChildItem -LiteralPath (Join-Path $inst 'config\device') -File)
            if ($files.Count -eq 0) { throw 'config\device 沒有檔案' }
            $plain = @($files | Where-Object { (Get-Content -Raw -LiteralPath $_.FullName) -match 'BEGIN (EC |RSA )?PRIVATE KEY' })
            if ($plain) { throw "私鑰以明文 PEM 存放:$(($plain | ForEach-Object { $_.Name }) -join '、')" }
            if (-not (Test-Path (Join-Path $inst 'config\packaging.json'))) { throw '沒有產生 config\packaging.json' }
            return (($files | ForEach-Object { "$($_.Name)($($_.Length))" }) -join '、')
        })
        $r4 = Invoke-Pkg @('device', 'show', '--root', $inst)
        [void](Check 'device show' { if ($r4.ExitCode -ne 0) { throw "結束碼 $($r4.ExitCode)" } })
        $r5 = Invoke-Pkg @('status', '--root', $inst, '--json') 300 40
        $status0 = $null
        [void](Check 'status --json(初始安裝)' {
            if ($r5.ExitCode -ne 0) { throw "結束碼 $($r5.ExitCode)" }
            $script:st = $r5.Output | ConvertFrom-Json
            return "環境:$(($script:st.environment | ConvertTo-Json -Compress -Depth 3))"
        })
        # 3) 更新包:驗證 → 套用(app\ 整個替換)→ 核對 → 回復
        $haveRelPkg = Check '發行版的軟體更新包存在' { if (-not (Test-Path $relPkg)) { throw "沒有 $relPkg" }; return "$([math]::Round((Get-Item $relPkg).Length / 1MB, 1)) MB" }
        if ($haveRelPkg) {
            $rv = Invoke-Pkg @('verify', $relPkg, '--root', $inst)
            $verified = Check '更新包 verify(簽章、信任金鑰、版本)' { if ($rv.ExitCode -ne 0) { throw "結束碼 $($rv.ExitCode)" } }
            $before = (Get-Item (Join-Path $inst 'app\simcore\SimosaBRM.SimCore.Host.dll')).LastWriteTimeUtc
            $ra = Invoke-Pkg @('apply', $relPkg, '--root', $inst, '--operator', 'CI') 600 12
            $applied = Check '更新包 apply(Windows 上替換 app\)' {
                if ($ra.TimedOut) { throw '逾時' }
                if ($ra.ExitCode -ne 0) { throw "結束碼 $($ra.ExitCode)" }
                foreach ($rel in @('app\simcore\SimosaBRM.SimCore.Host.dll', 'app\launch\start-ship-laptop.ps1', 'app\shell\SimosaBRM.Shell.exe', 'app\web\index.html')) {
                    if (-not (Test-Path (Join-Path $inst $rel))) { throw "套用後缺 $rel" }
                }
                if (-not (Test-Path (Join-Path $inst 'state\installed.json'))) { throw '沒有 state\installed.json' }
            }
            if ($applied) {
                $rs2 = Invoke-Pkg @('status', '--root', $inst)
                [void](Check '套用後 status 顯示版本與可回復' { if ($rs2.ExitCode -ne 0) { throw "結束碼 $($rs2.ExitCode)" }; if ($rs2.Output.IndexOf($version) -lt 0) { throw "輸出沒有版本 $version" } })
                $rc2 = Invoke-Ps51 (Join-Path $inst 'app\launch\start-ship-laptop.ps1') @('-CheckOnly') 300 $inst
                [void](Check '套用後 start-ship-laptop.ps1 -CheckOnly' { if ($rc2.ExitCode -ne 0) { throw "結束碼 $($rc2.ExitCode):$($rc2.Output)" } })
                # 使用中的安裝根目錄要用安裝模式核對(一般模式把執行期資料、改過的設定與更新包替換的檔案都算成不符;run 85–88 都是結束碼 1)
                $rr = Invoke-Pkg @('release', 'verify', '--dir', $inst, '--installed') 600 14
                [void](Check '套用更新包後 安裝模式核對通過(執行期資料、信任金鑰、裝置金鑰、更新包覆蓋清單)' {
                    if ($rr.ExitCode -ne 0) { throw "結束碼 $($rr.ExitCode)" }
                    if (-not (Test-Path (Join-Path $inst 'state\installed-files.json'))) { throw '套用後沒有 state\installed-files.json(更新包覆蓋清單)' }
                })
                $rplain = Invoke-Pkg @('release', 'verify', '--dir', $inst) 600 4
                [void](Check '一般模式對使用中的安裝根目錄:結束碼非 0 並提示改用 --installed' {
                    if ($rplain.ExitCode -eq 0) { throw '結束碼 0(預期非 0:目錄內有執行期資料)' }
                    if ($rplain.Output -notmatch '--installed') { throw '輸出沒有提示 --installed' }
                } -Soft)
                # 損毀偵測:把更新包寫入的一個程式檔改掉一個位元組 → 必須結束碼非 0 並指出該檔;還原後恢復通過
                $victim = Join-Path $inst 'app\launch\stop-all.ps1'
                $orig = [System.IO.File]::ReadAllBytes($victim)
                try {
                    $bad = [byte[]]$orig.Clone(); $bad[$bad.Length - 1] = [byte]($bad[$bad.Length - 1] -bxor 0x20)
                    [System.IO.File]::WriteAllBytes($victim, $bad)
                    $rt = Invoke-Pkg @('verify-install', '--root', $inst) 600 14
                    [void](Check '安裝模式核對抓到被改動的程式檔(app\launch\stop-all.ps1 改一個位元組)' {
                        if ($rt.ExitCode -eq 0) { throw '結束碼 0(應為非 0)' }
                        if ($rt.Output -notmatch 'stop-all\.ps1') { throw '輸出沒有指出 stop-all.ps1' }
                        return "結束碼 $($rt.ExitCode)"
                    })
                } finally { [System.IO.File]::WriteAllBytes($victim, $orig) }
                $stray = Join-Path $inst 'app\simcore\ci-stray.dll'
                Set-Content -Path $stray -Value 'x' -Encoding ASCII
                try {
                    $rs4 = Invoke-Pkg @('verify-install', '--root', $inst) 600 10
                    [void](Check '安裝模式核對抓到程式目錄內多出的檔案(app\simcore\ci-stray.dll)' {
                        if ($rs4.ExitCode -eq 0) { throw '結束碼 0(應為非 0)' }
                        if ($rs4.Output -notmatch 'ci-stray\.dll') { throw '輸出沒有指出 ci-stray.dll' }
                    })
                } finally { Remove-Item -LiteralPath $stray -Force -ErrorAction SilentlyContinue }
                $rok = Invoke-Pkg @('verify-install', '--root', $inst) 600 6
                [void](Check '還原後 安裝模式核對恢復通過' { if ($rok.ExitCode -ne 0) { throw "結束碼 $($rok.ExitCode)" } })
                # 第二次套用同一版本應被拒絕(版本須遞增)
                $ra2 = Invoke-Pkg @('apply', $relPkg, '--root', $inst, '--operator', 'CI') 600 6
                [void](Check '同一版本再次套用被拒絕(版本須遞增)' { if ($ra2.ExitCode -eq 0) { throw '結束碼 0(應拒絕)' }; return "結束碼 $($ra2.ExitCode)" } -Soft)
                $rb = Invoke-Pkg @('rollback', '--root', $inst, '--operator', 'CI') 600 10
                [void](Check 'rollback(回復到套用前的 app\)' {
                    if ($rb.ExitCode -ne 0) { throw "結束碼 $($rb.ExitCode)" }
                    if (-not (Test-Path (Join-Path $inst 'app\simcore\SimosaBRM.SimCore.Host.dll'))) { throw '回復後缺 app\simcore\SimosaBRM.SimCore.Host.dll' }
                })
                $rc3 = Invoke-Ps51 (Join-Path $inst 'app\launch\start-ship-laptop.ps1') @('-CheckOnly') 300 $inst
                [void](Check '回復後 start-ship-laptop.ps1 -CheckOnly' { if ($rc3.ExitCode -ne 0) { throw "結束碼 $($rc3.ExitCode)" } })
                $rrb = Invoke-Pkg @('release', 'verify', '--dir', $inst, '--installed') 600 10
                [void](Check '回復(rollback)後 安裝模式核對通過(覆蓋清單一併回復)' {
                    if ($rrb.ExitCode -ne 0) { throw "結束碼 $($rrb.ExitCode)" }
                    if (Test-Path (Join-Path $inst 'state\installed-files.json')) { throw '回復到初始安裝後 state\installed-files.json 仍在' }
                })
            }
        }
        # 4) 紀錄包:打包(裝置金鑰簽章)→ 檢視 → 教官端匯入
        $rp = Invoke-Pkg @('records', 'pack', '--root', $inst) 600 10
        $sbrec = $null
        $packed = Check 'records pack(以裝置金鑰簽章的紀錄包)' {
            if ($rp.ExitCode -ne 0) { throw "結束碼 $($rp.ExitCode)" }
            $script:sbrec = Get-ChildItem -LiteralPath (Join-Path $inst 'state\outbox') -Filter '*.sbrec' -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $script:sbrec) { throw 'state\outbox 沒有 .sbrec' }
            return "$($script:sbrec.Name)($([math]::Round($script:sbrec.Length / 1KB)) KB)"
        }
        if ($packed) {
            $ri = Invoke-Pkg @('inspect', $script:sbrec.FullName) 300 14
            [void](Check 'inspect 紀錄包' { if ($ri.ExitCode -ne 0) { throw "結束碼 $($ri.ExitCode)" } })
            $store1 = Join-Path $work 'store-import'
            if (Test-Path $store1) { Remove-Item -Recurse -Force $store1 }
            $rim = Invoke-Pkg @('store', 'import', $script:sbrec.FullName, '--store', $store1, '--enroll-device') 300 10
            [void](Check '教官端 store import(--enroll-device)' { if ($rim.ExitCode -ne 0) { throw "結束碼 $($rim.ExitCode)" }; if (-not (Test-Path $store1)) { throw "沒有建立 $store1" } })
            $rl = Invoke-Pkg @('store', 'list', '--store', $store1) 300 10
            [void](Check 'store list' { if ($rl.ExitCode -ne 0) { throw "結束碼 $($rl.ExitCode)" } })
        }
        # 5) 直連同步:船端 serve(本機回送)+ 教官端 sync(取回紀錄、回報)
        $serveOut = Join-Path $work 'serve.out.txt'; $serveErr = Join-Path $work 'serve.err.txt'
        $serve = Start-Process -FilePath 'dotnet' -ArgumentList ('"' + $pkgDll + '" serve --root "' + $inst + '" --listen 127.0.0.1 --port 7300') -WorkingDirectory $inst -PassThru -WindowStyle Hidden -RedirectStandardOutput $serveOut -RedirectStandardError $serveErr
        try {
            $up = Check 'serve 監聽 127.0.0.1:7300' { if (-not (Wait-Until { Test-Tcp '127.0.0.1' 7300 } 20)) { throw "20 秒內未開啟;程序已結束 = $($serve.HasExited)" } }
            if ($up) {
                $store2 = Join-Path $work 'store-sync'
                if (Test-Path $store2) { Remove-Item -Recurse -Force $store2 }
                $rsync = Invoke-Pkg @('sync', '--ship', '127.0.0.1', '--port', '7300', '--key', (Join-Path $keys 'dev-primary.key.pem'), '--store', $store2, '--enroll-device') 300 16
                [void](Check 'sync(教官端經直連取回紀錄包)' { if ($rsync.TimedOut) { throw '逾時' }; if ($rsync.ExitCode -ne 0) { throw "結束碼 $($rsync.ExitCode)" } } -Soft)
            }
        } finally {
            if (-not $serve.HasExited) { & taskkill.exe /PID $serve.Id /T /F 2>&1 | Out-Null }
            Show-File $serveOut 12
            if ((Test-Path $serveErr) -and (Get-Item $serveErr).Length -gt 0) { Show-File $serveErr 12 }
        }
        $rs3 = Invoke-Pkg @('status', '--root', $inst) 300 14
        [void](Check '最後 status' { if ($rs3.ExitCode -ne 0) { throw "結束碼 $($rs3.ExitCode)" } })
        $rfin = Invoke-Pkg @('verify-install', '--root', $inst) 600 12
        [void](Check '最後 安裝模式核對(啟動過、外殼跑過、打包與同步過紀錄之後)' { if ($rfin.ExitCode -ne 0) { throw "結束碼 $($rfin.ExitCode)" } })
    }
}

# ======================================================================================================================
if ($Stage -contains 'instructor') {
    Write-Host ''; Write-Host '==== instructor:教官筆電單機模式(-Standalone)===='
    $inst = Get-InstallDir
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    else {
        $launch = Join-Path $inst 'app\launch'
        $logs = Join-Path $inst 'build\logs'
        $rb = Invoke-Ps51 (Join-Path $launch 'start-instructor-laptop.ps1') @('-Standalone', '-NoWait') 240 $inst
        Show-Tail 'start-instructor-laptop.ps1 -Standalone -NoWait' $rb.Output 30
        [void](Check '教官筆電 -Standalone 啟動結束碼 0' { if ($rb.TimedOut) { throw '逾時' }; if ($rb.ExitCode -ne 0) { throw "結束碼 $($rb.ExitCode)" } })
        [void](Check '本機核心 8765 與雷達 8766 開啟' { if (-not (Wait-Until { (Test-Tcp '127.0.0.1' 8765) -and (Test-Tcp '127.0.0.1' 8766) } 40)) { throw '40 秒內未開啟' } })
        foreach ($page in @('instructor.html', 'radar.html', 'ecdis.html', 'report.html')) {
            [void](Check "教官筆電網頁 http://127.0.0.1:8080/$page 回 200" {
                $resp = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8080/$page" -TimeoutSec 10
                if ($resp.StatusCode -ne 200) { throw "狀態碼 $($resp.StatusCode)" }
            })
        }
        # 教官筆電的教官站頁面由本機的 simosa-web 提供:頁首顯示的是這台(教官筆電)的發行版
        [void](Check '教官筆電網頁 /_status/release.json:發行版本等於安裝的版本' {
            $resp = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:8080/_status/release.json' -TimeoutSec 10
            $j = "$($resp.Content)" | ConvertFrom-Json
            if ("$($j.format)" -ne 'simosa-release-status/1' -or "$($j.releaseVersion)" -ne $version) { throw "內容:$($resp.Content)" }
            return "發行版 $($j.releaseVersion)"
        })
        $profileDir = Join-Path $inst 'build\browser\edge'
        [void](Check '教官站以專屬 Edge 設定檔開啟' {
            if (-not (Wait-Until { @(Get-ProcByCommandLine 'msedge.exe' $profileDir).Count -gt 0 } 40)) { throw "40 秒內沒有命令列含 $profileDir 的 msedge.exe" }
        } -Soft)
        [void](Check '教官站頁面已連上本機核心(狀態 WebSocket 連線數)' { return (Assert-PagesConnected $logs '教官站頁面') } -Soft)
        Start-Sleep -Seconds 5
        $rs = Invoke-Ps51 (Join-Path $launch 'stop-all.ps1') @() 120 $inst
        Show-Tail 'stop-all.ps1' $rs.Output 14
        Show-CoreLog $logs
        [void](Check 'stop-all.ps1(教官筆電)結束碼 0、未強制結束核心' { if ($rs.ExitCode -ne 0) { throw "結束碼 $($rs.ExitCode)" }; if ($rs.Output -match '強制結束') { throw '輸出含「強制結束」' } })
        [void](Check 'stop-all(教官筆電):各埠已關閉、Edge 已關閉' {
            if (-not (Wait-Until { -not ((Test-Tcp '127.0.0.1' 8765 300) -or (Test-Tcp '127.0.0.1' 8766 300) -or (Test-Tcp '127.0.0.1' 8080 300)) } 15)) { throw '15 秒後仍有埠開著' }
            if (-not (Wait-Until { @(Get-ProcByCommandLine 'msedge.exe' $profileDir).Count -eq 0 } 15)) { throw '15 秒後仍有專屬設定檔的 msedge' }
        })
        foreach ($e in @(Get-ProcByCommandLine 'msedge.exe' $profileDir | Where-Object { $_.CommandLine -notmatch '--type=' })) { & taskkill.exe /PID $e.ProcessId /T /F 2>&1 | Out-Null }
        Save-InstalledLogs $inst 'instructor'
    }
}

# ======================================================================================================================
if ($Stage -contains 'reinstall') {
    Write-Host ''; Write-Host '==== reinstall:在使用中的安裝位置上重新安裝(升級 / 修復)===='
    $inst = Get-InstallDir
    $setup = Join-Path $root "build\release\installer\SimosaBRM-Setup-$version.exe"
    if (-not $inst) { Add-Result 'FAIL' '安裝位置' '找不到(先執行 install 階段)' }
    elseif (-not (Test-Path $setup)) { Add-Result 'FAIL' '安裝程式存在' "沒有 $setup(先執行 build 階段)" }
    else {
        $pkgDll = Join-Path $inst 'tools\updater\simosa-pkg.dll'
        # 模擬舊版留下的程式檔(Vite 每次建置的資產檔名雜湊都不同;舊更新包新增的檔案)與使用者資料
        $stale = @('app\web\assets\ci-stale-0000.js', 'app\simcore\ci-stale.dll', 'tools\updater\ci-stale.dll')
        foreach ($rel in $stale) { Set-Content -Path (Join-Path $inst $rel) -Value 'stale' -Encoding ASCII }
        $keep = @('build\records\ci-keep.txt', 'data\records\ci-keep.txt', 'state\ci-keep.txt', 'store\ci-keep.txt', 'config\trusted-keys\ci-keep.txt', 'config\device\ci-keep.txt')
        foreach ($rel in $keep) { $f = Join-Path $inst $rel; New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null; Set-Content -Path $f -Value 'keep' -Encoding ASCII }
        $cfg = Join-Path $inst 'config\ship-laptop.json'
        $cfgHash = if (Test-Path $cfg) { (Get-FileHash -Algorithm SHA256 -LiteralPath $cfg).Hash } else { '' }
        $r0 = Invoke-Proc 'dotnet' @($pkgDll, 'verify-install', '--root', $inst) 600 $inst
        Show-Tail 'verify-install(重新安裝前,有殘留檔)' $r0.Output 12
        [void](Check '重新安裝前:安裝模式核對指出程式目錄內多出的檔案' {
            if ($r0.ExitCode -eq 0) { throw '結束碼 0(應為非 0)' }
            foreach ($n in @('ci-stale-0000.js', 'ci-stale.dll')) { if ($r0.Output.IndexOf($n) -lt 0) { throw "輸出沒有 $n" } }
        })
        $logR = Join-Path $work 'reinstall.log'
        $rr = Invoke-Proc $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$inst", "/LOG=$logR") 900
        $ok = Check '重新安裝(同一位置,靜默)結束碼 0' { if ($rr.TimedOut) { throw '逾時' }; if ($rr.ExitCode -ne 0) { throw "結束碼 $($rr.ExitCode)" } }
        if (-not $ok -and (Test-Path $logR)) { Show-File $logR 40 }
        [void](Check '重新安裝:程式目錄內殘留的舊檔已清除(安裝程式 [InstallDelete])' {
            $left = @($stale | Where-Object { Test-Path (Join-Path $inst $_) })
            if ($left) { throw "仍在:$($left -join '、')" }
        })
        [void](Check '重新安裝:程式檔齊全' {
            foreach ($rel in @('app\simcore\SimosaBRM.SimCore.Host.dll', 'app\launch\start-ship-laptop.ps1', 'app\web\index.html', 'tools\updater\simosa-pkg.dll', 'manifest.json')) {
                if (-not (Test-Path (Join-Path $inst $rel))) { throw "缺 $rel" }
            }
        })
        [void](Check '重新安裝:設定、紀錄、金鑰、帳冊保留' {
            $lost = @($keep | Where-Object { -not (Test-Path (Join-Path $inst $_)) })
            if ($lost) { throw "不見了:$($lost -join '、')" }
            if ($cfgHash -and (Get-FileHash -Algorithm SHA256 -LiteralPath $cfg).Hash -ne $cfgHash) { throw 'config\ship-laptop.json 被覆寫' }
            foreach ($rel in @('config\device\device.json', 'config\packaging.json')) { if (-not (Test-Path (Join-Path $inst $rel))) { throw "不見了:$rel" } }
        })
        $r1 = Invoke-Proc 'dotnet' @($pkgDll, 'verify-install', '--root', $inst) 600 $inst
        Show-Tail 'verify-install(重新安裝後)' $r1.Output 12
        [void](Check '重新安裝後:安裝模式核對通過' { if ($r1.ExitCode -ne 0) { throw "結束碼 $($r1.ExitCode)" } })
        $rc = Invoke-Ps51 (Join-Path $inst 'app\launch\start-ship-laptop.ps1') @('-CheckOnly') 300 $inst
        [void](Check '重新安裝後 start-ship-laptop.ps1 -CheckOnly' { if ($rc.ExitCode -ne 0) { throw "結束碼 $($rc.ExitCode):$($rc.Output)" } })
        foreach ($rel in $keep) { Remove-Item -LiteralPath (Join-Path $inst $rel) -Force -ErrorAction SilentlyContinue }
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
if ($Stage -contains 'summary') {
    Write-Host ''; Write-Host '==== summary:各階段結果總表 ===='
    $total = @{ PASS = 0; FAIL = 0; WARN = 0; INFO = 0 }
    foreach ($f in @(Get-ChildItem -LiteralPath $work -Filter 'results-*.json' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)) {
        if ($f.Name -eq 'results-summary.json') { continue }
        Write-Host "-- $($f.BaseName -replace '^results-', '')"
        # Windows PowerShell 5.1 的 ConvertFrom-Json 把整個 JSON 陣列當成「一個物件」送進管線:先存變數再用 ForEach-Object 展開
        $parsed = Get-Content -Raw -Encoding UTF8 $f.FullName | ConvertFrom-Json
        foreach ($r in @($parsed | ForEach-Object { $_ })) {
            if ($null -eq $r) { continue }
            $lvl = "$($r.level)"
            if ($total.ContainsKey($lvl)) { $total[$lvl] = $total[$lvl] + 1 }
            $d = "$($r.detail)"
            if ($d.Length -gt 150) { $d = $d.Substring(0, 150) + '…' }
            if ($d) { Write-Host "   [$($r.level)] $($r.name) — $d" } else { Write-Host "   [$($r.level)] $($r.name)" }
        }
    }
    Write-Host "總計:通過 $($total.PASS),失敗 $($total.FAIL),待確認 $($total.WARN)"
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
# 結果寫成 JSON 陣列(供 summary 階段彙整)。Windows PowerShell 5.1 對 List[object] 用 ConvertTo-Json -InputObject 會丟
# 「Argument types do not match」,所以走管線;只有一筆時管線輸出不是陣列,自行補上方括號
$resultArray = @($script:results.ToArray())
$resultJson = '[]'
if ($resultArray.Count -eq 1) { $resultJson = '[' + ($resultArray[0] | ConvertTo-Json -Depth 3) + ']' }
elseif ($resultArray.Count -gt 1) { $resultJson = ($resultArray | ConvertTo-Json -Depth 3) }
if ($Stage -notcontains 'summary' -or $Stage.Count -gt 1) {
    [System.IO.File]::WriteAllText((Join-Path $work ("results-" + ($Stage -join '-') + ".json")), $resultJson, (New-Object System.Text.UTF8Encoding($false)))
}
if ($fail -gt 0) { exit 1 }
exit 0
