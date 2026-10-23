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
            & dotnet build $csproj -c Release -nologo -v q
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
        & (Join-Path $script:LaunchDir 'stop-all.ps1') -Logs $Logs
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

# 開啟網頁(預設瀏覽器;Windows 11 IoT LTSC 內建 Edge/WebView2)
function Open-LaunchPages([string]$BaseUrl, $Pages, [hashtable]$Subst) {
    foreach ($p in @($Pages)) {
        $u = [string]$p
        foreach ($k in $Subst.Keys) { $u = $u.Replace('{' + $k + '}', [string]$Subst[$k]) }
        $url = "$BaseUrl/$u"
        Write-Host "  開啟 $url"
        try { Start-Process $url } catch { Write-Warning "無法開啟瀏覽器:$url" }
    }
}
