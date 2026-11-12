# Simosa BRM 開發環境設定(Windows 11)
# 用途:把所有工具的快取、暫存與輸出固定在專案資料夾之內(專案放在 D: 或 G:,例如 D:\Simosa BRM、G:\Simosa BRM),避免寫到 C:\。
#   守則(CLAUDE.md):本機資料一律放 D: 或 G:;C: 只放應用程式本身;放在隨身碟會嚴重拖慢進度的項目,須先取得訓練負責人同意才可放 C:。
# 執行:開 PowerShell,cd 到專案資料夾後執行 .\tools\setup-windows.ps1(寫入使用者層級環境變數;之後開的終端機都生效)
#   -SessionOnly      :只設定目前這個 PowerShell 工作階段(不寫使用者層級環境變數);每次開新終端機先執行一次即可
#   -SystemTemp       :不把 TEMP/TMP 導到專案內(暫存檔留在 Windows 預設的 C:\Users\…\AppData\Local\Temp;須經同意才使用)
#   -AllowSystemDrive :專案位於系統磁碟(通常 C:)時仍繼續(預設拒絕;須經同意才使用)
#   -InstallPwsh:另把 PowerShell 7(固定版 $PwshVersion;啟動腳本測試 tools\launch\tests\launch-common.tests.ps1 需 pwsh 7,Windows 內建的 5.1 不夠)
#                 以 dotnet 全域工具裝到 .cache\dotnet-tools\(NuGet 走 nuget.config 與 .cache\nuget;不寫 C:\;開發機用,船上不需要)
param([switch]$InstallPwsh, [string]$PwshVersion = '7.4.22', [switch]$SessionOnly, [switch]$SystemTemp, [switch]$AllowSystemDrive)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "專案根目錄:$root"
# 專案不得放在系統磁碟(通常 C:):本機資料放 D: 或 G:;確實要放系統磁碟須先取得同意並加 -AllowSystemDrive
$sysDrive = $env:SystemDrive
if ($sysDrive -and $root.StartsWith($sysDrive, [System.StringComparison]::OrdinalIgnoreCase)) {
    if (-not $AllowSystemDrive) { throw "專案位於系統磁碟 $sysDrive($root)。守則:本機資料放 D: 或 G:;確實要放在 $sysDrive 須先取得訓練負責人同意,再加 -AllowSystemDrive 執行。" }
    Write-Warning "專案位於系統磁碟 $sysDrive(已加 -AllowSystemDrive):建置輸出、快取、紀錄都會寫在 $sysDrive。"
}
$drive = Split-Path -Qualifier $root   # 專案所在磁碟(D: 或 G:);下方安裝建議以它為準
$cache = Join-Path $root '.cache'
New-Item -ItemType Directory -Force -Path (Join-Path $cache 'nuget'), (Join-Path $cache 'nuget\http'), (Join-Path $cache 'nuget\plugins'), (Join-Path $cache 'nuget\scratch'), (Join-Path $cache 'corepack'), (Join-Path $cache 'npm'), (Join-Path $root 'build\tmp'), (Join-Path $cache 'pnpm'), (Join-Path $cache 'uv'), (Join-Path $cache 'uv-python'), (Join-Path $cache 'uv-tools'), (Join-Path $cache 'uv-bin'), (Join-Path $cache 'playwright'), (Join-Path $cache 'dotnet-home'), (Join-Path $cache 'dotnet-tools'), (Join-Path $root 'build'), (Join-Path $root 'data\records') | Out-Null

# 使用者層級環境變數(只影響本機帳號;各工具因此把快取放在專案內)
# uv:套件快取(UV_CACHE_DIR)之外,沒有 Python 3.12+ 時 uv sync/run 會自動下載受管 CPython,
# 預設裝到 %APPDATA%\uv\python(C:\),而且只能用環境變數改(uv.toml/pyproject 沒有對應鍵),
# 故一併把受管 Python、uv tool 環境與可執行檔捷徑目錄指到 .cache\ 之下(與快取分開,uv cache clean 不會清掉 Python)。
# UV_CACHE_DIR 以絕對路徑設定也避免 uv.toml 的相對 cache-dir 依執行目錄而異(uv 以 cwd 解析相對路徑)。
$vars = @{
  'NUGET_PACKAGES'           = (Join-Path $cache 'nuget\packages')
  # NuGet 的 HTTP 快取、外掛快取與暫存只能用環境變數改(nuget.config 無對應鍵);預設在 %LOCALAPPDATA%\NuGet 與 %TEMP%(C:\)
  'NUGET_HTTP_CACHE_PATH'    = (Join-Path $cache 'nuget\http')
  'NUGET_PLUGINS_CACHE_PATH' = (Join-Path $cache 'nuget\plugins')
  'NUGET_SCRATCH'            = (Join-Path $cache 'nuget\scratch')
  'DOTNET_CLI_HOME'          = (Join-Path $cache 'dotnet-home')
  'DOTNET_CLI_TELEMETRY_OPTOUT' = '1'
  'PNPM_HOME'                = (Join-Path $cache 'pnpm\home')
  # corepack 下載的 pnpm 本體(預設 %LOCALAPPDATA%\node\corepack)與 npm/npx 快取(預設 %LOCALAPPDATA%\npm-cache)
  'COREPACK_HOME'            = (Join-Path $cache 'corepack')
  'npm_config_cache'         = (Join-Path $cache 'npm')
  'UV_CACHE_DIR'             = (Join-Path $cache 'uv')
  'UV_PYTHON_INSTALL_DIR'    = (Join-Path $cache 'uv-python')
  'UV_TOOL_DIR'              = (Join-Path $cache 'uv-tools')
  'UV_TOOL_BIN_DIR'          = (Join-Path $cache 'uv-bin')
  'UV_PYTHON_BIN_DIR'        = (Join-Path $cache 'uv-bin')
  'PLAYWRIGHT_BROWSERS_PATH' = (Join-Path $cache 'playwright')
  'SIMOSA_BRM_ROOT'          = $root
}
foreach ($k in $vars.Keys) { if (-not $SessionOnly) { [Environment]::SetEnvironmentVariable($k, $vars[$k], 'User') }; Set-Item -Path "Env:$k" -Value $vars[$k] }
if ($SessionOnly) { Write-Host '已設定本工作階段的環境變數(未寫入使用者層級):' ($vars.Keys -join ', ') }
else { Write-Host '已設定使用者環境變數:' ($vars.Keys -join ', ') }

# 暫存檔(TEMP/TMP):只改目前這個 PowerShell 工作階段(與由它啟動的 dotnet / pnpm / uv / Playwright 等子程序)→ build\tmp。
# 不改使用者層級的 TEMP(會影響這個帳號的所有程式;隨身碟拔除時 Windows 其他程式會失敗)— 所以每次開新終端機要重新執行本腳本
#(可加 -SessionOnly),或由 tools\ 下的建置/啟動腳本自行設定(它們各自把 TEMP/TMP 指到 build\tmp)。
# -SystemTemp:暫存留在 Windows 預設位置(C:\Users\…\AppData\Local\Temp);放在隨身碟會明顯拖慢建置時,經同意才使用。
if ($SystemTemp) { Write-Warning "暫存檔留在系統預設位置($env:TEMP);須經訓練負責人同意。" }
else {
    $tmp = Join-Path $root 'build\tmp'
    $env:TEMP = $tmp; $env:TMP = $tmp
    Write-Host "本工作階段的暫存檔(TEMP/TMP)→ $tmp(每次開新終端機先執行 .\tools\setup-windows.ps1 -SessionOnly)"
}

# 檢查工具
function Check($name, $cmd) { try { $v = & $cmd 2>$null | Select-Object -First 1; Write-Host ("  {0,-12} {1}" -f $name, $v) } catch { Write-Host ("  {0,-12} 未安裝" -f $name) -ForegroundColor Yellow } }
Write-Host '工具版本:'
Check 'dotnet'  { dotnet --version }
Check 'node'    { node --version }
Check 'pnpm'    { pnpm --version }
Check 'uv'      { uv --version }
Check 'git'     { git --version }
Check 'git-lfs' { git lfs version }
# PowerShell 7:優先看專案內的 dotnet 全域工具版(.cache\dotnet-tools\pwsh.exe),其次 PATH
$pwshTool = Join-Path $cache 'dotnet-tools\pwsh.exe'
Check 'pwsh'    { if (Test-Path $pwshTool) { & $pwshTool -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' } else { pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' } }
Check 'ISCC'    { $c = Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($c) { $c.Source } elseif (Test-Path (Join-Path $cache 'innosetup\ISCC.exe')) { Join-Path $cache 'innosetup\ISCC.exe' } else { throw 'none' } }

# PowerShell 7 以 dotnet 全域工具安裝到專案內(tools\run-ps-tests.sh 與 tools\launch\tests\launch-common.tests.ps1 用;固定版號,NuGet 快取在 .cache\nuget)
if ($InstallPwsh) {
    if (-not (Test-Path $pwshTool)) {
        Write-Host "安裝 PowerShell $PwshVersion → $(Join-Path $cache 'dotnet-tools')"
        $env:DOTNET_CLI_HOME = $vars['DOTNET_CLI_HOME']; $env:NUGET_PACKAGES = $vars['NUGET_PACKAGES']
        & dotnet tool install PowerShell --tool-path (Join-Path $cache 'dotnet-tools') --version $PwshVersion --configfile (Join-Path $root 'nuget.config')
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool install PowerShell 失敗(結束碼 $LASTEXITCODE)" }
    } else { Write-Host "已有 $pwshTool" }
    Write-Host "  執行測試:& '$pwshTool' -NoProfile -File tools\launch\tests\launch-common.tests.ps1(或 Git Bash:bash tools/run-ps-tests.sh)"
}

Write-Host ''
Write-Host "安裝建議(應用程式本身可裝在 C:;要裝在專案磁碟時可指定 $drive\Tools\):"
Write-Host "  .NET 8 SDK      : winget install Microsoft.DotNet.SDK.8  (安裝程式路徑可選 $drive\Tools\dotnet)"
Write-Host '  Node.js 22 LTS  : winget install OpenJS.NodeJS.LTS;  然後 corepack enable pnpm'
Write-Host '  uv              : winget install astral-sh.uv  (Python 3.12+ 不必另裝:uv sync 會下載到 .cache\uv-python)'
Write-Host '  Git + LFS       : winget install Git.Git; git lfs install'
Write-Host "  Godot 4.5 .NET  : 解壓到 .cache\godot\<版本>\(旁邊放 _sc_ 空檔即為自含模式)或 $drive\Tools\;專案位於 src\Visual.*"
Write-Host "  PowerShell 7     : .\tools\setup-windows.ps1 -InstallPwsh(dotnet tool,固定版 $PwshVersion,裝到 .cache\dotnet-tools;啟動腳本測試用,船上不需要)"
Write-Host "  Inno Setup 6.3+  : 安裝到 $drive\Tools\Inno Setup 6\ 或可攜版放 .cache\innosetup\ISCC.exe(tools\build-release.ps1 -Installer 產生 Windows 安裝程式;tools\installer\README.md)"
Write-Host ''
Write-Host "完成。之後在此資料夾執行:dotnet build、pnpm install、uv sync 時,快取、暫存與輸出都會留在 $root 內。"
Write-Host '仍會用到 C: 的只有應用程式本身(.NET SDK/執行階段、Node.js、Git、Microsoft Edge/WebView2 Runtime 等安裝在 Program Files 者)與 Windows 自身的設定(登錄、防火牆規則)。'
