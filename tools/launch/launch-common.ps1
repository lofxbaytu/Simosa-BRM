# 啟動腳本共用函式(Windows;Linux 版為 launch-common.sh):找安裝根目錄、讀 config\*.json、找各元件、背景啟動並記 PID。
# 安裝根目錄 = 本檔上兩層(儲存庫:tools\launch\..\..;發行版:app\launch\..\..),必須含 data\。
# 所有輸出留在根目錄內:build\logs\<元件>.log、build\logs\simosa.pids、build\logs\simcore.stop[file](規劃書第 11.2 節、CLAUDE.md:不寫 C:\)。
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:LaunchDir = $PSScriptRoot
# Windows PowerShell 5.1 沒有 $IsWindows;Linux 上的 pwsh(測試用)不支援 Start-Process -WindowStyle 與 CloseMainWindow
$script:OnWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)

function Get-LaunchRoot {
    $root = (Resolve-Path (Join-Path $script:LaunchDir '..\..')).Path
    if (-not (Test-Path (Join-Path $root 'data'))) { throw "安裝根目錄 $root 沒有 data\" }
    return $root
}

# Test-LaunchUnderRoot <路徑> <根目錄>:路徑等於根目錄或在其下(不分大小寫;路徑不必存在)
function Test-LaunchUnderRoot([string]$Path, [string]$Root) {
    if (-not $Path -or -not $Root) { return $false }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $false }
    $r = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return ($full.Equals($r, [System.StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($r + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase))
}

# Test-LaunchOnSystemDrive <根目錄>:根目錄是否在 Windows 的系統磁碟($env:SystemDrive,通常 C:);非 Windows 一律 $false
function Test-LaunchOnSystemDrive([string]$Root) {
    $sys = $env:SystemDrive
    if (-not $sys -or -not $Root) { return $false }
    return $Root.StartsWith($sys.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)
}

# 把工具快取與暫存檔導到安裝根目錄內(守則:本機資料放 D: 或 G:,不寫 C:\;只影響本程序與由它啟動的子程序):
#   .NET CLI 家目錄、NuGet 套件 / HTTP 快取 / 外掛快取 / 暫存(後三者只能用環境變數改,nuget.config 無對應鍵)。
#   已設定且在根目錄內者沿用;未設定、或指到根目錄以外者(例如專案從 D: 複製到 G: 後殘留的使用者層級設定、或指到 C: 的舊值)改指到本根目錄;
#   TEMP / TMP → <根目錄>\build\tmp。環境變數 SIMOSA_SYSTEM_TEMP=1 時不改 TEMP/TMP(暫存留在 Windows 預設的
#   C:\Users\…\AppData\Local\Temp;放在隨身碟會明顯拖慢時,經訓練負責人同意才使用)。
#   NODE_COMPILE_CACHE / npm_config_cache / PWTEST_CACHE_DIR:只在「已設定且指到根目錄外」時改指回(其餘如 PNPM_HOME、UV_*、
#   PLAYWRIGHT_BROWSERS_PATH 不處理:換磁碟後請重新執行 setup-windows.ps1 或改用 dev-shell.cmd)。
#   根目錄在系統磁碟時警告(守則;經同意後以環境變數 SIMOSA_ALLOW_SYSTEM_DRIVE=1 關閉警告)。
function Set-LaunchProjectEnv([string]$Root) {
    if ((Test-LaunchOnSystemDrive $Root) -and $env:SIMOSA_ALLOW_SYSTEM_DRIVE -ne '1') {
        Write-Warning "根目錄 $Root 位於系統磁碟 $($env:SystemDrive):守則為本機資料放 D: 或 G:;放系統磁碟須先經訓練負責人同意(同意後設環境變數 SIMOSA_ALLOW_SYSTEM_DRIVE=1 可關閉此警告;docs\ops\本機資料位置.md)"
    }
    $defaults = [ordered]@{
        'DOTNET_CLI_HOME'          = @('.cache', 'dotnet-home')
        'NUGET_PACKAGES'           = @('.cache', 'nuget', 'packages')
        'NUGET_HTTP_CACHE_PATH'    = @('.cache', 'nuget', 'http')
        'NUGET_PLUGINS_CACHE_PATH' = @('.cache', 'nuget', 'plugins')
        'NUGET_SCRATCH'            = @('.cache', 'nuget', 'scratch')
    }
    foreach ($k in $defaults.Keys) {
        $cur = [Environment]::GetEnvironmentVariable($k)
        if (-not (Test-LaunchUnderRoot $cur $Root)) {
            $p = $Root; foreach ($seg in $defaults[$k]) { $p = Join-Path $p $seg }
            if ($cur) { Write-Host "  $k 原為 $cur(不在 $Root 內),本次改用 $p" }
            [Environment]::SetEnvironmentVariable($k, $p)
        }
    }
    # Node 的快取(pnpm / node 由 build-release.ps1、soak-test.ps1 執行):未設定時預設在 TEMP 之下或不使用,不必處理;
    # 已設定但指到根目錄外(換磁碟後殘留的使用者層級設定)者改指回本根目錄
    $ifSetOutside = [ordered]@{
        'NODE_COMPILE_CACHE' = @('.cache', 'node-compile-cache')
        'npm_config_cache'   = @('.cache', 'npm')
        'PWTEST_CACHE_DIR'   = @('.cache', 'playwright-transform')
    }
    foreach ($k in $ifSetOutside.Keys) {
        $cur = [Environment]::GetEnvironmentVariable($k)
        if ($cur -and -not (Test-LaunchUnderRoot $cur $Root)) {
            $p = $Root; foreach ($seg in $ifSetOutside[$k]) { $p = Join-Path $p $seg }
            Write-Host "  $k 原為 $cur(不在 $Root 內),本次改用 $p"
            [Environment]::SetEnvironmentVariable($k, $p)
        }
    }
    if ($env:SIMOSA_SYSTEM_TEMP -ne '1') {
        $tmp = Join-Path (Join-Path $Root 'build') 'tmp'
        # 建立暫存資料夾不算「輸出」:-WhatIf 模式下仍建立(否則子程序拿到不存在的暫存目錄)
        New-Item -ItemType Directory -Force -Path $tmp -WhatIf:$false | Out-Null
        $env:TEMP = $tmp; $env:TMP = $tmp
    }
}

# Invoke-LaunchWithGodotUserDir <根目錄> { 啟動動作 }:執行動作期間把 APPDATA / LOCALAPPDATA 指到 <根目錄>\.cache\godot\appdata\{roaming,local},結束後還原。
#   Godot(編輯器與匯出檔)在 Windows 以 APPDATA 決定 user://(project.godot 的 custom_user_dir_name → %APPDATA%\SimosaBRM-Visual:引擎 log、
#   著色器與管線快取);不導向會寫到 C:\Users\…\AppData\Roaming。子程序在啟動當下繼承環境變數,所以只要包住「啟動」即可;
#   還原是為了不影響之後啟動的程序(同步服務、瀏覽器)與呼叫端的 PowerShell 工作階段。XDG_* 在 Windows 版 Godot 無效。
function Invoke-LaunchWithGodotUserDir([string]$Root, [scriptblock]$Action) {
    $base = Join-Path (Join-Path (Join-Path $Root '.cache') 'godot') 'appdata'
    $roaming = Join-Path $base 'roaming'; $local = Join-Path $base 'local'
    New-Item -ItemType Directory -Force -Path $roaming, $local -WhatIf:$false | Out-Null
    $oldRoaming = $env:APPDATA; $oldLocal = $env:LOCALAPPDATA
    try {
        $env:APPDATA = $roaming; $env:LOCALAPPDATA = $local
        & $Action
    } finally {
        $env:APPDATA = $oldRoaming; $env:LOCALAPPDATA = $oldLocal
    }
}

function Read-LaunchConfig([string]$Path) {
    if (-not (Test-Path $Path)) { throw "找不到設定檔 $Path" }
    return Get-Content -Raw -Encoding UTF8 $Path | ConvertFrom-Json
}

# 取巢狀設定值:Get-Cfg $cfg 'ports.stateWs' 8765
function Get-Cfg($Config, [string]$Path, $Default = $null) {
    $cur = $Config
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $cur) { return $Default }
        $prop = $cur.PSObject.Properties[$part]
        if ($null -eq $prop) { return $Default }
        $cur = $prop.Value
    }
    if ($null -eq $cur) { return $Default }
    return $cur
}

# 找元件的 .dll:發行版 app\<元件>\<dll>(simosa-pkg 另看 tools\updater)→ 開發 build\bin\<專案>\Release|Debug\net8.0\<dll>(缺少且有原始碼時先建置 Release)
function Find-LaunchDll([string]$Component, [string]$Project, [string]$Dll, [string]$Root) {
    $candidates = @(
        (Join-Path $Root "app\$Component\$Dll"),
        (Join-Path $Root "tools\updater\$Dll"),
        (Join-Path $Root "build\bin\$Project\Release\net8.0\$Dll"),
        (Join-Path $Root "build\bin\$Project\Debug\net8.0\$Dll")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    foreach ($csproj in @((Join-Path $Root "src\$Project\$Project.csproj"), (Join-Path $Root "tools\launch\$Project\$Project.csproj"))) {
        if (Test-Path $csproj) {
            Write-Host "建置 $Project(Release)…"
            # 建置輸出送到畫面(Out-Host),不可流進本函式的回傳值:否則呼叫端拿到的是「建置訊息 + 路徑」的陣列,
            # 接著會把 "Build succeeded." 之類的文字當成 dotnet 的參數(第一次從原始碼啟動、尚未建置時才會發生;CI run 78 發現)
            & dotnet build $csproj -c Release -nologo -v q | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "建置 $Project 失敗" }
            $c = Join-Path $Root "build\bin\$Project\Release\net8.0\$Dll"
            if (Test-Path $c) { return $c }
        }
    }
    return $null
}

$script:PidFile = $null
function Initialize-LaunchLogs([string]$Root, [string]$Logs) {
    $dir = Join-Path $Root $Logs
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $script:PidFile = Join-Path $dir 'simosa.pids'
    if ((Test-Path $script:PidFile) -and (Get-Content $script:PidFile | Where-Object { $_ })) {
        Write-Warning "$($script:PidFile) 仍有上次的 PID,先結束"
        & (Join-Path $script:LaunchDir 'stop-all.ps1') -Logs $Logs -KeepBrowser   # 重新啟動:只清上次的程序,不關使用者開著的 Edge 視窗
    }
    Set-Content -Path $script:PidFile -Value '' -Encoding UTF8
}

# 輪替 log:Start-Process -RedirectStandardOutput 一律覆寫檔案,為保留上一次執行(例如崩潰原因)的內容,啟動前把 simcore.log → simcore.1.log、
# simcore.1.log → simcore.2.log …,最多保留 $Keep 份(.sh 版以 >> 追加,不需輪替)
function Save-LaunchLogHistory([string]$Path, [int]$Keep = 3) {
    if (-not (Test-Path $Path)) { return }
    $dir = Split-Path -Parent $Path
    $base = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    $ext = [System.IO.Path]::GetExtension($Path)
    for ($i = $Keep; $i -ge 1; $i--) {
        $src = if ($i -eq 1) { $Path } else { Join-Path $dir "$base.$($i - 1)$ext" }
        if (Test-Path $src) { Move-Item -Force -Path $src -Destination (Join-Path $dir "$base.$i$ext") }
    }
}

# Start-LaunchProcess -Name simcore -Log <路徑> -File dotnet -Arguments @(...):背景啟動(隱藏視窗),stdout/stderr 寫 log,PID 記入 simosa.pids。
# 啟動標頭(時間、名稱、PID、指令與參數)追加寫到同資料夾的 launch.log(Start-Process 會覆寫 $Log,寫在 $Log 內會遺失);上一次的 log 輪替保留。
# 參數不可命名為 Args:會被自動變數 args(未繫結的引數,此處恆為空)遮蔽,程序會以沒有參數的方式啟動。
function Start-LaunchProcess([string]$Name, [string]$Log, [string]$File, [string[]]$Arguments, [string]$WorkDir, [switch]$Window) {
    Write-Host "  啟動 $Name → $Log"
    $errLog = [System.IO.Path]::ChangeExtension($Log, '.err.log')
    Save-LaunchLogHistory $Log
    Save-LaunchLogHistory $errLog
    # 含空白的參數(例如 D:\Simosa BRM\app\...)必須加引號,Start-Process 只以空白串接 -ArgumentList
    $quoted = ($Arguments | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join ' '
    $sp = @{ FilePath = $File; ArgumentList = $quoted; WorkingDirectory = $WorkDir; PassThru = $true; RedirectStandardOutput = $Log; RedirectStandardError = $errLog }
    if ($script:OnWindows) { $sp.WindowStyle = $(if ($Window) { 'Normal' } else { 'Hidden' }) }
    $p = Start-Process @sp
    Add-Content -Path $script:PidFile -Value "${Name}:$($p.Id)" -Encoding UTF8
    Add-Content -Path (Join-Path (Split-Path -Parent $Log) 'launch.log') -Value ("# {0:u} {1} PID {2} {3} {4}" -f (Get-Date).ToUniversalTime(), $Name, $p.Id, $File, ($Arguments -join ' ')) -Encoding UTF8
    return $p
}

# Get-LaunchConsoleArgs <埠規格> <ws 埠> [鮑率]:USB 操控台服務(app\console;src\Contracts\console-serial.md)的參數。
#   規格為序列埠名稱(COM5)→ --port <埠> --baud <鮑率>;loopback:<讀取檔>[,<寫入檔>] → --loopback <…>(測試用假裝置);
#   log 由 Start-LaunchProcess 收 stdout(--log -),不另寫檔。與 launch-common.sh 的 console_service_args 相同規則。
function Get-LaunchConsoleArgs([string]$Spec, [int]$WsPort, [int]$Baud = 115200) {
    $a = if ($Spec -like 'loopback:*') { @('--loopback', $Spec.Substring(9)) } else { @('--port', $Spec, '--baud', "$Baud") }
    return $a + @('--ws', "ws://127.0.0.1:$WsPort", '--log', '-')
}

# Get-LaunchNmeaArgs $cfg:config ports.nmeaTargets[](可多個)/nmeaBind/nmeaFile → SimCore.Host 的 --nmea-target <ip:port>(可重複)、--nmea-bind <ip>、--nmea-file <路徑>;
#   $null/空字串 = 不加該參數(nmeaFile 相對路徑由 Host 以安裝根目錄解析且必須在根目錄內)。與 launch-common.sh 的 nmea_host_args 相同規則;
#   無參數時不輸出任何東西:呼叫端以 @(Get-LaunchNmeaArgs $cfg) 取陣列(直接 += 函式輸出會在空輸出時加入 $null 元素 → 空參數)
function Get-LaunchNmeaArgs($Config) {
    $a = @()
    foreach ($t in @(Get-Cfg $Config 'ports.nmeaTargets' @())) { if ($null -ne $t -and "$t" -ne '') { $a += @('--nmea-target', "$t") } }
    $bind = Get-Cfg $Config 'ports.nmeaBind'
    if ($bind) { $a += @('--nmea-bind', "$bind") }
    $file = Get-Cfg $Config 'ports.nmeaFile'
    if ($file) { $a += @('--nmea-file', "$file") }
    return $a
}

# Get-LaunchStopFile <根目錄> <logs>:SimCore.Host 的停止檔路徑(--stop-file;stop-all.ps1 先寫此檔讓核心自行結束並寫完紀錄 footer — Windows 的
# Stop-Process 不會送 Ctrl+C,主控台程序收不到訊號)。路徑記在 <logs>\simcore.stopfile 供 stop-all.ps1 讀取;殘留的停止檔先刪。
function Get-LaunchStopFile([string]$Root, [string]$Logs) {
    $dir = Join-Path $Root $Logs
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stop = Join-Path $dir 'simcore.stop'
    Remove-Item $stop -ErrorAction SilentlyContinue
    Set-Content -Path (Join-Path $dir 'simcore.stopfile') -Value $stop -Encoding UTF8
    return $stop
}

# Test-LaunchTcpOpen <主機> <埠> [毫秒]:在限時內連得上即為 true(連線包在 ConnectAsync().Wait 內:對不可達的 IP,例如船上筆電未接線,
# 同步的 Connect 會等到 TCP 逾時,不能讓啟動腳本卡住)
function Test-LaunchTcpOpen([string]$HostName, [int]$Port, [int]$TimeoutMs = 1000) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($HostName, $Port)
        return ($t.Wait($TimeoutMs) -and $c.Connected)
    } catch { return $false } finally { $c.Close() }
}

# Test-LaunchPortFree <主機> <埠>:埠沒有程序監聽即為 true(啟動前檢查殘留的舊核心/其他程式;否則 SimCore.Host 會立刻崩潰而 Wait-LaunchTcp 仍連到別的程序)
function Test-LaunchPortFree([string]$HostName, [int]$Port) { return -not (Test-LaunchTcpOpen $HostName $Port) }

# Assert-LaunchPortsFree @{ 'SimCore ws' = 8765; ... } [主機…]:任一埠(於 127.0.0.1 與指定主機)被占用即 throw
function Assert-LaunchPortsFree([hashtable]$Ports, [string[]]$Hosts = @()) {
    $busy = @()
    foreach ($name in $Ports.Keys) {
        foreach ($h in (@('127.0.0.1') + @($Hosts | Where-Object { $_ }) | Select-Object -Unique)) {
            if (-not (Test-LaunchPortFree $h $Ports[$name])) { $busy += "埠 $($Ports[$name]) 已被占用($name,$h)"; break }
        }
    }
    if ($busy) { throw (($busy -join ';') + ':先執行 stop-all.ps1,或查占用程序(netstat -ano | findstr :<埠>)') }
}

# Wait-LaunchTcp <主機> <埠> [秒] [-Process <程序>]:等待埠開啟;給 -Process 時程序已結束即立刻回傳 $false(不等到逾時,也不把別的程序的監聽當成自己的)
function Wait-LaunchTcp([string]$HostName, [int]$Port, [int]$Seconds = 20, [System.Diagnostics.Process]$Process = $null) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if ($null -ne $Process -and $Process.HasExited) { return $false }
        if (Test-LaunchTcpOpen $HostName $Port) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# ---- 發行版狀態(教官站頁首的「發行版 <版本>」與降版提醒;docs\design\packaging-sync.md 第 5.1 節)----
# Get-LaunchReleaseStatusPath <根目錄>:狀態檔 <根目錄>\build\run\release-status.json(執行期資料,不是程式檔;simosa-web --status-file 讀它,
#   以 /_status/release.json 提供教官站需要的欄位)。與 launch-common.sh 的 release_status_path 相同。
function Get-LaunchReleaseStatusPath([string]$Root) {
    return (Join-Path (Join-Path (Join-Path $Root 'build') 'run') 'release-status.json')
}

# Find-LaunchReleaseStatusTool <根目錄>:安裝版(根目錄有 manifest.json)才回傳 simosa-pkg.dll 的路徑(tools\updater;沒有時用已建置的開發輸出),
#   否則 $null。不建置:開發模式(儲存庫,沒有 manifest.json)沒有發行版本可顯示,也不為了它建置 Tools.Packaging.Cli。
#   與啟動同步服務無關(-NoSync 或 sync.enabled=false 時照樣取狀態)。
function Find-LaunchReleaseStatusTool([string]$Root) {
    if (-not (Test-Path (Join-Path $Root 'manifest.json') -PathType Leaf)) { return $null }
    $bin = Join-Path (Join-Path (Join-Path $Root 'build') 'bin') 'Tools.Packaging.Cli'
    foreach ($c in @((Join-Path (Join-Path (Join-Path $Root 'tools') 'updater') 'simosa-pkg.dll'),
                     (Join-Path (Join-Path (Join-Path $bin 'Release') 'net8.0') 'simosa-pkg.dll'),
                     (Join-Path (Join-Path (Join-Path $bin 'Debug') 'net8.0') 'simosa-pkg.dll'))) {
        if (Test-Path $c -PathType Leaf) { return $c }
    }
    return $null
}

# Write-LaunchReleaseStatus <根目錄> <log 資料夾> [-TimeoutSeconds 20] [-PkgDll <simosa-pkg.dll>]:啟動網頁伺服之前執行一次
#   simosa-pkg status --json,輸出存成 Get-LaunchReleaseStatusPath。回傳狀態檔路徑;沒有產生時 $null。
#   - 先刪掉上一次的狀態檔:這次沒有產生(開發模式、找不到 simosa-pkg、逾時、失敗、輸出不是 JSON)時教官站就不顯示,不會顯示過期的版本。
#   - 逾時與失敗都不擋住啟動、不丟例外:只警告並在 <log 資料夾>\launch.log 記一行(stderr 在 release-status.err.log)。
#   - 存的是 status 的完整輸出(與在本機下指令看到的相同,留在本機);simosa-web 只把發行版本與降版資訊送給網頁,不會整份送出。
#   - 以 Start-Process 把輸出導到檔案(不用 & dotnet … 擷取):Windows PowerShell 5.1 擷取原生程式的輸出時以主控台字碼頁解碼,
#     simosa-pkg 輸出的 UTF-8 中文(降版說明)會變成亂碼;導到檔案是原樣的位元組。
function Write-LaunchReleaseStatus([string]$Root, [string]$LogDir, [int]$TimeoutSeconds = 20, [string]$PkgDll = '') {
    $out = Get-LaunchReleaseStatusPath $Root
    $tmp = $out + '.tmp'
    $note = { param([string]$Message)
        try { Add-Content -Path (Join-Path $LogDir 'launch.log') -Value ("# {0:u} release-status {1}" -f (Get-Date).ToUniversalTime(), $Message) -Encoding UTF8 } catch { } }
    try {
        Remove-Item -LiteralPath $out, $tmp -Force -ErrorAction SilentlyContinue
        if (-not $PkgDll) { $PkgDll = Find-LaunchReleaseStatusTool $Root }
        if (-not $PkgDll) {
            if (Test-Path (Join-Path $Root 'manifest.json') -PathType Leaf) { Write-Warning '發行版狀態:找不到 tools\updater\simosa-pkg.dll,教官站不顯示發行版本' }
            return $null
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out), $LogDir | Out-Null
        $err = Join-Path $LogDir 'release-status.err.log'
        # 含空白的路徑(D:\Simosa BRM\…)加引號:Start-Process 只以空白串接 -ArgumentList(與 Start-LaunchProcess 相同)
        $quoted = (@($PkgDll, 'status', '--root', $Root, '--json') | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join ' '
        $sp = @{ FilePath = 'dotnet'; ArgumentList = $quoted; WorkingDirectory = $Root; PassThru = $true; RedirectStandardOutput = $tmp; RedirectStandardError = $err }
        if ($script:OnWindows) { $sp.WindowStyle = 'Hidden' }
        $p = Start-Process @sp
        $null = $p.Handle   # Windows PowerShell 5.1:先取 Handle,結束後才讀得到 ExitCode
        if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
            try { $p.Kill() } catch { }
            Write-Warning "發行版狀態:simosa-pkg status 超過 $TimeoutSeconds 秒沒有結束,略過(教官站不顯示發行版本)"
            & $note "逾時($TimeoutSeconds 秒),略過"
            return $null
        }
        # 結束碼讀不到($null;Windows PowerShell 5.1 的 Start-Process 偶有)時不當成失敗:下面仍會確認輸出是 JSON 物件
        if ($null -ne $p.ExitCode -and $p.ExitCode -ne 0) {
            Write-Warning "發行版狀態:simosa-pkg status 結束碼 $($p.ExitCode),略過(教官站不顯示發行版本;見 $err)"
            & $note "simosa-pkg status 結束碼 $($p.ExitCode),略過"
            return $null
        }
        # 確認輸出是 JSON 物件(不是的話不留檔);版本只用來在啟動視窗與 launch.log 記一行
        $st = [System.IO.File]::ReadAllText($tmp, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
        if ($null -eq $st -or $st -isnot [System.Management.Automation.PSCustomObject]) { throw 'simosa-pkg status 的輸出不是 JSON 物件' }
        Move-Item -LiteralPath $tmp -Destination $out -Force
        $ver = Get-Cfg $st 'releaseVersion'
        $summary = if ($ver) { "發行版 $ver" } else { 'status 沒有發行版本(manifest.json 讀不到,或 simosa-pkg 是較舊的版本),教官站不顯示' }
        $basis = Get-Cfg $st 'releaseDowngrade.basisVersion'
        if ($basis) { $summary += "(降版提醒:曾安裝或套用過 ${basis};教官站頁首會顯示)" }
        Write-Host "  發行版狀態:$summary → $out"
        & $note $summary
        return $out
    } catch {
        Write-Warning "發行版狀態:無法取得($($_.Exception.Message)),略過(教官站不顯示發行版本)"
        & $note "無法取得:$($_.Exception.Message)"
        return $null
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}

# ---- 船上筆電的網頁位址(第三十一輪;教官筆電的教官站向船上筆電的 simosa-web 取 /_status/release.json,頁首顯示船上的版本與降版提醒;
#      與 launch-common.sh 的 ship_web_url / ship_web_page 相同規則)----
# Get-LaunchShipWebUrl <是否 standalone> <船上 IP> <船上網頁埠>:直連模式而且有設船上網頁埠(config\instructor-laptop.json 的 ports.shipWeb;1–65535)
#   時回傳 http://<IP>:<埠>,否則空字串。沒設時不必帶參數:教官站自己由 ws 參數的主機推得 http://<主機>:8080(船上 simosa-web 的預設埠);
#   standalone(核心在本機)沒有另一台電腦,不取。
#   埠只接受整數或純數字的字串(JSON 的 8081、"8081");8081.0(JSON 的小數)、陣列、物件、true / false 都不是埠 —— 與 launch-common.sh 相同
#   (不做型別檢查的話 "$Port" 會把 8081.0 與 [8081] 都變成「8081」而被接受)。
function Get-LaunchShipWebUrl([bool]$Standalone, [string]$ShipIp, $Port) {
    if ($Standalone -or -not $ShipIp) { return '' }
    if (-not ($Port -is [int] -or $Port -is [long] -or $Port -is [string])) { return '' }
    $text = "$Port"
    if ($text -notmatch '^[1-9][0-9]{0,4}$' -or [int]$text -gt 65535) { return '' }
    return "http://${ShipIp}:${text}"
}

# Get-LaunchShipWebPort <設定>:config\instructor-laptop.json 的 ports.shipWeb → @{ Port = <1–65535 的整數;沒設或不合格時 0>; Invalid = <不合格時原值的文字,否則 ''> }。
#   沒有這個鍵(舊的設定檔)、null、空字串 = 沒設(Port 0、Invalid '')。直接讀屬性而不經 Get-Cfg:函式的回傳值會把只有一個元素的陣列
#   展開成那個元素([8081] 會變成 8081 而被當成合格的埠)。Invalid 的文字與 start-instructor-laptop.sh 的警告相同:字串原樣、陣列或物件寫「陣列或物件」、
#   其餘(小數、true / false)是 JSON 的寫法。
function Get-LaunchShipWebPort($Config) {
    $value = $null
    $ports = $null
    if ($null -ne $Config) { $portsProp = $Config.PSObject.Properties['ports']; if ($null -ne $portsProp) { $ports = $portsProp.Value } }
    if ($null -ne $ports) { $prop = $ports.PSObject.Properties['shipWeb']; if ($null -ne $prop) { $value = $prop.Value } }
    if ($null -eq $value -or ($value -is [string] -and $value -eq '')) { return @{ Port = 0; Invalid = '' } }
    if (($value -is [int] -or $value -is [long] -or $value -is [string]) -and "$value" -match '^[1-9][0-9]{0,4}$' -and [int]"$value" -le 65535) { return @{ Port = [int]"$value"; Invalid = '' } }
    $text = '陣列或物件'
    if ($value -is [string]) { $text = $value }
    elseif (-not ($value -is [array] -or $value -is [System.Management.Automation.PSCustomObject] -or $value -is [System.Collections.IDictionary])) { $text = (ConvertTo-Json $value -Compress) }
    return @{ Port = 0; Invalid = $text }
}

# Add-LaunchShipWebParam <頁面> <船上網頁位址>:教官站頁面(instructor.html、instructor.html?… 或 instructor.html#…)而且查詢字串還沒有 shipWeb 參數時,
#   在查詢字串加上 shipWeb=<位址>(既有的 openPages 設定不必改);其他頁面、位址是空的、已經寫了 shipWeb= 的原樣回傳。區分大小寫(網頁端的參數名稱也是)。
#   頁面帶 # 片段時加在片段**之前**(加在後面的話參數落在片段裡,網頁讀不到,會改用預設的 8080);片段裡的 shipWeb= 不算已經寫了。
function Add-LaunchShipWebParam([string]$Page, [string]$ShipWebUrl) {
    $hash = $Page.IndexOf('#')
    $base = $Page
    $frag = ''
    if ($hash -ge 0) { $base = $Page.Substring(0, $hash); $frag = $Page.Substring($hash) }
    if (-not $ShipWebUrl -or $base -cnotmatch '^instructor\.html(\?|$)' -or $base -cmatch '[?&]shipWeb=') { return $Page }
    $sep = if ($base.Contains('?')) { '&' } else { '?' }
    return "${base}${sep}shipWeb=${ShipWebUrl}${frag}"
}

# 設定直連介面固定 IP(需系統管理員;僅在介面存在且 IP 不同時變更)
function Set-DirectLinkIp([string]$Adapter, [string]$Ip, [string]$Mask) {
    if (-not $Adapter -or -not $Ip) { return }
    $nic = Get-NetAdapter -Name $Adapter -ErrorAction SilentlyContinue
    if ($null -eq $nic) { Write-Warning "找不到網路介面「$Adapter」;請依 docs\ops\安裝與啟動.md 設定固定 IP $Ip"; return }
    $cur = Get-NetIPAddress -InterfaceIndex $nic.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -eq $Ip }
    if ($null -eq $cur) {
        $prefix = 24
        if ($Mask) { $prefix = ([Convert]::ToString(([IPAddress]$Mask).Address, 2) -replace '0', '').Length }
        Write-Host "  設定 $Adapter 固定 IP $Ip/$prefix"
        try { New-NetIPAddress -InterfaceIndex $nic.ifIndex -IPAddress $Ip -PrefixLength $prefix -ErrorAction Stop | Out-Null }
        catch { Write-Warning "無法設定固定 IP(需系統管理員):$($_.Exception.Message)" }
    }
}

# 瀏覽器端資料(localStorage / IndexedDB:情境草稿、參與者與休息時間資料、講評報告站的訓練紀錄、ECDIS 與顯示設定)存在瀏覽器的使用者設定檔內;
# 預設瀏覽器的設定檔在 C:\Users\…\AppData。為讓這些資料留在安裝根目錄內,啟動腳本以專屬設定檔啟動 Microsoft Edge
#(--user-data-dir=<根目錄>\build\browser\edge;WebView2 外殼則在 build\webview2\);網頁下載的檔案兩者都存到 build\downloads\。
function Get-LaunchBrowserProfile([string]$Root) {
    return (Join-Path (Join-Path (Join-Path $Root 'build') 'browser') 'edge')
}

# 專屬 Edge 設定檔第一次建立時預先寫入下載位置(<根目錄>\build\downloads;與 WebView2 外殼相同):Edge 預設把「下載 YAML / 紀錄 JSON / 報告」
# 存到 C:\Users\…\Downloads。Chromium 啟動時讀取既有的 Default\Preferences 並補上其餘預設值。
# 設定檔已存在時:下載位置(download.default_directory、savefile.default_directory)若不在目前根目錄內 — 專案資料夾換了磁碟代號或整個複製到
# 另一個磁碟(D: → G:)後仍指向舊路徑,舊磁碟不在時 Chromium 會退回 C:\Users 的下載/文件資料夾 — 就改寫成目前的 build\downloads;
# 該設定檔的 Edge 還開著(lockfile 被占用)時不改檔,只警告。使用者自行改到根目錄內其他資料夾者不動。
# Preferences 必須是沒有 BOM 的 UTF-8(有 BOM 會被視為損毀而重設);只以文字取代這兩個值,不重寫整份 JSON
#(Windows PowerShell 5.1 的 ConvertFrom-Json 無法處理 Edge 設定檔內的空白鍵名與僅大小寫不同的鍵名)。
function Initialize-LaunchBrowserProfile([string]$ProfileDir) {
    $buildDir = Split-Path -Parent (Split-Path -Parent $ProfileDir)
    $rootDir = Split-Path -Parent $buildDir
    $downloads = Join-Path $buildDir 'downloads'
    $default = Join-Path $ProfileDir 'Default'
    New-Item -ItemType Directory -Force -Path $default, $downloads | Out-Null
    $prefs = Join-Path $default 'Preferences'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    if (-not (Test-Path $prefs)) {
        $json = @{
            download = @{ default_directory = $downloads; prompt_for_download = $false; directory_upgrade = $true }
            savefile = @{ default_directory = $downloads }
        } | ConvertTo-Json -Depth 4 -Compress
        [System.IO.File]::WriteAllText($prefs, $json, $utf8)
        Write-Host "  第一次使用專屬 Edge 設定檔($ProfileDir):先前用其他瀏覽器存的草稿、參與者資料與訓練紀錄不會自動帶過來,搬移步驟見 docs\ops\本機資料位置.md 第 5 節"
        return
    }
    try {
        $text = [System.IO.File]::ReadAllText($prefs, $utf8)
        $pattern = '("default_directory"\s*:\s*")((?:[^"\\]|\\.)*)(")'
        $stale = @()
        foreach ($m in [regex]::Matches($text, $pattern)) {
            $cur = $m.Groups[2].Value.Replace('\\', '\')
            if (-not (Test-LaunchUnderRoot $cur $rootDir)) { $stale += $cur }
        }
        if ($stale.Count -eq 0) { return }
        $lock = Join-Path $ProfileDir 'lockfile'
        if (Test-Path $lock) {
            try { $fs = [System.IO.File]::Open($lock, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None); $fs.Close() }
            catch { Write-Warning "專屬 Edge 設定檔的下載位置仍指向 $($stale[0])(不在 $rootDir 內),但該設定檔的 Edge 還開著,無法修正:請先關閉 Simosa BRM 的 Edge 視窗(stop-all.ps1)再啟動,或在 Edge 設定 → 下載 改為 $downloads"; return }
        }
        $escaped = $downloads.Replace('\', '\\')
        $fixed = [regex]::Replace($text, $pattern, { param($m)
                $cur = $m.Groups[2].Value.Replace('\\', '\')
                if (Test-LaunchUnderRoot $cur $rootDir) { $m.Value } else { $m.Groups[1].Value + $escaped + $m.Groups[3].Value } })
        [System.IO.File]::WriteAllText($prefs, $fixed, $utf8)
        Write-Host "  專屬 Edge 設定檔的下載位置由 $($stale[0]) 改為 $downloads(專案資料夾換了位置)"
    } catch { Write-Warning "無法檢查專屬 Edge 設定檔的下載位置($($_.Exception.Message));請在 Edge 設定 → 下載 確認為 $downloads" }
}

# 找 Microsoft Edge(Windows 10/11 內建);找不到回 $null。非 Windows(測試用的 pwsh)一律 $null。
function Find-LaunchEdge {
    if (-not $script:OnWindows) { return $null }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, $env:LOCALAPPDATA)) {
        if (-not $base) { continue }
        $p = Join-Path $base 'Microsoft\Edge\Application\msedge.exe'
        if (Test-Path $p) { return $p }
    }
    return $null
}

# 組出以專屬設定檔開啟網址的 Edge 參數(含空白的路徑與網址加引號:Start-Process 只以空白串接 -ArgumentList)
function Get-LaunchEdgeArguments([string]$ProfileDir, [string]$Url) {
    return @("--user-data-dir=`"$ProfileDir`"", '--no-first-run', '--no-default-browser-check', "`"$Url`"")
}

# 開啟網頁。$ProfileDir 有值且找得到 Edge → 以該設定檔啟動 Edge(瀏覽器端資料留在安裝根目錄內);
# $ProfileDir 為空(啟動腳本 -DefaultBrowser)→ Windows 預設瀏覽器(資料在該瀏覽器位於 C:\Users 的設定檔,需經同意才使用);
# $ProfileDir 有值但找不到 Edge → 不自動退回預設瀏覽器(那會把草稿、參與者資料、訓練紀錄與下載檔存到 C:\Users,守則要求先經同意):
#   列出網址與處理方式後略過開啟。
function Open-LaunchPages([string]$BaseUrl, $Pages, [hashtable]$Subst, [string]$ProfileDir = '') {
    $urls = @()
    foreach ($p in @($Pages)) {
        $u = [string]$p
        foreach ($k in $Subst.Keys) { $u = $u.Replace('{' + $k + '}', [string]$Subst[$k]) }
        $urls += "$BaseUrl/$u"
    }
    $edge = $null
    if ($ProfileDir) {
        $edge = Find-LaunchEdge
        if (-not $edge) {
            Write-Warning ('找不到 Microsoft Edge,未開啟網頁。用預設瀏覽器開啟會把瀏覽器端資料(草稿、參與者資料、訓練紀錄)與下載的檔案存到 C:\Users,' +
                '須先經訓練負責人同意:同意後加 -DefaultBrowser 重新啟動;或改用 -Shell(WebView2 外殼,資料在 build\webview2)。網址:' + ($urls -join '、'))
            return
        }
        Initialize-LaunchBrowserProfile $ProfileDir
    }
    foreach ($url in $urls) {
        Write-Host "  開啟 $url"
        try {
            if ($edge) { Start-Process -FilePath $edge -ArgumentList (Get-LaunchEdgeArguments $ProfileDir $url) }
            else { Start-Process $url }
        } catch { Write-Warning "無法開啟瀏覽器:$url" }
    }
}

# 找 WebView2 操船台外殼(發行版 app\shell\SimosaBRM.Shell.exe → 開發建置 build\bin\Shell.WebView2\Release|Debug\net8.0-windows\[win-x64\]);找不到回 $null
function Find-LaunchShellExe([string]$Root) {
    $candidates = @(
        (Join-Path $Root 'app\shell\SimosaBRM.Shell.exe'),
        (Join-Path $Root 'build\bin\Shell.WebView2\Release\net8.0-windows\win-x64\SimosaBRM.Shell.exe'),
        (Join-Path $Root 'build\bin\Shell.WebView2\Release\net8.0-windows\SimosaBRM.Shell.exe'),
        (Join-Path $Root 'build\bin\Shell.WebView2\Debug\net8.0-windows\win-x64\SimosaBRM.Shell.exe'),
        (Join-Path $Root 'build\bin\Shell.WebView2\Debug\net8.0-windows\SimosaBRM.Shell.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

# 以 WebView2 外殼開啟網頁(每頁一個全螢幕 kiosk 程序;PID 記入 simosa.pids,stop-all.ps1 一併結束;docs\ops\安裝與啟動.md 第 4.5 節)。
# $Monitors:與 $Pages 同序的螢幕索引(0 起算;省略或 $null 時由外殼用主螢幕);找不到外殼 exe 時退回瀏覽器($BrowserProfile 同 Open-LaunchPages 的 $ProfileDir)。
# 程序名稱 shell-<頁面主檔名>(index.html?station=bridge → shell-index;同主檔名的第 2 頁起加 -2、-3…,避免兩頁共用同一 log 與 PID 名稱)。
function Start-LaunchShellPages([string]$Exe, [string]$BaseUrl, $Pages, [hashtable]$Subst, [string]$WorkDir, [string]$LogDir, $Monitors = $null, [string]$BrowserProfile = '') {
    if (-not $Exe -or -not (Test-Path $Exe)) {
        Write-Warning '找不到 WebView2 外殼(app\shell\SimosaBRM.Shell.exe 或 build\bin\Shell.WebView2\…),改用瀏覽器'
        Open-LaunchPages $BaseUrl $Pages $Subst $BrowserProfile
        return
    }
    $i = 0
    $used = @{}
    foreach ($p in @($Pages)) {
        $u = [string]$p
        foreach ($k in $Subst.Keys) { $u = $u.Replace('{' + $k + '}', [string]$Subst[$k]) }
        $url = "$BaseUrl/$u"
        $name = 'shell-' + ($u -replace '\.html.*$', '' -replace '[^A-Za-z0-9_-]', '_')
        if ($name -eq 'shell-') { $name = "shell-$i" }
        if ($used.ContainsKey($name)) { $used[$name]++; $name = "$name-$($used[$name])" } else { $used[$name] = 1 }
        # --no-close-confirm:stop-all.ps1 以 CloseMainWindow 結束外殼時不彈確認對話框(否則會等到逾時被強制結束)
        $shellArgs = @('--url', $url, '--kiosk', '--no-close-confirm')
        if ($null -ne $Monitors -and $i -lt @($Monitors).Count -and $null -ne @($Monitors)[$i]) { $shellArgs += @('--monitor', "$(@($Monitors)[$i])") }
        Write-Host "  外殼 $url"
        Start-LaunchProcess -Name $name -Log (Join-Path $LogDir "$name.log") -File $Exe -Arguments $shellArgs -WorkDir $WorkDir -Window | Out-Null
        $i++
    }
}

# 版本資訊(soak 報告的 meta.json;第三十輪審查 S-F4):Rev = HEAD 的提交(可能為 $null)、Dirty = $true(工作樹有未提交的修改,含未追蹤檔)/ $false / $null(無法得知)。
# 有 git 時以 git 取(git worktree 的 .git 是檔案也可);沒有 git(船上筆電)或 -NoGit(測試用)時讀 .git\HEAD 與 refs
# (.git 是檔案時跟到 gitdir、worktree 的 commondir、packed-refs),Dirty 為 $null。不拋例外。與 launch-common.sh 的 launch_git_info 相同。
function Get-LaunchGitInfo([string]$Root, [switch]$NoGit) {
    if (-not $NoGit -and (Get-Command git -ErrorAction SilentlyContinue)) {
        # try:Windows PowerShell 5.1 在 $ErrorActionPreference = 'Stop' 下,原生程式寫 stderr(不是 git 儲存庫)即使 2>$null 也會變成終止錯誤
        try {
            $rev = (& git -C $Root rev-parse HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $rev) {
                $status = @(& git -C $Root status --porcelain 2>$null)
                return [pscustomobject]@{ Rev = "$rev".Trim(); Dirty = [bool]($LASTEXITCODE -eq 0 -and $status.Count -gt 0) }
            }
        } catch { }
    }
    $info = [pscustomobject]@{ Rev = $null; Dirty = $null }
    try {
        $gd = Join-Path $Root '.git'
        if (Test-Path $gd -PathType Leaf) {
            $line = @(Get-Content $gd | Where-Object { $_ -like 'gitdir: *' })[0]
            if (-not $line) { return $info }
            $gd = $line.Substring(8).Trim()
            if (-not [System.IO.Path]::IsPathRooted($gd)) { $gd = Join-Path $Root $gd }
        }
        $headFile = Join-Path $gd 'HEAD'
        if (-not (Test-Path $headFile -PathType Leaf)) { return $info }
        $head = (Get-Content $headFile -TotalCount 1).Trim()
        if (-not $head.StartsWith('ref: ')) { $info.Rev = $head; return $info }
        $ref = $head.Substring(5).Trim()
        $common = $gd
        $commonFile = Join-Path $gd 'commondir'
        if (Test-Path $commonFile -PathType Leaf) {
            $common = (Get-Content $commonFile -TotalCount 1).Trim()
            if (-not [System.IO.Path]::IsPathRooted($common)) { $common = Join-Path $gd $common }
        }
        foreach ($dir in @($gd, $common)) {
            $refFile = Join-Path $dir ($ref -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if (Test-Path $refFile -PathType Leaf) { $info.Rev = (Get-Content $refFile -TotalCount 1).Trim(); return $info }
        }
        $packed = Join-Path $common 'packed-refs'
        if (Test-Path $packed -PathType Leaf) {
            foreach ($l in Get-Content $packed) { $p = $l -split ' '; if ($p.Count -ge 2 -and $p[1] -eq $ref) { $info.Rev = $p[0]; break } }
        }
    } catch { }
    return $info
}
