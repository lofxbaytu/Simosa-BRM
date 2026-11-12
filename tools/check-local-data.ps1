<#
.SYNOPSIS
  本機資料位置檢查(Windows 開發機 / 船上筆電 / 教官筆電):確認資料都在專案(安裝)根目錄所在的 D: 或 G:,沒有落到 C:。
.DESCRIPTION
  守則(CLAUDE.md、docs\ops\本機資料位置.md):本機資料一律放 D: 或 G:;C: 只放應用程式本身;
  放在隨身碟會嚴重拖慢進度的項目,須先取得訓練負責人同意才可放 C:。
  本腳本只讀取、不修改任何東西:
    1) 專案根目錄所在磁碟(是否為系統磁碟);
    2) 目前工作階段的環境變數是否都指到專案內(快取、暫存);
    3) 各工具「預設」會寫到 C:\Users\… 的位置是否存在、大小、最近寫入時間(-Since 之後有寫入者標為「有新資料」)。
  第 3 項的資料夾可能是其他專案或安裝本軟體之前留下的;以「最近寫入時間」判斷是否為本專案造成。
.PARAMETER Since
  只把這個時間之後有寫入的 C: 位置視為問題(預設:24 小時前)。例如開始工作前記下時間,收工時以 -Since 該時間執行。
.PARAMETER Json
  以 JSON 輸出(供自動化使用)。
.EXAMPLE
  .\tools\check-local-data.ps1
  .\tools\check-local-data.ps1 -Since '2026-11-12 09:00'
  結束碼:0 = 沒有發現問題;1 = 有項目需要處理(環境變數未指到專案內,或 C: 上有新資料);2 = 專案位於系統磁碟。
#>
[CmdletBinding()]
param([datetime]$Since = (Get-Date).AddHours(-24), [switch]$Json)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sysDrive = $env:SystemDrive
$onSystemDrive = [bool]($sysDrive -and $root.StartsWith($sysDrive, [System.StringComparison]::OrdinalIgnoreCase))

function Test-UnderRoot([string]$Path) {
    if (-not $Path) { return $false }
    try { $full = [System.IO.Path]::GetFullPath($Path) } catch { return $false }
    $r = $root.TrimEnd('\', '/')
    return ($full.Equals($r, [System.StringComparison]::OrdinalIgnoreCase) -or
            $full.StartsWith($r + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase))
}

# ---- 2) 環境變數:應指到專案內(tools\setup-windows.ps1 設定;TEMP/TMP 只在執行過 setup-windows.ps1 的工作階段內生效) ----
$envNames = @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_PLUGINS_CACHE_PATH', 'NUGET_SCRATCH', 'DOTNET_CLI_HOME', 'PNPM_HOME',
              'COREPACK_HOME', 'npm_config_cache', 'UV_CACHE_DIR', 'UV_PYTHON_INSTALL_DIR', 'UV_TOOL_DIR', 'UV_TOOL_BIN_DIR', 'UV_PYTHON_BIN_DIR',
              'PLAYWRIGHT_BROWSERS_PATH', 'TEMP', 'TMP')
$envRows = @()
foreach ($n in $envNames) {
    $v = [Environment]::GetEnvironmentVariable($n)
    $ok = Test-UnderRoot $v
    $envRows += [pscustomobject]@{ name = $n; value = $v; insideProject = $ok }
}

# ---- 3) 各工具預設寫到使用者設定檔(C:\Users\…)的位置 ----
function Join-Base([string]$Base, [string]$Rel) { if ($Base) { return (Join-Path $Base $Rel) } else { return $null } }
$candidates = @(
    @{ what = 'NuGet 套件(預設位置)';            path = (Join-Base $env:USERPROFILE '.nuget\packages') },
    @{ what = 'NuGet HTTP 快取';                    path = (Join-Base $env:LOCALAPPDATA 'NuGet\v3-cache') },
    @{ what = 'NuGet 外掛快取';                     path = (Join-Base $env:LOCALAPPDATA 'NuGet\plugins-cache') },
    @{ what = '.NET CLI 家目錄';                    path = (Join-Base $env:USERPROFILE '.dotnet') },
    @{ what = 'pnpm 套件庫';                        path = (Join-Base $env:LOCALAPPDATA 'pnpm') },
    @{ what = 'pnpm 快取';                          path = (Join-Base $env:LOCALAPPDATA 'pnpm-cache') },
    @{ what = 'npm 快取';                           path = (Join-Base $env:LOCALAPPDATA 'npm-cache') },
    @{ what = 'corepack(pnpm 本體)';              path = (Join-Base $env:LOCALAPPDATA 'node\corepack') },
    @{ what = 'uv 快取';                            path = (Join-Base $env:LOCALAPPDATA 'uv') },
    @{ what = 'uv 受管 Python / 工具';              path = (Join-Base $env:APPDATA 'uv') },
    @{ what = 'Playwright 瀏覽器';                  path = (Join-Base $env:LOCALAPPDATA 'ms-playwright') },
    @{ what = 'matplotlib 設定與字型快取';          path = (Join-Base $env:USERPROFILE '.matplotlib') },
    @{ what = 'Godot 編輯器與使用者資料';           path = (Join-Base $env:APPDATA 'Godot') },
    @{ what = 'Simosa BRM 視景使用者資料(Godot)'; path = (Join-Base $env:APPDATA 'SimosaBRM') }
)
$leakRows = @()
foreach ($c in $candidates) {
    $p = $c.path
    $row = [pscustomobject]@{ what = $c.what; path = $p; exists = $false; sizeMb = 0.0; lastWrite = $null; newData = $false }
    if ($p -and (Test-Path $p) -and -not (Test-UnderRoot $p)) {
        $row.exists = $true
        $files = @(Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue)
        if ($files.Count -gt 0) {
            $row.sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
            $row.lastWrite = ($files | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
            $row.newData = ($row.lastWrite -gt $Since)
        }
    }
    $leakRows += $row
}

$envBad = @($envRows | Where-Object { -not $_.insideProject })
$leakBad = @($leakRows | Where-Object { $_.newData })
$exit = 0
if ($envBad.Count -gt 0 -or $leakBad.Count -gt 0) { $exit = 1 }
if ($onSystemDrive) { $exit = 2 }

if ($Json) {
    [pscustomobject]@{ root = $root; systemDrive = $sysDrive; rootOnSystemDrive = $onSystemDrive; since = $Since; environment = $envRows; defaultLocations = $leakRows; exitCode = $exit } | ConvertTo-Json -Depth 4
    exit $exit
}

Write-Host "專案(安裝)根目錄:$root"
if ($onSystemDrive) { Write-Host "  [問題] 位於系統磁碟 $sysDrive — 守則:本機資料放 D: 或 G:;放系統磁碟須經訓練負責人同意" -ForegroundColor Red }
else { Write-Host "  [正常] 不在系統磁碟($sysDrive)" -ForegroundColor Green }

Write-Host ''
Write-Host '環境變數(應指到專案內;未設定或指到專案外者,請在這個終端機先執行 .\tools\setup-windows.ps1 -SessionOnly):'
foreach ($r in $envRows) {
    $mark = if ($r.insideProject) { '[正常]' } else { '[問題]' }
    $color = if ($r.insideProject) { 'Green' } else { 'Yellow' }
    $shown = if ($r.value) { $r.value } else { '(未設定)' }
    Write-Host ("  {0} {1,-26} {2}" -f $mark, $r.name, $shown) -ForegroundColor $color
}

Write-Host ''
Write-Host "各工具預設寫到 C:\Users 的位置($($Since.ToString('yyyy-MM-dd HH:mm')) 之後有寫入者為「有新資料」):"
foreach ($r in $leakRows) {
    if (-not $r.exists) { Write-Host ("  [正常] {0,-30} 不存在" -f $r.what) -ForegroundColor Green; continue }
    $when = if ($r.lastWrite) { $r.lastWrite.ToString('yyyy-MM-dd HH:mm') } else { '(空)' }
    if ($r.newData) { Write-Host ("  [問題] {0,-30} 有新資料:{1} MB,最近寫入 {2} — {3}" -f $r.what, $r.sizeMb, $when, $r.path) -ForegroundColor Red }
    else { Write-Host ("  [舊資料] {0,-28} {1} MB,最近寫入 {2}(早於 -Since,非本次造成)— {3}" -f $r.what, $r.sizeMb, $when, $r.path) -ForegroundColor DarkGray }
}

Write-Host ''
Write-Host '不在本檢查範圍(依守則屬應用程式或 Windows 自身,可在 C:):.NET SDK/執行階段、Node.js、Git、uv、Microsoft Edge / WebView2 Runtime 的程式檔;'
Write-Host '  登錄與防火牆規則;%APPDATA%\NuGet\NuGet.Config(NuGet 的使用者設定檔,數 KB)。詳見 docs\ops\本機資料位置.md。'
if ($exit -eq 0) { Write-Host '結果:沒有發現問題。' -ForegroundColor Green }
else { Write-Host "結果:有 $($envBad.Count) 個環境變數未指到專案內、$($leakBad.Count) 個 C: 位置有新資料$(if ($onSystemDrive) { ';專案位於系統磁碟' })。" -ForegroundColor Yellow }
exit $exit
