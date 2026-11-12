<#
.SYNOPSIS
  本機資料位置檢查:確認資料都在專案(安裝)根目錄所在的 D: 或 G:,沒有落到 C:。開發機與安裝好的船上 / 教官筆電都可執行。
.DESCRIPTION
  守則(CLAUDE.md、docs\ops\本機資料位置.md):本機資料一律放 D: 或 G:;C: 只放應用程式本身;
  放在隨身碟會嚴重拖慢進度的項目,須先取得訓練負責人同意才可放 C:。
  本腳本只讀取、不修改任何東西:
    1) 根目錄所在磁碟:是否為系統磁碟、檔案系統(開發機須為 NTFS:exFAT / FAT32 上 pnpm install 會失敗)、是否為抽取式;
    2) 目前工作階段的環境變數是否都指到專案內(快取、暫存;只有開發機檢查);
    3) 各工具「預設」會寫到 C:\Users\… 的位置是否存在、大小、最近寫入時間(-Since 之後有寫入者標為「有新資料」)。
  第 3 項的資料夾可能是其他專案或安裝本軟體之前留下的;以「最近寫入時間」判斷是否為本專案造成。
  模式:根目錄有 src\ → 開發機(檢查全部);沒有 → 安裝版(-Runtime:只檢查執行期會寫的位置,不檢查開發工具的環境變數)。
  位置:儲存庫 tools\check-local-data.ps1;發行版 app\launch\check-local-data.ps1。
.PARAMETER Since
  只把這個時間之後有寫入的 C: 位置視為問題(預設:24 小時前)。例如開始工作前記下時間,收工時以 -Since 該時間執行。
.PARAMETER Json
  以 JSON 輸出(供自動化使用)。
.PARAMETER Runtime
  強制以安裝版模式檢查(預設依根目錄有無 src\ 自動判斷)。
.EXAMPLE
  .\tools\check-local-data.ps1
  .\tools\check-local-data.ps1 -Since '2026-11-12 09:00'
  結束碼:0 = 沒有發現問題;1 = 有項目需要處理(環境變數未指到專案內、C: 上有新資料、或開發機的專案磁碟不是 NTFS);2 = 根目錄位於系統磁碟。
#>
[CmdletBinding()]
param([datetime]$Since = (Get-Date).AddHours(-24), [switch]$Json, [switch]$Runtime)
$ErrorActionPreference = 'Stop'
# 根目錄:本檔往上找第一個含 data\ 的資料夾(儲存庫:tools\..;發行版:app\launch\..\..)
$root = $null
$dir = $PSScriptRoot
for ($i = 0; $i -lt 3 -and $dir; $i++) {
    $dir = Split-Path -Parent $dir
    if ($dir -and (Test-Path (Join-Path $dir 'data'))) { $root = $dir; break }
}
if (-not $root) { throw "找不到根目錄(本檔上層應有含 data\ 的資料夾):$PSScriptRoot" }
$onWindows = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)
$devMode = (-not $Runtime) -and (Test-Path (Join-Path $root 'src'))
$sysDrive = $env:SystemDrive
$onSystemDrive = [bool]($sysDrive -and $root.StartsWith($sysDrive.TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase))
$tempConsent = ($env:SIMOSA_SYSTEM_TEMP -eq '1')

function Test-UnderRoot([string]$Path) {
    if (-not $Path) { return $false }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $false }
    $r = $root.TrimEnd('\', '/')
    return ($full.Equals($r, [System.StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($r + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase))
}

# ---- 1) 根目錄所在磁碟 ----
$fileSystem = $null; $driveType = $null
if ($onWindows) {
    try { $di = New-Object System.IO.DriveInfo((Split-Path -Qualifier $root)); $fileSystem = $di.DriveFormat; $driveType = "$($di.DriveType)" } catch { }
}
$fsBad = [bool]($devMode -and $fileSystem -and (@('NTFS', 'ReFS') -notcontains $fileSystem))

# ---- 2) 環境變數:應指到專案內(tools\setup-windows.ps1 設定;TEMP/TMP 只在執行過 setup-windows.ps1 的工作階段內生效)。只有開發機檢查 ----
#   SIMOSA_BRM_ROOT 可以不設(程式會自行找根目錄);有設就必須是本根目錄。
$envNames = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH', 'DOTNET_CLI_HOME', 'PNPM_HOME',
              'npm_config_cache', 'NODE_COMPILE_CACHE', 'PWTEST_CACHE_DIR', 'UV_CACHE_DIR', 'UV_PYTHON_INSTALL_DIR', 'UV_TOOL_DIR', 'UV_TOOL_BIN_DIR', 'UV_PYTHON_BIN_DIR',
              'PLAYWRIGHT_BROWSERS_PATH', 'SIMOSA_BRM_ROOT', 'TEMP', 'TMP')
$envRows = @()
if ($devMode) {
    foreach ($n in $envNames) {
        $v = [Environment]::GetEnvironmentVariable($n)
        $ok = Test-UnderRoot $v
        $note = ''
        if ($n -eq 'SIMOSA_BRM_ROOT' -and -not $v) { $ok = $true; $note = '未設定(程式會自行找根目錄)' }
        if (($n -eq 'TEMP' -or $n -eq 'TMP') -and -not $ok -and $tempConsent) { $ok = $true; $note = '已同意的例外(SIMOSA_SYSTEM_TEMP=1)' }
        $envRows += [pscustomobject]@{ name = $n; value = $v; insideProject = $ok; note = $note }
    }
}

# ---- 3) 各工具預設寫到使用者設定檔(C:\Users\…)的位置 ----
# 以 Windows 的已知資料夾 API 取得真正的位置:同一個終端機執行過 visual-run.ps1 等腳本後,$env:APPDATA / $env:LOCALAPPDATA 可能已被改指到專案內。
if ($onWindows) {
    $userProfile = [Environment]::GetFolderPath('UserProfile')
    $roaming = [Environment]::GetFolderPath('ApplicationData')
    $local = [Environment]::GetFolderPath('LocalApplicationData')
} else {
    # 非 Windows(測試用):沿用環境變數
    $userProfile = $env:USERPROFILE; $roaming = $env:APPDATA; $local = $env:LOCALAPPDATA
}
$sysTemp = if ($local) { Join-Path $local 'Temp' } else { $null }
function Join-Base([string]$Base, [string]$Rel) { if ($Base) { return (Join-Path $Base $Rel) } else { return $null } }
# dev = $true:只有開發機會寫;$false:安裝版(執行期)也可能寫。path 可含萬用字元(* 只用在最後一段)。
$candidates = @(
    @{ dev = $false; what = '視景使用者資料(Godot user://:log、著色器快取)'; path = (Join-Base $roaming 'SimosaBRM-Visual') },
    @{ dev = $true;  what = 'NuGet 套件(預設位置)';            path = (Join-Base $userProfile '.nuget\packages') },
    @{ dev = $true;  what = 'NuGet HTTP 快取';                    path = (Join-Base $local 'NuGet\v3-cache') },
    @{ dev = $true;  what = 'NuGet 外掛快取';                     path = (Join-Base $local 'NuGet\plugins-cache') },
    @{ dev = $true;  what = '.NET CLI 家目錄';                    path = (Join-Base $userProfile '.dotnet') },
    @{ dev = $true;  what = 'pnpm 套件庫';                        path = (Join-Base $local 'pnpm') },
    @{ dev = $true;  what = 'pnpm 快取';                          path = (Join-Base $local 'pnpm-cache') },
    @{ dev = $true;  what = 'pnpm 狀態';                          path = (Join-Base $local 'pnpm-state') },
    @{ dev = $true;  what = 'npm 快取';                           path = (Join-Base $local 'npm-cache') },
    @{ dev = $true;  what = 'uv 快取';                            path = (Join-Base $local 'uv') },
    @{ dev = $true;  what = 'uv 受管 Python / 工具';              path = (Join-Base $roaming 'uv') },
    @{ dev = $true;  what = 'Playwright 瀏覽器';                  path = (Join-Base $local 'ms-playwright') },
    @{ dev = $true;  what = 'matplotlib 設定與字型快取';          path = (Join-Base $local 'matplotlib') },
    @{ dev = $true;  what = 'matplotlib 設定(舊位置)';          path = (Join-Base $userProfile '.matplotlib') },
    @{ dev = $true;  what = 'Godot 編輯器設定';                   path = (Join-Base $roaming 'Godot') },
    @{ dev = $true;  what = 'Godot 編輯器快取';                   path = (Join-Base $local 'Godot') },
    @{ dev = $true;  temp = $true; what = '系統暫存:MSBuild';           path = (Join-Base $sysTemp 'MSBuildTemp*') },
    @{ dev = $true;  temp = $true; what = '系統暫存:NuGet 還原';        path = (Join-Base $sysTemp 'NuGetScratch*') },
    @{ dev = $true;  temp = $true; what = '系統暫存:Node 編譯快取';     path = (Join-Base $sysTemp 'node-compile-cache') },
    @{ dev = $true;  temp = $true; what = '系統暫存:Playwright 轉譯';   path = (Join-Base $sysTemp 'playwright-transform-cache*') }
)
$leakRows = @()
foreach ($c in $candidates) {
    if ($c.dev -and -not $devMode) { continue }
    $p = $c.path
    $isTemp = [bool]$c['temp']
    $row = [pscustomobject]@{ what = $c.what; path = $p; exists = $false; sizeMb = 0.0; lastWrite = $null; newData = $false; consented = ($isTemp -and $tempConsent) }
    $found = @()
    if ($p) { $found = @(Get-Item -Path $p -Force -ErrorAction SilentlyContinue | Where-Object { -not (Test-UnderRoot $_.FullName) }) }
    if ($found.Count -gt 0) {
        $row.exists = $true
        $files = @($found | ForEach-Object { if ($_.PSIsContainer) { Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Force -ErrorAction SilentlyContinue } else { $_ } })
        if ($files.Count -gt 0) {
            $row.sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
            # 解壓縮出來的檔案常保留封存內的舊修改時間:取「建立時間」與「修改時間」較晚者(Windows 的建立時間是在這顆磁碟上建立的時間)
            $latest = [datetime]::MinValue
            foreach ($f in $files) {
                if ($f.LastWriteTime -gt $latest) { $latest = $f.LastWriteTime }
                if ($f.CreationTime -gt $latest) { $latest = $f.CreationTime }
            }
            $row.lastWrite = $latest
            $row.newData = ($latest -gt $Since)
        }
    }
    $leakRows += $row
}

# 專案內的暫存與瀏覽器端資料(僅供參考:存在與大小)
function Get-DirSizeMb([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return 0.0 }
    return [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
}
$insideRows = @()
foreach ($rel in @('build\tmp', 'build\browser', 'build\webview2', 'build\downloads', '.cache\godot\appdata')) {
    $p = $root; foreach ($seg in $rel.Split('\')) { $p = Join-Path $p $seg }
    $insideRows += [pscustomobject]@{ path = $rel; sizeMb = (Get-DirSizeMb $p) }
}

$envBad = @($envRows | Where-Object { -not $_.insideProject })
$leakBad = @($leakRows | Where-Object { $_.newData -and -not $_.consented })
$exit = 0
if ($envBad.Count -gt 0 -or $leakBad.Count -gt 0 -or $fsBad) { $exit = 1 }
if ($onSystemDrive) { $exit = 2 }

if ($Json) {
    [pscustomobject]@{ root = $root; mode = $(if ($devMode) { 'development' } else { 'runtime' }); systemDrive = $sysDrive; rootOnSystemDrive = $onSystemDrive
        fileSystem = $fileSystem; driveType = $driveType; fileSystemOk = (-not $fsBad); since = $Since; environment = $envRows; defaultLocations = $leakRows
        insideProject = $insideRows; exitCode = $exit } | ConvertTo-Json -Depth 4
    exit $exit
}

Write-Host "根目錄:$root($(if ($devMode) { '開發機模式' } else { '安裝版模式:只檢查執行期會寫的位置' }))"
if ($onSystemDrive) { Write-Host "  [問題] 位於系統磁碟 $sysDrive — 守則:本機資料放 D: 或 G:;放系統磁碟須經訓練負責人同意" -ForegroundColor Red }
else { Write-Host "  [正常] 不在系統磁碟($sysDrive)" -ForegroundColor Green }
if ($fileSystem) {
    if ($fsBad) { Write-Host "  [問題] 檔案系統 $fileSystem(不是 NTFS):pnpm install 會失敗(不支援目錄連結),網頁無法在這顆磁碟建置;開發用請改為 NTFS" -ForegroundColor Red }
    else { Write-Host "  [正常] 檔案系統 $fileSystem$(if ($driveType) { ";磁碟種類 $driveType" })" -ForegroundColor Green }
    if ($driveType -eq 'Removable') { Write-Host '  [提醒] 抽取式磁碟:拔除前先執行 stop-all.ps1 並關閉 Simosa BRM 的 Edge 視窗;開發機建議用 setup-windows.ps1 -SessionOnly 或 tools\dev-shell.cmd' -ForegroundColor Yellow }
}

if ($devMode) {
    Write-Host ''
    Write-Host '環境變數(應指到專案內;未設定或指到專案外者,請在這個終端機先執行 .\tools\setup-windows.ps1 -SessionOnly,或改用 tools\dev-shell.cmd 開啟的視窗):'
    foreach ($r in $envRows) {
        $mark = if ($r.insideProject) { '[正常]' } else { '[問題]' }
        $color = if ($r.insideProject) { 'Green' } else { 'Yellow' }
        $shown = if ($r.note) { $r.note } elseif ($r.value) { $r.value } else { '(未設定)' }
        Write-Host ("  {0} {1,-26} {2}" -f $mark, $r.name, $shown) -ForegroundColor $color
    }
}

Write-Host ''
Write-Host "各工具預設寫到系統磁碟使用者設定檔的位置($($Since.ToString('yyyy-MM-dd HH:mm')) 之後有寫入者為「有新資料」):"
foreach ($r in $leakRows) {
    if (-not $r.exists) { Write-Host ("  [正常] {0,-30} 不存在" -f $r.what) -ForegroundColor Green; continue }
    $when = if ($r.lastWrite) { $r.lastWrite.ToString('yyyy-MM-dd HH:mm') } else { '(空)' }
    if ($r.newData -and $r.consented) { Write-Host ("  [已同意] {0,-28} {1} MB,最近寫入 {2}(SIMOSA_SYSTEM_TEMP=1)— {3}" -f $r.what, $r.sizeMb, $when, $r.path) -ForegroundColor DarkGray }
    elseif ($r.newData) { Write-Host ("  [問題] {0,-30} 有新資料:{1} MB,最近寫入 {2} — {3}" -f $r.what, $r.sizeMb, $when, $r.path) -ForegroundColor Red }
    else { Write-Host ("  [舊資料] {0,-28} {1} MB,最近寫入 {2}(早於 -Since,非本次造成)— {3}" -f $r.what, $r.sizeMb, $when, $r.path) -ForegroundColor DarkGray }
}

Write-Host ''
Write-Host '專案內的暫存與瀏覽器端資料(僅供參考;build\tmp 在所有程序結束後可以整個刪除):'
foreach ($r in $insideRows) {
    $shown = if ($null -eq $r.sizeMb) { '不存在' } else { "$($r.sizeMb) MB" }
    Write-Host ("  {0,-22} {1}" -f $r.path, $shown)
}

Write-Host ''
Write-Host '不在本檢查範圍(依守則屬應用程式或 Windows 自身,可在系統磁碟;完整清單見 docs\ops\本機資料位置.md 第 3 節):'
Write-Host '  .NET SDK/執行階段、Node.js、corepack 下載的 pnpm 本體、Git、uv、Microsoft Edge / WebView2 Runtime 的程式檔;登錄與防火牆規則;'
Write-Host '  NuGet 與 Git 的使用者設定檔(%APPDATA%\NuGet\NuGet.Config、%USERPROFILE%\.gitconfig);PowerShell 的指令歷史;顯示卡驅動程式自己的著色器快取。'
Write-Host '  也不檢查:手動用其他瀏覽器開啟網頁所留下的資料與下載檔(請一律用啟動腳本開啟)。'
if ($exit -eq 0) { Write-Host '結果:沒有發現問題。' -ForegroundColor Green }
else { Write-Host "結果:有 $($envBad.Count) 個環境變數未指到專案內、$($leakBad.Count) 個系統磁碟位置有新資料$(if ($fsBad) { ';專案磁碟不是 NTFS' })$(if ($onSystemDrive) { ';根目錄位於系統磁碟' })。" -ForegroundColor Yellow }
exit $exit
